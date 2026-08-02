using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Vigil.Core.Contracts;
using Vigil.Core.Diagnostics;

namespace Vigil.Data
{
    /// <summary>
    /// Networking tuning.
    ///
    /// <para>Named <c>NetworkTuningConfig</c> rather than the more obvious
    /// <c>NetworkConfig</c> because Netcode for GameObjects already exports
    /// <c>Unity.Netcode.NetworkConfig</c>. Any file that used both â€” which is every
    /// file in Vigil.Net â€” would need a disambiguating alias, and someone would
    /// eventually resolve the ambiguity the wrong way. Renaming ours once is
    /// cheaper than that class of bug.</para>
    /// </summary>
    [CreateAssetMenu(menuName = "Vigil/Net/Network Tuning Config", fileName = "NetworkTuningConfig")]
    public sealed class NetworkTuningConfig : ScriptableObject, IValidatableConfig
    {
        [Header("Timebase")]
        [SerializeField, Range(10, 60), Tooltip("Simulation ticks per second. Must match NetworkManager's tick rate.")]
        int _tickRate = 30;

        [SerializeField, Range(1, 60)] int _snapshotSendRate = 20;

        [Header("Session")]
        [SerializeField, Range(1, 8)] int _maxPlayers = 4;
        [SerializeField, Min(1f)] float _connectionTimeoutSeconds = 12f;
        [SerializeField, Min(0f), Tooltip("Seconds a dropped client may reconnect and reclaim its character.")]
        float _reconnectWindowSeconds = 45f;

        [SerializeField] ushort _defaultPort = 7777;

        [Header("Interest management")]
        [SerializeField, Min(1f), Tooltip("Base replication radius.")]
        float _interestRadius = 55f;

        [SerializeField, Min(1f), Tooltip("Hysteresis band. An entity must exit interestRadius + this before being hidden, so a boundary-hugging entity does not show/hide repeatedly.")]
        float _interestHysteresis = 9f;

        [SerializeField, Min(0.05f)] float _interestUpdateInterval = 0.4f;

        [Header("Prediction")]
        [SerializeField, Range(2, 30), Tooltip("Ticks of snapshot buffering before interpolation. Higher = smoother under jitter, but adds visual latency.")]
        int _interpolationBufferTicks = 3;

        [SerializeField, Range(4, 120), Tooltip("Maximum ticks of prediction replayed during reconciliation.")]
        int _maxPredictionTicks = 45;

        [SerializeField, Min(0.001f), Tooltip("Positional error below which NO correction is applied. Correcting sub-centimetre error every tick produces jitter that is worse than the error itself.")]
        float _reconcileThreshold = 0.06f;

        [SerializeField, Range(1, 12), Tooltip("Unacknowledged commands resent per message, so one dropped packet does not stall the server's input queue.")]
        int _inputRedundancy = 3;

        [SerializeField, Range(1, 8), Tooltip("Maximum input commands the server executes per tick. Caps speed-hack style flooding.")]
        int _maxCommandsPerTick = 2;

        public int TickRate => _tickRate;
        public int SnapshotSendRate => _snapshotSendRate;
        public int MaxPlayers => _maxPlayers;
        public float ConnectionTimeoutSeconds => _connectionTimeoutSeconds;
        public float ReconnectWindowSeconds => _reconnectWindowSeconds;
        public ushort DefaultPort => _defaultPort;
        public float InterestRadius => _interestRadius;
        public float InterestHysteresis => _interestHysteresis;
        public float InterestUpdateInterval => _interestUpdateInterval;
        public int InterpolationBufferTicks => _interpolationBufferTicks;
        public int MaxPredictionTicks => _maxPredictionTicks;
        public float ReconcileThreshold => _reconcileThreshold;
        public int InputRedundancy => _inputRedundancy;
        public int MaxCommandsPerTick => _maxCommandsPerTick;

        public void Validate(IList<string> problems)
        {
            if (_snapshotSendRate > _tickRate)
            {
                problems.Add($"{name}: snapshotSendRate ({_snapshotSendRate}) exceeds tickRate ({_tickRate}); it cannot send more often than it simulates.");
            }
            if (_reconcileThreshold <= 0f)
            {
                problems.Add($"{name}: reconcileThreshold of 0 corrects every tick and will produce continuous visible jitter.");
            }
        }
    }
}
