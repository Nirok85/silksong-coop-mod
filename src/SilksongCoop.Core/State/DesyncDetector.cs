using System;
using System.Text;
using SilksongCoop.Core.Events;
using SilksongCoop.Core.Networking;

namespace SilksongCoop.Core.State
{
    /// <summary>
    /// Detects desynchronization between peers by comparing state checksums.
    /// </summary>
    public class DesyncDetector
    {
        public class DesyncDetectedEvent : GameEvent
        {
            public string LocalChecksum { get; set; } = string.Empty;
            public string RemoteChecksum { get; set; } = string.Empty;
            public string Context { get; set; } = string.Empty;
        }

        private readonly IEventBus _eventBus;
        private float _lastCheckTime;
        private string _lastLocalChecksum = string.Empty;
        private string _lastRemoteChecksum = string.Empty;

        public float CheckIntervalSeconds { get; set; } = 5f;
        public bool Enabled { get; set; } = true;

        public DesyncDetector(IEventBus eventBus)
        {
            _eventBus = eventBus;
        }

        public void Update(float currentTime, string localChecksum)
        {
            if (!Enabled) return;
            if (currentTime - _lastCheckTime < CheckIntervalSeconds) return;

            _lastCheckTime = currentTime;
            _lastLocalChecksum = localChecksum;

            // Remote checksum would be set via OnRemoteChecksumReceived
        }

        public void OnRemoteChecksumReceived(string remoteChecksum, string context)
        {
            _lastRemoteChecksum = remoteChecksum;

            if (string.IsNullOrEmpty(_lastLocalChecksum) || string.IsNullOrEmpty(remoteChecksum))
                return;

            if (_lastLocalChecksum != remoteChecksum)
            {
                _eventBus.Publish(new DesyncDetectedEvent
                {
                    LocalChecksum = _lastLocalChecksum,
                    RemoteChecksum = remoteChecksum,
                    Context = context
                });
            }
        }

        /// <summary>
        /// Computes a stable FNV-1a checksum over a caller-supplied state signature.
        /// The signature must contain only state both peers are expected to share
        /// (scene, progression, shared currency), never per-player positions or health.
        /// </summary>
        public string ComputeChecksum(string stateSignature)
        {
            uint hash = 2166136261;
            var bytes = Encoding.UTF8.GetBytes(stateSignature ?? string.Empty);
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= 16777619;
            }
            return hash.ToString("X8");
        }

        public void Reset()
        {
            _lastCheckTime = 0;
            _lastLocalChecksum = string.Empty;
            _lastRemoteChecksum = string.Empty;
        }
    }
}
