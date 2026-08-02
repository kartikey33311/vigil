// -----------------------------------------------------------------------------
// Vigil — session lifecycle.
//
// Three backends behind one interface: direct IP (LAN, playtests, automated
// tests), Relay (public matchmaking, NAT traversal), and dedicated server.
//
// The Relay implementation lives in a SEPARATE file behind #if VIGIL_UGS_RELAY
// and is reached through IRelayBackend. That is deliberate risk isolation: online
// service SDKs change signatures between minor versions, and a compile break in
// matchmaking must never stop the team from building and playing the game over
// LAN. If the relay package is absent or its API moves, this file still compiles
// and direct IP still works.
//
// The other rule enforced here: a failed join must NEVER leave the game
// half-connected. Every async path catches, tears down, and reports Failed. The
// "it says I'm in the lobby but nothing works" bug class comes entirely from
// partial teardown after a failed connect.
// -----------------------------------------------------------------------------

using System;
using System.Text;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;

namespace Vigil.Net.Session
{
    /// <summary>
    /// Pluggable relay/matchmaking backend. Implemented against UGS when the
    /// packages are present; a no-op stub otherwise.
    /// </summary>
    public interface IRelayBackend
    {
        bool IsAvailable { get; }

        /// <summary>Signs in anonymously if required. Returns false on failure.</summary>
        Task<bool> InitialiseAsync();

        /// <summary>Allocates a relay and configures the transport. Returns the join code.</summary>
        Task<string> HostAsync(UnityTransport transport, int maxPlayers);

        /// <summary>Joins an existing allocation and configures the transport.</summary>
        Task<bool> JoinAsync(UnityTransport transport, string joinCode);
    }

    /// <summary>Used when no online services are present. Direct IP only.</summary>
    public sealed class NullRelayBackend : IRelayBackend
    {
        public bool IsAvailable => false;
        public Task<bool> InitialiseAsync() => Task.FromResult(false);
        public Task<string> HostAsync(UnityTransport transport, int maxPlayers) => Task.FromResult(string.Empty);
        public Task<bool> JoinAsync(UnityTransport transport, string joinCode) => Task.FromResult(false);
    }

    public sealed class SessionDriver : ISessionDriver
    {
        readonly NetworkAuthority _authority;
        readonly IRelayBackend _relay;
        readonly string _buildHash;

        ConnectionState _state = ConnectionState.Disconnected;
        SessionOptions _options = SessionOptions.Default;

        public ConnectionState State => _state;
        public SessionMode Mode { get; private set; } = SessionMode.Offline;
        public string JoinCode { get; private set; } = string.Empty;

        public event Action<ConnectionState> OnStateChanged;
        public event Action<DisconnectReason> OnDisconnected;

        /// <summary>Build identity clients are validated against. Surfaced in the lobby UI.</summary>
        public string BuildHash => _buildHash;

        public SessionDriver(NetworkAuthority authority, IRelayBackend relay = null, string buildHash = null)
        {
            _authority = authority;
            _relay = relay ?? new NullRelayBackend();
            _buildHash = string.IsNullOrEmpty(buildHash) ? ComputeBuildHash() : buildHash;
        }

        static string ComputeBuildHash()
        {
            // Version + platform is enough to catch the case that actually matters:
            // someone joining with a different build of the game. A content hash
            // would be stricter but would also reject a client with identical code
            // and a different texture, which is not worth blocking a playtest over.
            return $"{Application.version}-{Application.unityVersion}-{(int)Application.platform}";
        }

        void SetState(ConnectionState next)
        {
            if (_state == next) return;
            _state = next;
            VLog.Info(LogCat.Session, $"Session state -> {next}");
            OnStateChanged?.Invoke(next);
        }

        // =====================================================================
        // Host
        // =====================================================================

        public async Task<bool> StartHostAsync(SessionOptions options)
        {
            _options = options;

            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null)
            {
                VLog.Error(LogCat.Session, "StartHostAsync: no NetworkManager in the scene.");
                SetState(ConnectionState.Failed);
                return false;
            }

            try
            {
                UnityTransport transport = ResolveTransport(nm);
                if (transport == null) { SetState(ConnectionState.Failed); return false; }

                ConfigureApproval(nm);

                if (_relay.IsAvailable && !options.IsPrivate)
                {
                    SetState(ConnectionState.Authenticating);
                    if (!await _relay.InitialiseAsync())
                    {
                        VLog.Warn(LogCat.Session, "Relay init failed — falling back to direct IP.");
                        ConfigureDirect(transport, options);
                    }
                    else
                    {
                        SetState(ConnectionState.Allocating);
                        JoinCode = await _relay.HostAsync(transport, options.MaxPlayers);

                        if (string.IsNullOrEmpty(JoinCode))
                        {
                            VLog.Warn(LogCat.Session, "Relay allocation failed — falling back to direct IP.");
                            ConfigureDirect(transport, options);
                        }
                    }
                }
                else
                {
                    ConfigureDirect(transport, options);
                    JoinCode = $"{options.JoinCodeOrAddress}:{options.Port}";
                }

                SetState(ConnectionState.Connecting);

                if (!nm.StartHost())
                {
                    VLog.Error(LogCat.Session, "NetworkManager.StartHost returned false.");
                    Teardown(DisconnectReason.TransportError);
                    return false;
                }

                Mode = SessionMode.Host;
                _authority?.SetMode(SessionMode.Host);
                HookCallbacks(nm);
                SetState(ConnectionState.Connected);

                VLog.Info(LogCat.Session, $"Hosting '{options.SessionName}' — join code '{JoinCode}', seed {options.SessionSeed}.");
                return true;
            }
            catch (Exception ex)
            {
                VLog.Exception(LogCat.Session, ex);
                Teardown(DisconnectReason.TransportError);
                return false;
            }
        }

        // =====================================================================
        // Offline / single player
        // =====================================================================

        /// <summary>
        /// Starts a host on a transport that never opens a socket.
        ///
        /// <para>This is how single player works, and it is the ONLY mode that runs
        /// on WebGL: browsers cannot listen for connections, so the real transport
        /// can never host there. Swapping in <see cref="LoopbackTransport"/> lets
        /// every NetworkObject spawn and every OnNetworkSpawn fire, so the AI,
        /// mission and player all initialise exactly as they do online — with no
        /// networking underneath.</para>
        /// </summary>
        public Task<bool> StartOfflineAsync(SessionOptions options)
        {
            _options = options;

            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null)
            {
                VLog.Error(LogCat.Session, "StartOfflineAsync: no NetworkManager in the scene.");
                SetState(ConnectionState.Failed);
                return Task.FromResult(false);
            }

            try
            {
                LoopbackTransport loopback = nm.gameObject.GetComponent<LoopbackTransport>();
                if (loopback == null) loopback = nm.gameObject.AddComponent<LoopbackTransport>();

                nm.NetworkConfig.NetworkTransport = loopback;

                // No approval callback offline: there is no remote client to approve,
                // and leaving it armed only risks rejecting our own local player.
                nm.NetworkConfig.ConnectionApproval = false;
                nm.ConnectionApprovalCallback = null;

                SetState(ConnectionState.Connecting);

                if (!nm.StartHost())
                {
                    VLog.Error(LogCat.Session, "Offline StartHost failed.");
                    Teardown(DisconnectReason.TransportError);
                    return Task.FromResult(false);
                }

                Mode = SessionMode.Offline;
                _authority?.SetMode(SessionMode.Offline);
                JoinCode = string.Empty;
                SetState(ConnectionState.Connected);

                VLog.Info(LogCat.Session, $"Offline session started (seed {options.SessionSeed}).");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                VLog.Exception(LogCat.Session, ex);
                Teardown(DisconnectReason.TransportError);
                return Task.FromResult(false);
            }
        }

        // =====================================================================
        // Client
        // =====================================================================

        public async Task<bool> StartClientAsync(SessionOptions options)
        {
            _options = options;

            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null)
            {
                VLog.Error(LogCat.Session, "StartClientAsync: no NetworkManager in the scene.");
                SetState(ConnectionState.Failed);
                return false;
            }

            try
            {
                UnityTransport transport = ResolveTransport(nm);
                if (transport == null) { SetState(ConnectionState.Failed); return false; }

                string code = options.JoinCodeOrAddress ?? string.Empty;
                bool looksLikeAddress = code.Contains(".") || code.Contains(":") || code.Equals("localhost", StringComparison.OrdinalIgnoreCase);

                if (_relay.IsAvailable && !looksLikeAddress && !string.IsNullOrEmpty(code))
                {
                    SetState(ConnectionState.Authenticating);
                    if (!await _relay.InitialiseAsync())
                    {
                        SetState(ConnectionState.Failed);
                        return false;
                    }

                    SetState(ConnectionState.Allocating);
                    if (!await _relay.JoinAsync(transport, code))
                    {
                        VLog.Error(LogCat.Session, $"Failed to join relay allocation '{code}'.");
                        Teardown(DisconnectReason.TransportError);
                        return false;
                    }
                }
                else
                {
                    ConfigureDirect(transport, options);
                }

                // The client's identity payload. The server validates the build hash
                // before approving; see ConfigureApproval.
                nm.NetworkConfig.ConnectionData = Encoding.UTF8.GetBytes(_buildHash);

                SetState(ConnectionState.Connecting);

                if (!nm.StartClient())
                {
                    VLog.Error(LogCat.Session, "NetworkManager.StartClient returned false.");
                    Teardown(DisconnectReason.TransportError);
                    return false;
                }

                Mode = SessionMode.Client;
                _authority?.SetMode(SessionMode.Client);
                HookCallbacks(nm);
                SetState(ConnectionState.Connected);
                return true;
            }
            catch (Exception ex)
            {
                VLog.Exception(LogCat.Session, ex);
                Teardown(DisconnectReason.TransportError);
                return false;
            }
        }

        // =====================================================================
        // Dedicated server
        // =====================================================================

        public Task<bool> StartDedicatedServerAsync(SessionOptions options)
        {
            _options = options;

            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null)
            {
                VLog.Error(LogCat.Session, "StartDedicatedServerAsync: no NetworkManager in the scene.");
                SetState(ConnectionState.Failed);
                return Task.FromResult(false);
            }

            try
            {
                UnityTransport transport = ResolveTransport(nm);
                if (transport == null) { SetState(ConnectionState.Failed); return Task.FromResult(false); }

                // UnityTransport takes (address, port, listenAddress). For a server
                // the LISTEN address is what matters. Defaulting to 0.0.0.0 covers
                // LAN and most containers, but some hosts route UDP only to a
                // specific address (Fly.io uses fly-global-services) — binding
                // 0.0.0.0 there silently receives nothing at all.
                string bind = string.IsNullOrEmpty(options.BindAddress) ? "0.0.0.0" : options.BindAddress;
                transport.SetConnectionData(bind, options.Port, bind);

                VLog.Info(LogCat.Session, $"Server binding to {bind}:{options.Port}.");
                ConfigureApproval(nm);

                SetState(ConnectionState.Connecting);

                if (!nm.StartServer())
                {
                    VLog.Error(LogCat.Session, "NetworkManager.StartServer returned false.");
                    Teardown(DisconnectReason.TransportError);
                    return Task.FromResult(false);
                }

                Mode = SessionMode.DedicatedServer;
                _authority?.SetMode(SessionMode.DedicatedServer);
                HookCallbacks(nm);
                SetState(ConnectionState.Connected);

                VLog.Info(LogCat.Session, $"Dedicated server listening on :{options.Port} (seed {options.SessionSeed}).");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                VLog.Exception(LogCat.Session, ex);
                Teardown(DisconnectReason.TransportError);
                return Task.FromResult(false);
            }
        }

        // =====================================================================
        // Shared
        // =====================================================================

        static UnityTransport ResolveTransport(NetworkManager nm)
        {
            UnityTransport transport = nm.GetComponent<UnityTransport>();
            if (transport == null)
            {
                VLog.Error(LogCat.Session, "NetworkManager has no UnityTransport component.");
            }
            return transport;
        }

        static void ConfigureDirect(UnityTransport transport, SessionOptions options)
        {
            string address = string.IsNullOrEmpty(options.JoinCodeOrAddress) ? "127.0.0.1" : options.JoinCodeOrAddress;

            // Strip a port suffix if the user typed "1.2.3.4:7777".
            ushort port = options.Port;
            int colon = address.LastIndexOf(':');
            if (colon > 0 && ushort.TryParse(address.Substring(colon + 1), out ushort parsed))
            {
                port = parsed;
                address = address.Substring(0, colon);
            }

            transport.SetConnectionData(address, port);
        }

        void ConfigureApproval(NetworkManager nm)
        {
            nm.NetworkConfig.ConnectionApproval = true;
            nm.ConnectionApprovalCallback = ApprovalCheck;
        }

        void ApprovalCheck(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            NetworkManager nm = NetworkManager.Singleton;

            // Capacity.
            if (nm != null && nm.ConnectedClientsIds.Count >= _options.MaxPlayers)
            {
                response.Approved = false;
                response.Reason = DisconnectReason.SessionFull.ToString();
                VLog.Warn(LogCat.Session, $"Rejected client {request.ClientNetworkId}: session full.");
                return;
            }

            // Build identity. A client on a different build produces bugs that are
            // nearly impossible to diagnose from the symptom, so this is a hard
            // rejection rather than a warning.
            string incoming = request.Payload != null && request.Payload.Length > 0
                ? Encoding.UTF8.GetString(request.Payload)
                : string.Empty;

            bool isLocalHost = nm != null && request.ClientNetworkId == nm.LocalClientId;

            if (!isLocalHost && !string.Equals(incoming, _buildHash, StringComparison.Ordinal))
            {
                response.Approved = false;
                response.Reason = DisconnectReason.VersionMismatch.ToString();
                VLog.Warn(LogCat.Session, $"Rejected client {request.ClientNetworkId}: build mismatch ('{incoming}' vs '{_buildHash}').");
                return;
            }

            response.Approved = true;
            response.CreatePlayerObject = true;
        }

        void HookCallbacks(NetworkManager nm)
        {
            nm.OnClientDisconnectCallback -= HandleClientDisconnect;
            nm.OnClientDisconnectCallback += HandleClientDisconnect;
        }

        void HandleClientDisconnect(ulong clientId)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null) return;

            // Our own disconnect, as a client, means the session is over.
            if (!nm.IsServer && clientId == nm.LocalClientId)
            {
                DisconnectReason reason = ParseReason(nm.DisconnectReason);
                VLog.Warn(LogCat.Session, $"Disconnected: {reason} ({nm.DisconnectReason}).");
                Mode = SessionMode.Offline;
                _authority?.SetMode(SessionMode.Offline);
                SetState(ConnectionState.Disconnected);
                OnDisconnected?.Invoke(reason);
            }
            else
            {
                VLog.Info(LogCat.Session, $"Client {clientId} left.");
            }
        }

        static DisconnectReason ParseReason(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return DisconnectReason.Timeout;
            return Enum.TryParse(raw, out DisconnectReason parsed) ? parsed : DisconnectReason.TransportError;
        }

        public void Shutdown(DisconnectReason reason = DisconnectReason.UserRequested)
        {
            Teardown(reason);
        }

        void Teardown(DisconnectReason reason)
        {
            NetworkManager nm = NetworkManager.Singleton;

            if (nm != null)
            {
                nm.OnClientDisconnectCallback -= HandleClientDisconnect;
                nm.ConnectionApprovalCallback = null;

                if (nm.IsListening) nm.Shutdown();
            }

            Mode = SessionMode.Offline;
            _authority?.SetMode(SessionMode.Offline);
            JoinCode = string.Empty;

            SetState(reason == DisconnectReason.UserRequested ? ConnectionState.Disconnected : ConnectionState.Failed);
            OnDisconnected?.Invoke(reason);
        }
    }
}
