// -----------------------------------------------------------------------------
// Vigil — offline transport.
//
// A NetworkTransport that never touches a socket. It exists so the game can run
// in single player, and — the reason it was written — so it can run in a BROWSER.
//
// The problem it solves: every gameplay object here is a NetworkBehaviour, and
// NetworkBehaviours only initialise when a server spawns them. No server means no
// OnNetworkSpawn, which means no AI, no mission, no player. So "offline" cannot
// simply mean "don't start networking".
//
// The fix is to start a HOST whose transport does nothing. NGO's host routes the
// local client's messages internally and never asks the transport to deliver
// them, so with no remote clients a no-op transport is completely sufficient:
// every NetworkObject spawns, every OnNetworkSpawn fires, IsServer is true, and
// the whole simulation runs locally with zero network traffic.
//
// This is also the ONLY way the game runs on WebGL. UnityTransport there is
// client-only over WebSockets and cannot bind a listener, so StartHost fails on
// the real transport no matter how it is configured.
// -----------------------------------------------------------------------------

using System;
using Unity.Netcode;
using Vigil.Core.Diagnostics;

namespace Vigil.Net.Session
{
    public sealed class LoopbackTransport : NetworkTransport
    {
        bool _started;

        /// <summary>NGO reserves 0 for the server; the host's local client shares it.</summary>
        public override ulong ServerClientId => 0UL;

        public override void Initialize(NetworkManager networkManager = null)
        {
            _started = false;
        }

        public override bool StartServer()
        {
            _started = true;
            VLog.Info(LogCat.Net, "LoopbackTransport: offline server started (no socket).");
            return true;
        }

        /// <summary>
        /// Succeeds so host startup completes. There is no remote end to connect
        /// to; the host's local client is wired internally by NGO.
        /// </summary>
        public override bool StartClient()
        {
            _started = true;
            return true;
        }

        /// <summary>
        /// Discards everything. Nothing is listening, and in host mode NGO never
        /// routes the local client's traffic through here in the first place.
        /// </summary>
        public override void Send(ulong clientId, ArraySegment<byte> payload, NetworkDelivery networkDelivery)
        {
        }

        public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload, out float receiveTime)
        {
            clientId = ServerClientId;
            payload = default;
            receiveTime = 0f;
            return NetworkEvent.Nothing;
        }

        public override void DisconnectRemoteClient(ulong clientId)
        {
        }

        public override void DisconnectLocalClient()
        {
            _started = false;
        }

        public override ulong GetCurrentRtt(ulong clientId) => 0UL;

        public override void Shutdown()
        {
            _started = false;
        }

        public bool IsStarted => _started;
    }
}
