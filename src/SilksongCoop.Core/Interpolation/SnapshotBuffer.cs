using System;

namespace SilksongCoop.Core.Interpolation
{
    /// <summary>
    /// Fixed-capacity circular snapshot buffer (64 snapshots) for interpolation.
    /// Manages timeline alignment, bracket finding, and adaptive delay estimation.
    /// </summary>
    public class SnapshotBuffer
    {
        public const int DefaultCapacity = 64;

        private readonly TransformSnapshot[] _buffer;
        private int _count;
        private int _head; // Points to next insertion index

        public int Capacity => _buffer.Length;
        public int Count => _count;

        public SnapshotBuffer(int capacity = DefaultCapacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _buffer = new TransformSnapshot[capacity];
            _count = 0;
            _head = 0;
        }

        public void Clear()
        {
            _count = 0;
            _head = 0;
        }

        public void AddSnapshot(TransformSnapshot snapshot)
        {
            if (_count > 0)
            {
                var latest = GetSnapshotAt(_count - 1);
                if (snapshot.TimestampMs <= latest.TimestampMs)
                {
                    // Fix: Check for clock wrap / session reset with proper uint wraparound handling
                    // If the new timestamp is significantly older (> 10 seconds), treat as session reset
                    double timeDiff = latest.TimestampMs - snapshot.TimestampMs;
                    if (timeDiff > 10000.0)
                    {
                        Clear();
                    }
                    else
                    {
                        return; // Drop stale out-of-order or duplicate packet
                    }
                }
            }

            // Insert in circular buffer
            _buffer[_head] = snapshot;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length)
            {
                _count++;
            }
        }

        public bool TryGetLatest(out TransformSnapshot latest)
        {
            if (_count == 0)
            {
                latest = default;
                return false;
            }

            int index = (_head - 1 + _buffer.Length) % _buffer.Length;
            latest = _buffer[index];
            return true;
        }

        public bool TryGetOldest(out TransformSnapshot oldest)
        {
            if (_count == 0)
            {
                oldest = default;
                return false;
            }

            int index = (_head - _count + _buffer.Length) % _buffer.Length;
            oldest = _buffer[index];
            return true;
        }

        public TransformSnapshot GetSnapshotAt(int chronologicalIndex)
        {
            if (chronologicalIndex < 0 || chronologicalIndex >= _count)
                throw new ArgumentOutOfRangeException(nameof(chronologicalIndex));

            int physicalIndex = (_head - _count + chronologicalIndex + _buffer.Length) % _buffer.Length;
            return _buffer[physicalIndex];
        }

        /// <summary>
        /// Finds two snapshots s0 and s1 such that s0.TimestampMs <= renderTime <= s1.TimestampMs,
        /// and computes normalized t in [0.0, 1.0].
        /// </summary>
        public bool TryGetSnapshotsForTime(double renderTime, out TransformSnapshot s0, out TransformSnapshot s1, out double t)
        {
            s0 = default;
            s1 = default;
            t = 0.0;

            if (_count == 0)
                return false;

            if (_count == 1)
            {
                s0 = GetSnapshotAt(0);
                s1 = s0;
                t = renderTime >= s0.TimestampMs ? 1.0 : 0.0;
                return true;
            }

            var oldest = GetSnapshotAt(0);
            if (renderTime <= oldest.TimestampMs)
            {
                s0 = oldest;
                s1 = oldest;
                t = 0.0;
                return true;
            }

            var newest = GetSnapshotAt(_count - 1);
            if (renderTime >= newest.TimestampMs)
            {
                s0 = newest;
                s1 = newest;
                t = 1.0;
                return true;
            }

            // Binary search or linear search over the chronological snapshots
            for (int i = 0; i < _count - 1; i++)
            {
                var cur = GetSnapshotAt(i);
                var next = GetSnapshotAt(i + 1);

                if (cur.TimestampMs <= renderTime && renderTime <= next.TimestampMs)
                {
                    s0 = cur;
                    s1 = next;
                    double dt = next.TimestampMs - cur.TimestampMs;
                    t = dt > 0.0001 ? Math.Clamp((renderTime - cur.TimestampMs) / dt, 0.0, 1.0) : 0.0;
                    return true;
                }
            }

            s0 = newest;
            s1 = newest;
            t = 1.0;
            return true;
        }

        /// <summary>
        /// Calculates adaptive interpolation delay based on RTT, jitter standard deviation, and tick interval.
        /// Formula: Delay = (RTT / 2) + (2 * jitter) + tickInterval
        /// </summary>
        public static double ComputeAdaptiveDelay(double rttMs, double jitterMs, double tickIntervalMs = 33.33)
        {
            double oneWay = Math.Max(0, rttMs * 0.5);
            double jitterBuffer = Math.Max(0, 2.0 * jitterMs);
            return oneWay + jitterBuffer + Math.Max(16.66, tickIntervalMs);
        }
    }
}
