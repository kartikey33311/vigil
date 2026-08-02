// -----------------------------------------------------------------------------
// Vigil — pooling.
//
// Two pools, for two different problems:
//
//   Pool<T>          plain managed objects (NavPath corridors, path requests,
//                    steering scratch). Prevents per-tick GC pressure in the AI loop.
//
//   PrefabPool       Unity GameObjects. Instantiate/Destroy on a NetworkObject is
//                    expensive AND, on the server, causes a spawn/despawn message
//                    per instance. Pooling gore decals, footstep audio emitters and
//                    projectiles keeps both frame time and bandwidth flat.
//
// Both are explicitly NOT thread-safe. All simulation runs on the main thread by
// design; a lock here would cost more than it saves and would hide accidental
// cross-thread access instead of surfacing it.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;
using Vigil.Core.Diagnostics;

namespace Vigil.Core.Pooling
{
    /// <summary>Implement to be notified on rent/return.</summary>
    public interface IPoolable
    {
        void OnRented();
        void OnReturned();
    }

    public sealed class Pool<T> where T : class
    {
        readonly Stack<T> _available;
        readonly Func<T> _factory;
        readonly Action<T> _onReturn;
        readonly int _maxRetained;

        /// <summary>Instances currently rented out. Non-zero at teardown means a leak.</summary>
        public int ActiveCount { get; private set; }

        public int AvailableCount => _available.Count;

        /// <summary>Peak simultaneous rentals — use it to size the prewarm.</summary>
        public int PeakActiveCount { get; private set; }

        public Pool(Func<T> factory, int prewarm = 0, int maxRetained = 256, Action<T> onReturn = null)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _onReturn = onReturn;
            _maxRetained = maxRetained;
            _available = new Stack<T>(Math.Max(4, prewarm));

            for (int i = 0; i < prewarm; i++) _available.Push(_factory());
        }

        public T Rent()
        {
            T item = _available.Count > 0 ? _available.Pop() : _factory();
            ActiveCount++;
            if (ActiveCount > PeakActiveCount) PeakActiveCount = ActiveCount;
            (item as IPoolable)?.OnRented();
            return item;
        }

        public void Return(T item)
        {
            if (item == null) return;

            (item as IPoolable)?.OnReturned();
            _onReturn?.Invoke(item);

            ActiveCount--;
            if (ActiveCount < 0)
            {
                VLog.Error(LogCat.Core, $"Pool<{typeof(T).Name}>: double-return detected.");
                ActiveCount = 0;
            }

            // Cap retention so a one-off spike does not pin memory for the session.
            if (_available.Count < _maxRetained) _available.Push(item);
        }

        public void Clear()
        {
            _available.Clear();
            ActiveCount = 0;
        }
    }

    /// <summary>
    /// GameObject pool. Pooled instances are deactivated and parented under a
    /// hidden root so they neither render nor tick while idle.
    /// </summary>
    public sealed class PrefabPool
    {
        readonly GameObject _prefab;
        readonly Transform _root;
        readonly Stack<GameObject> _available;
        readonly int _maxRetained;

        public int ActiveCount { get; private set; }
        public int AvailableCount => _available.Count;

        public PrefabPool(GameObject prefab, Transform root, int prewarm = 0, int maxRetained = 128)
        {
            _prefab = prefab != null ? prefab : throw new ArgumentNullException(nameof(prefab));
            _root = root;
            _maxRetained = maxRetained;
            _available = new Stack<GameObject>(Math.Max(4, prewarm));

            for (int i = 0; i < prewarm; i++)
            {
                GameObject go = UnityEngine.Object.Instantiate(_prefab, _root);
                go.SetActive(false);
                _available.Push(go);
            }
        }

        public GameObject Rent(Vector3 position, Quaternion rotation, Transform parent = null)
        {
            GameObject go;
            if (_available.Count > 0)
            {
                go = _available.Pop();
                // A pooled object can be destroyed out from under us by a scene
                // unload; fall through to a fresh instantiate rather than throwing.
                if (go == null) go = UnityEngine.Object.Instantiate(_prefab);
            }
            else
            {
                go = UnityEngine.Object.Instantiate(_prefab);
            }

            Transform t = go.transform;
            t.SetParent(parent != null ? parent : _root, false);
            t.SetPositionAndRotation(position, rotation);
            go.SetActive(true);

            ActiveCount++;

            IPoolable[] poolables = go.GetComponentsInChildren<IPoolable>(true);
            for (int i = 0; i < poolables.Length; i++) poolables[i].OnRented();

            return go;
        }

        public void Return(GameObject go)
        {
            if (go == null) return;

            IPoolable[] poolables = go.GetComponentsInChildren<IPoolable>(true);
            for (int i = 0; i < poolables.Length; i++) poolables[i].OnReturned();

            go.SetActive(false);
            go.transform.SetParent(_root, false);

            ActiveCount--;
            if (ActiveCount < 0) ActiveCount = 0;

            if (_available.Count < _maxRetained) _available.Push(go);
            else UnityEngine.Object.Destroy(go);
        }

        public void Clear(bool destroyInstances = true)
        {
            if (destroyInstances)
            {
                while (_available.Count > 0)
                {
                    GameObject go = _available.Pop();
                    if (go != null) UnityEngine.Object.Destroy(go);
                }
            }
            else
            {
                _available.Clear();
            }

            ActiveCount = 0;
        }
    }
}
