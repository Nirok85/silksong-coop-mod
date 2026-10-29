using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace SilksongCoop.Mod.UI
{
    /// <summary>
    /// Lightweight network diagnostics tracker computing rolling averages for bandwidth,
    /// packet rates, and session duration. Updated from the main Update loop each frame.
    /// Maintains 60-second rolling history for graph visualization.
    /// </summary>
    public class NetworkDiagnostics
    {
        #region Fields

        private const float WindowSeconds = 2.0f; // Rolling window for rate calculations
        private const float HistoryDuration = 60.0f; // 60-second history for graphs
        private const int HistorySampleInterval = 1; // Sample every 1 second

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

        // Externally supplied loss estimate (transport-level unreliable sequence gaps).
        private float _externalLossPercent;

        // Historical data for graphs (rolling 60s window)
        private readonly Queue<(float timestamp, float value)> _pingHistory = new Queue<(float, float)>();
        private readonly Queue<(float timestamp, float value)> _packetLossHistory = new Queue<(float, float)>();
        private readonly Queue<(float timestamp, float value)> _bandwidthHistory = new Queue<(float, float)>();
        private float _lastHistorySampleTime;
        private float _currentPing;

        #endregion

        #region Properties

        public long BytesOutPerSecond => (long)_bytesOutPerSecond;
        public long BytesInPerSecond => (long)_bytesInPerSecond;
        public long PacketsOutPerSecond => (long)_packetsOutPerSecond;
        public long PacketsInPerSecond => (long)_packetsInPerSecond;
        public float SessionDuration => _sessionActive ? (Time.unscaledTime - _sessionStartTime) : _sessionDuration;
        public float CurrentPing => _currentPing;

        /// <summary>
        /// Estimated packet loss percentage (0–100). Returns the sequence-gap estimate when
        /// available, otherwise the value reported by the active transport (which measures
        /// gaps in the unreliable stream — the sequence-gap path is only fed when callers
        /// pass explicit sequence numbers).
        /// </summary>
        public float PacketLossPercent
        {
            get
            {
                if (_packetsExpected >= 10)
                {
                    long lost = Math.Max(0, _packetsExpected - _packetsReceived);
                    return (float)lost / _packetsExpected * 100f;
                }

                return _externalLossPercent;
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Record ping (RTT) value in milliseconds.
        /// </summary>
        public void RecordPing(float pingMs)
        {
            _currentPing = pingMs;
        }

        /// <summary>
        /// Record the active transport's packet-loss estimate (0–100). Used for the F11
        /// overlay until/unless explicit sequence numbers are supplied to
        /// <see cref="RecordReceived"/>.
        /// </summary>
        public void RecordLossPercent(float percent)
        {
            _externalLossPercent = Math.Clamp(percent, 0f, 100f);
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
            _sessionStartTime = Time.unscaledTime;
            _lastExpectedInSeq = uint.MaxValue;
            _packetsExpected = 0;
            _packetsReceived = 0;
            Reset();
            ClearHistory();
        }

        /// <summary>
        /// Mark session end.
        /// </summary>
        public void OnSessionEnded()
        {
            if (_sessionActive)
            {
                _sessionDuration = Time.unscaledTime - _sessionStartTime;
            }
            _sessionActive = false;
            Reset();
        }

        /// <summary>
        /// Get ping history for graph rendering (oldest to newest).
        /// </summary>
        public IEnumerable<float> GetPingHistory()
        {
            foreach (var (_, value) in _pingHistory)
                yield return value;
        }

        /// <summary>
        /// Get packet loss history for graph rendering (oldest to newest).
        /// </summary>
        public IEnumerable<float> GetPacketLossHistory()
        {
            foreach (var (_, value) in _packetLossHistory)
                yield return value;
        }

        /// <summary>
        /// Get bandwidth history for graph rendering (oldest to newest).
        /// </summary>
        public IEnumerable<float> GetBandwidthHistory()
        {
            foreach (var (_, value) in _bandwidthHistory)
                yield return value;
        }

        /// <summary>
        /// Dump full diagnostics state to a file.
        /// </summary>
        public void DumpToFile(string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Silksong Co-op Diagnostics Dump ===");
            sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            
            sb.AppendLine("--- Session Info ---");
            sb.AppendLine($"Session Active: {_sessionActive}");
            sb.AppendLine($"Session Duration: {SessionDuration:F1}s");
            sb.AppendLine();
            
            sb.AppendLine("--- Current Metrics ---");
            sb.AppendLine($"Bytes Out/s: {BytesOutPerSecond}");
            sb.AppendLine($"Bytes In/s: {BytesInPerSecond}");
            sb.AppendLine($"Packets Out/s: {PacketsOutPerSecond}");
            sb.AppendLine($"Packets In/s: {PacketsInPerSecond}");
            sb.AppendLine($"Current Ping: {_currentPing:F1}ms");
            sb.AppendLine($"Packet Loss: {PacketLossPercent:F2}%");
            sb.AppendLine();
            
            sb.AppendLine("--- Historical Data (60s) ---");
            sb.AppendLine($"Ping samples: {_pingHistory.Count}");
            sb.AppendLine($"Packet loss samples: {_packetLossHistory.Count}");
            sb.AppendLine($"Bandwidth samples: {_bandwidthHistory.Count}");
            
            if (_pingHistory.Count > 0)
            {
                float minPing = float.MaxValue, maxPing = float.MinValue, sumPing = 0;
                foreach (var (_, value) in _pingHistory)
                {
                    minPing = Math.Min(minPing, value);
                    maxPing = Math.Max(maxPing, value);
                    sumPing += value;
                }
                float avgPing = sumPing / _pingHistory.Count;
                sb.AppendLine($"  Ping: min={minPing:F1}ms, avg={avgPing:F1}ms, max={maxPing:F1}ms");
            }
            
            if (_packetLossHistory.Count > 0)
            {
                float minLoss = float.MaxValue, maxLoss = float.MinValue, sumLoss = 0;
                foreach (var (_, value) in _packetLossHistory)
                {
                    minLoss = Math.Min(minLoss, value);
                    maxLoss = Math.Max(maxLoss, value);
                    sumLoss += value;
                }
                float avgLoss = sumLoss / _packetLossHistory.Count;
                sb.AppendLine($"  Packet Loss: min={minLoss:F2}%, avg={avgLoss:F2}%, max={maxLoss:F2}%");
            }
            
            if (_bandwidthHistory.Count > 0)
            {
                float minBw = float.MaxValue, maxBw = float.MinValue, sumBw = 0;
                foreach (var (_, value) in _bandwidthHistory)
                {
                    minBw = Math.Min(minBw, value);
                    maxBw = Math.Max(maxBw, value);
                    sumBw += value;
                }
                float avgBw = sumBw / _bandwidthHistory.Count;
                sb.AppendLine($"  Bandwidth: min={minBw:F0} B/s, avg={avgBw:F0} B/s, max={maxBw:F0} B/s");
            }

            File.WriteAllText(filePath, sb.ToString());
        }

        #endregion

        #region Private Methods

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

            // Sample historical data every second. Unscaled time keeps the graph and
            // session timer running while the game is paused.
            float currentTime = Time.unscaledTime;
            if (currentTime - _lastHistorySampleTime >= HistorySampleInterval)
            {
                SampleHistory(currentTime);
                _lastHistorySampleTime = currentTime;
            }
        }

        /// <summary>
        /// Sample current metrics for historical graphs.
        /// </summary>
        private void SampleHistory(float timestamp)
        {
            // Record current metrics
            _pingHistory.Enqueue((timestamp, _currentPing));
            _packetLossHistory.Enqueue((timestamp, PacketLossPercent));
            _bandwidthHistory.Enqueue((timestamp, _bytesOutPerSecond + _bytesInPerSecond));

            // Remove old samples (older than 60 seconds)
            float cutoffTime = timestamp - HistoryDuration;
            while (_pingHistory.Count > 0 && _pingHistory.Peek().timestamp < cutoffTime)
                _pingHistory.Dequeue();
            while (_packetLossHistory.Count > 0 && _packetLossHistory.Peek().timestamp < cutoffTime)
                _packetLossHistory.Dequeue();
            while (_bandwidthHistory.Count > 0 && _bandwidthHistory.Peek().timestamp < cutoffTime)
                _bandwidthHistory.Dequeue();
        }

        /// <summary>
        /// Clear all historical data.
        /// </summary>
        private void ClearHistory()
        {
            _pingHistory.Clear();
            _packetLossHistory.Clear();
            _bandwidthHistory.Clear();
            _lastHistorySampleTime = 0f;
            _currentPing = 0f;
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
            _externalLossPercent = 0f;
        }

        #endregion
    }
}
