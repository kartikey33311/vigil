// -----------------------------------------------------------------------------
// Vigil — authority adapter.
//
// Every gameplay system asks THIS "am I the authority?", never
// NetworkManager.Singleton directly. Three reasons, all of which have bitten
// real projects:
//
//   1. Testability. Systems remain constructible and tickable in an EditMode test
//      with no NetworkManager in the scene at all.
//   2. Offline mode. A designer iterating on AI in a single-player scene should
//      not have to boot networking to do it.
//   3. Multiplayer Play Mode. Host and client share a process; code that reaches
//      for a global singleton gets whichever one initialised last.
//
// Every accessor is null-safe by design. Throwing because networking has not
// started yet turns "not connected" into a crash, and "not connected" is a
// completely ordinary state.
// -----------------------------------------------------------------------------

using Unity.Netcode;
using Vigil.Core.Contracts;

namespace Vigil.Net.Session
{
    public sealed class NetworkAuthority : INetworkAuthority
    {
        SessionMode _mode = SessionMode.Offline;

        /// <summary>Set by the session driver as connection state changes.</summary>
        public void SetMode(SessionMode mode) => _mode = mode;

        public SessionMode Mode
        {
            get
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsListening) return SessionMode.Offline;

                if (nm.IsHost) return SessionMode.Host;
                if (nm.IsServer) return SessionMode.DedicatedServer;
                if (nm.IsClient) return SessionMode.Client;

                return _mode;
            }
        }

        /// <summary>
        /// True on host and dedicated server. This is the flag every simulation
        /// method gates on — and it returns TRUE in offline mode, because a
        /// single-player scene must still simulate.
        /// </summary>
        public bool IsServer
        {
            get
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsListening) return _mode == SessionMode.Offline;
                return nm.IsServer;
            }
        }

        public bool IsClient
        {
            get
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsListening) return _mode == SessionMode.Offline;
                return nm.IsClient;
            }
        }

        public bool IsHost
        {
            get
            {
                NetworkManager nm = NetworkManager.Singleton;
                return nm != null && nm.IsListening && nm.IsHost;
            }
        }

        public ulong LocalClientId
        {
            get
            {
                NetworkManager nm = NetworkManager.Singleton;
                return nm != null ? nm.LocalClientId : 0UL;
            }
        }

        public int ServerTick
        {
            get
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm == null || nm.NetworkTickSystem == null) return 0;
                return nm.NetworkTickSystem.ServerTime.Tick;
            }
        }

        public float RoundTripTime
        {
            get
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsListening || nm.IsServer) return 0f;

                // NGO reports RTT in milliseconds via the transport.
                ulong id = nm.LocalClientId;
                return nm.NetworkConfig != null && nm.NetworkConfig.NetworkTransport != null
                    ? nm.NetworkConfig.NetworkTransport.GetCurrentRtt(id) / 1000f
                    : 0f;
            }
        }

        public int ConnectedClientCount
        {
            get
            {
                NetworkManager nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsListening || !nm.IsServer) return 0;
                return nm.ConnectedClientsIds.Count;
            }
        }
    }
}
