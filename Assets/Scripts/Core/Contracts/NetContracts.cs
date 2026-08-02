// -----------------------------------------------------------------------------
// Vigil — networking contracts.
//
// These are transport-agnostic on purpose. The shipping implementation is
// Netcode for GameObjects over Unity Transport, but Core must not reference NGO:
// AI, gameplay and audio all need to ask "am I the authority?" and "what tick is
// the server on?", and none of them should be unable to compile or unit-test
// without a networking package present.
//
// AUTHORITY MODEL: server-authoritative, full stop. In a game whose entire
// tension rests on not knowing where the monster is, a client-authoritative or
// peer-trusted model is not a performance trade-off — it is the game being over.
// Clients send input intent; the server simulates AI, damage, doors, item pickups
// and line of sight, and replicates results. Nothing about the antagonist's
// position is sent to a client that should not be able to perceive it (see
// IInterestManager).
// -----------------------------------------------------------------------------

namespace Vigil.Core.Contracts
{
    public enum SessionMode : byte
    {
        /// <summary>No networking. Single-player / editor testing.</summary>
        Offline = 0,

        /// <summary>Listen server: this process is both server and a player.</summary>
        Host = 1,

        /// <summary>Pure client.</summary>
        Client = 2,

        /// <summary>Headless authoritative server with no local player.</summary>
        DedicatedServer = 3
    }

    public enum ConnectionState : byte
    {
        Disconnected = 0,
        Authenticating = 1,
        Allocating = 2,
        Connecting = 3,
        Connected = 4,
        Reconnecting = 5,
        Failed = 6
    }

    public enum DisconnectReason : byte
    {
        None = 0,
        UserRequested = 1,
        Timeout = 2,
        ServerShutdown = 3,
        VersionMismatch = 4,
        SessionFull = 5,
        Kicked = 6,
        AuthenticationFailed = 7,
        TransportError = 8
    }

    /// <summary>
    /// Authority query. Every gameplay system takes this rather than reading
    /// NetworkManager.Singleton directly, so systems remain testable headlessly
    /// and behave correctly under Multiplayer Play Mode virtual players.
    /// </summary>
    public interface INetworkAuthority
    {
        SessionMode Mode { get; }

        /// <summary>True on host and dedicated server. Gate ALL simulation on this.</summary>
        bool IsServer { get; }

        bool IsClient { get; }
        bool IsHost { get; }

        /// <summary>Local client id. 0 on a dedicated server.</summary>
        ulong LocalClientId { get; }

        /// <summary>Authoritative server tick, the timeline all replicated state is stamped in.</summary>
        int ServerTick { get; }

        /// <summary>Round-trip time to the server in seconds. 0 on server.</summary>
        float RoundTripTime { get; }

        /// <summary>Connected client count. Server only; 0 elsewhere.</summary>
        int ConnectedClientCount { get; }
    }

    /// <summary>
    /// Drives session lifecycle — auth, allocation, transport configuration and
    /// connect/disconnect. Implemented once per backend (Relay, direct IP, dedicated).
    /// </summary>
    public interface ISessionDriver
    {
        ConnectionState State { get; }
        SessionMode Mode { get; }

        /// <summary>Join code / address shown to players. Empty when not hosting.</summary>
        string JoinCode { get; }

        System.Threading.Tasks.Task<bool> StartHostAsync(SessionOptions options);
        System.Threading.Tasks.Task<bool> StartClientAsync(SessionOptions options);
        System.Threading.Tasks.Task<bool> StartDedicatedServerAsync(SessionOptions options);

        void Shutdown(DisconnectReason reason = DisconnectReason.UserRequested);

        event System.Action<ConnectionState> OnStateChanged;
        event System.Action<DisconnectReason> OnDisconnected;
    }

    public struct SessionOptions
    {
        public string SessionName;
        public int MaxPlayers;
        public bool IsPrivate;

        /// <summary>Join code (client) or direct address (LAN/dedicated).</summary>
        public string JoinCodeOrAddress;

        /// <summary>
        /// Address the SERVER binds its listen socket to. Distinct from
        /// <see cref="JoinCodeOrAddress"/>, which is what a client dials.
        ///
        /// <para>Usually "0.0.0.0". Some hosts require something else: Fly.io routes
        /// UDP only to the special <c>fly-global-services</c> address, and a server
        /// bound to 0.0.0.0 there listens forever and never receives a packet, with
        /// no error to explain why.</para>
        /// </summary>
        public string BindAddress;

        public ushort Port;

        /// <summary>Seed for all deterministic systems this session. Replicated to clients.</summary>
        public uint SessionSeed;

        /// <summary>Rejects clients whose build hash differs. Non-negotiable for a shipping title.</summary>
        public string BuildHash;

        public static SessionOptions Default => new SessionOptions
        {
            SessionName = "Vigil Session",
            MaxPlayers = 4,
            IsPrivate = false,
            JoinCodeOrAddress = "127.0.0.1",
            BindAddress = "0.0.0.0",
            Port = 7777,
            SessionSeed = 0u,
            BuildHash = string.Empty
        };
    }

    /// <summary>
    /// Controls what each client is even TOLD about. Beyond bandwidth, this is an
    /// anti-cheat and a design requirement: a client that receives the entity's
    /// transform every tick can be made to render it through walls. The entity is
    /// only replicated to clients that could plausibly perceive it.
    /// </summary>
    public interface IInterestManager
    {
        /// <summary>Server-only. Recomputes visibility sets. Called on a slow tick.</summary>
        void Evaluate(in Vigil.Core.Simulation.SimTime time);

        /// <summary>Whether an entity is currently replicated to a client.</summary>
        bool IsVisibleTo(ulong entityId, ulong clientId);

        /// <summary>Forces an entity visible to a client for a duration (scripted reveals).</summary>
        void ForceVisible(ulong entityId, ulong clientId, float seconds);

        /// <summary>Entities currently replicated to a client (telemetry).</summary>
        int GetVisibleCount(ulong clientId);
    }

    /// <summary>Anything with a networked identity, independent of the transport.</summary>
    public interface INetEntity
    {
        ulong EntityId { get; }
        bool IsSpawned { get; }
        ulong OwnerId { get; }
    }
}
