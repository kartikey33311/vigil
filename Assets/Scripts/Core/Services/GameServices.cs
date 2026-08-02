// -----------------------------------------------------------------------------
// Vigil — explicit service registry.
//
// Deliberately NOT a static singleton-per-system pattern. Horror gameplay runs
// host-and-client in the SAME process during playtests (and in Multiplayer Play
// Mode virtual players), so "one global instance per system" breaks down. Here,
// services are registered against a context that the bootstrap owns and tears
// down cleanly on session end — which also makes PlayMode tests deterministic
// instead of leaking state between test cases.
//
// Resolution is O(1) and allocation-free after registration.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Vigil.Core.Diagnostics;

namespace Vigil.Core.Services
{
    /// <summary>Implement to receive a deterministic teardown callback.</summary>
    public interface IGameService
    {
        void OnServiceDisposed();
    }

    /// <summary>
    /// A registry of long-lived systems. One instance per running session.
    /// </summary>
    public sealed class ServiceContext : IDisposable
    {
        readonly Dictionary<Type, object> _services = new Dictionary<Type, object>(32);
        readonly List<IGameService> _disposalOrder = new List<IGameService>(32);
        bool _disposed;

        public string Name { get; }

        public ServiceContext(string name = "default")
        {
            Name = name;
        }

        public void Register<T>(T service) where T : class
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ServiceContext));
            if (service == null) throw new ArgumentNullException(nameof(service));

            Type key = typeof(T);
            if (_services.ContainsKey(key))
            {
                VLog.Error(LogCat.Core, $"ServiceContext '{Name}': {key.Name} already registered; overwriting.");
            }

            _services[key] = service;
            if (service is IGameService disposable) _disposalOrder.Add(disposable);
        }

        public void Unregister<T>() where T : class
        {
            Type key = typeof(T);
            if (_services.TryGetValue(key, out object existing))
            {
                if (existing is IGameService d) _disposalOrder.Remove(d);
                _services.Remove(key);
            }
        }

        /// <summary>Resolve or throw. Use for hard dependencies.</summary>
        public T Get<T>() where T : class
        {
            if (_services.TryGetValue(typeof(T), out object service)) return (T)service;
            throw new InvalidOperationException(
                $"ServiceContext '{Name}': required service {typeof(T).Name} is not registered.");
        }

        /// <summary>Resolve or null. Use for optional dependencies.</summary>
        public T TryGet<T>() where T : class
        {
            return _services.TryGetValue(typeof(T), out object service) ? (T)service : null;
        }

        public bool Has<T>() where T : class => _services.ContainsKey(typeof(T));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Reverse registration order: dependents die before their dependencies.
            for (int i = _disposalOrder.Count - 1; i >= 0; i--)
            {
                try
                {
                    _disposalOrder[i].OnServiceDisposed();
                }
                catch (Exception ex)
                {
                    VLog.Exception(LogCat.Core, ex);
                }
            }

            _disposalOrder.Clear();
            _services.Clear();
        }
    }

    /// <summary>
    /// Ambient accessor for the active session context. Kept minimal on purpose:
    /// prefer constructor/serialized-field injection. This exists for MonoBehaviours,
    /// which Unity constructs for us and cannot be given constructor arguments.
    /// </summary>
    public static class Services
    {
        static ServiceContext _active;

        public static ServiceContext Active => _active;

        public static bool IsReady => _active != null;

        public static void SetActive(ServiceContext context)
        {
            _active = context;
            VLog.Info(LogCat.Core, $"Active service context -> {(context != null ? context.Name : "<none>")}");
        }

        public static T Get<T>() where T : class
        {
            if (_active == null)
            {
                throw new InvalidOperationException(
                    $"No active ServiceContext when resolving {typeof(T).Name}. " +
                    "Ensure the scene was entered through the Bootstrap scene.");
            }
            return _active.Get<T>();
        }

        public static T TryGet<T>() where T : class => _active?.TryGet<T>();
    }
}
