using System;

namespace SilksongCoop.Mod.UI
{
    /// <summary>
    /// Lightweight network diagnostics tracker computing rolling averages for bandwidth,
    /// packet rates, and session duration. Updated from the main Update loop each frame.
    /// </summary>
    public class NetworkDiagnostics
    {
        private const float WindowSeconds = 2.0f; // Rolling window for rate calculations

        private float _elapsed;
        private long _bytesOutAccumulator;
        private long _bytesInAccumulator;
        private int _packetsOutAccumulator;
        private int _packetsInAccumulator;

        private float _bytesOutPerSecond;
        private float _bytesInPerSecond;
        private float _packetsOutPerSecond;
        private float _packetsInPerSecond;

        // Session timer
        private bool _sessionActive;
        private float _sessionStartTime;
        private float _sessionDuration;

        // Packet loss tracking (based on sequence number gaps)
        private uint _lastExpectedInSeq = uint.MaxValue;
        private long _packetsExpected;
        private long _packetsReceived;

        public long BytesOutPerSecond => (long)_bytesOutPerSecond;
        public long BytesInPerSecond => (long)_bytesInPerSecond;
        public long PacketsOutPerSecond => (long)_packetsOutPerSecond;
        public long PacketsInPerSecond => (long)_packetsInPerSecond;
        public float SessionDuration => _sessionActive ? (UnityEngine.Time.time - _sessionStartTime) : _sessionDuration;

        /// <summary>
        /// Estimated packet loss percentage (0–100). Returns 0 when insufficient data.
        /// </summary>
        public float PacketLossPercent
        {
            get
            {
                if (_packetsExpected < 10) return 0f;
                long lost = Math.Max(0, _packetsExpected - _packetsReceived);
                return (float)lost / _packetsExpected * 100f;
            }
        }

        /// <summary>
        /// Update rolling averages. Call once per frame from Update().
        /// </summary>
        public void Update(float deltaTime)
        {
            _elapsed += deltaTime;

            if (_elapsed >= WindowSeconds)
            {
                float inv = 1f / _elapsed;
                _bytesOutPerSecond = _bytesOutAccumulator * inv;
                _bytesInPerSecond = _bytesInAccumulator * inv;
                _packetsOutPerSecond = _packetsOutAccumulator * inv;
                _packetsInPerSecond = _packetsInAccumulator * inv;

                _bytesOutAccumulator = 0;
                _bytesInAccumulator = 0;
                _packetsOutAccumulator = 0;
                _packetsInAccumulator = 0;
                _elapsed = 0f;
            }
        }

        /// <summary>
        /// Record outgoing bytes. Call whenever a packet is sent.
        /// </summary>
        public void RecordSent(int byteCount)
        {
            _bytesOutAccumulator += byteCount;
            _packetsOutAccumulator++;
        }

        /// <summary>
        /// Record incoming bytes. Call whenever a packet is received.
        /// Optionally pass the expected sequence number for loss tracking.
        /// </summary>
        public void RecordReceived(int byteCount, uint? sequenceNumber = null)
        {
            _bytesInAccumulator += byteCount;
            _packetsInAccumulator++;

            if (sequenceNumber.HasValue)
            {
                uint seq = sequenceNumber.Value;
                if (_lastExpectedInSeq != uint.MaxValue)
                {
                    // Count how many packets we expected to receive since last seen
                    uint expected = seq > _lastExpectedInSeq
                        ? seq - _lastExpectedInSeq
                        : 1; // Wrap-around or out-of-order; count as 1
                    _packetsExpected += expected;
                    _packetsReceived++;
                }
                _lastExpectedInSeq = seq;
            }
        }

        /// <summary>
        /// Mark session start (for session timer).
        /// </summary>
        public void OnSessionStarted()
        {
            _sessionActive = true;
            _sessionStartTime = UnityEngine.Time.time;
            _lastExpectedInSeq = uint.MaxValue;
            _packetsExpected = 0;
            _packetsReceived = 0;
            Reset();
        }

        /// <summary>
        /// Mark session end.
        /// </summary>
        public void OnSessionEnded()
        {
            if (_sessionActive)
            {
                _sessionDuration = UnityEngine.Time.time - _sessionStartTime;
            }
            _sessionActive = false;
            Reset();
        }

        private void Reset()
        {
            _elapsed = 0f;
            _bytesOutAccumulator = 0;
            _bytesInAccumulator = 0;
            _packetsOutAccumulator = 0;
            _packetsInAccumulator = 0;
            _bytesOutPerSecond = 0f;
            _bytesInPerSecond = 0f;
            _packetsOutPerSecond = 0f;
            _packetsInPerSecond = 0f;
        }
    }
}
