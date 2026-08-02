// -----------------------------------------------------------------------------
// Vigil — typed event bus.
//
// Decouples publishers from subscribers so that, for example, the audio system
// can react to "player downed" without the gameplay code holding an audio
// reference. Struct payloads, so publishing does not box or allocate.
//
// Storage is PER INSTANCE, not static. That matters: host and client can run in
// the same process (Multiplayer Play Mode virtual players, and the editor when
// hosting), and a static channel would silently cross-wire their events — the
// client would react to the server's simulation. Each ServiceContext owns its
// own bus.
//
// Subscriptions return an IDisposable token rather than requiring a matching
// Unsubscribe call. Forgotten unsubscribes are the classic source of "works
// until you return to the menu twice" bugs, because a stale delegate keeps a
// destroyed MonoBehaviour alive and it throws on the next publish.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Vigil.Core.Diagnostics;

namespace Vigil.Core.Events
{
    public interface IEventBus
    {
        IDisposable Subscribe<T>(Action<T> handler) where T : struct;
        void Publish<T>(in T evt) where T : struct;
        void Clear();
    }

    public sealed class EventBus : IEventBus
    {
        interface IChannel
        {
            void Clear();
            void Compact();
        }

        sealed class Channel<T> : IChannel where T : struct
        {
            public readonly List<Subscription<T>> Handlers = new List<Subscription<T>>(4);

            public void Clear()
            {
                for (int i = 0; i < Handlers.Count; i++) Handlers[i].Active = false;
                Handlers.Clear();
            }

            public void Compact()
            {
                for (int i = Handlers.Count - 1; i >= 0; i--)
                {
                    if (!Handlers[i].Active) Handlers.RemoveAt(i);
                }
            }
        }

        sealed class Subscription<T> : IDisposable where T : struct
        {
            public Action<T> Handler;
            public bool Active = true;

            public void Dispose()
            {
                Active = false;
                Handler = null;
            }
        }

        readonly Dictionary<Type, IChannel> _channels = new Dictionary<Type, IChannel>(32);

        /// <summary>Depth guard so a handler that republishes the same event cannot recurse forever.</summary>
        int _dispatchDepth;
        public int MaxDispatchDepth { get; set; } = 8;

        public IDisposable Subscribe<T>(Action<T> handler) where T : struct
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            Channel<T> channel = GetOrCreate<T>();
            Subscription<T> sub = new Subscription<T> { Handler = handler };
            channel.Handlers.Add(sub);
            return sub;
        }

        public void Publish<T>(in T evt) where T : struct
        {
            if (!_channels.TryGetValue(typeof(T), out IChannel raw)) return;
            Channel<T> channel = (Channel<T>)raw;

            List<Subscription<T>> list = channel.Handlers;
            if (list.Count == 0) return;

            if (_dispatchDepth >= MaxDispatchDepth)
            {
                VLog.Error(LogCat.Core,
                    $"EventBus: max dispatch depth ({MaxDispatchDepth}) exceeded publishing {typeof(T).Name} — " +
                    "a handler is almost certainly republishing its own event.");
                return;
            }

            _dispatchDepth++;
            try
            {
                // Snapshot the count so handlers subscribed *during* dispatch do not
                // receive the event they were registered by, but guard against the
                // list shrinking if a handler disposes others.
                int count = list.Count;
                for (int i = 0; i < count && i < list.Count; i++)
                {
                    Subscription<T> sub = list[i];
                    if (!sub.Active || sub.Handler == null) continue;

                    try
                    {
                        sub.Handler(evt);
                    }
                    catch (Exception ex)
                    {
                        // A throwing subscriber is unsubscribed rather than allowed to
                        // break the publish for everyone downstream of it.
                        VLog.Exception(LogCat.Core, ex);
                        sub.Dispose();
                    }
                }
            }
            finally
            {
                _dispatchDepth--;
                if (_dispatchDepth == 0) channel.Compact();
            }
        }

        Channel<T> GetOrCreate<T>() where T : struct
        {
            Type key = typeof(T);
            if (_channels.TryGetValue(key, out IChannel existing)) return (Channel<T>)existing;

            Channel<T> created = new Channel<T>();
            _channels[key] = created;
            return created;
        }

        public void Clear()
        {
            foreach (KeyValuePair<Type, IChannel> kv in _channels) kv.Value.Clear();
            _channels.Clear();
            _dispatchDepth = 0;
        }
    }

    // -------------------------------------------------------------------------
    // Canonical gameplay events
    // -------------------------------------------------------------------------

    public struct PlayerSpawnedEvent { public ulong PlayerId; public Unity.Mathematics.float3 Position; }
    public struct PlayerDownedEvent { public ulong PlayerId; public ulong InstigatorId; public Unity.Mathematics.float3 Position; }
    public struct PlayerRevivedEvent { public ulong PlayerId; public ulong RescuerId; }
    public struct PlayerKilledEvent { public ulong PlayerId; public ulong InstigatorId; }

    public struct ChaseStartedEvent { public ulong EntityId; public ulong TargetId; }
    public struct ChaseEndedEvent { public ulong EntityId; public ulong TargetId; public float DurationSeconds; }

    public struct AgentStateChangedEvent { public ulong EntityId; public int FromState; public int ToState; }
    public struct AwarenessChangedEvent { public ulong EntityId; public ulong TargetId; public Contracts.AwarenessLevel Level; }

    public struct DreadPhaseChangedEvent { public Contracts.DreadPhase From; public Contracts.DreadPhase To; }

    public struct ObjectiveCompletedEvent { public int ObjectiveId; public ulong CompletedBy; }
    public struct NoiseEmittedEvent { public Contracts.Stimulus Stimulus; }
    public struct CompositionChangedEvent { public ulong PlayerId; public float Composure; }
}
