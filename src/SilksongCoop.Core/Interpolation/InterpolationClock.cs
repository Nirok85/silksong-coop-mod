using System;

namespace SilksongCoop.Core.Interpolation
{
    /// <summary>
    /// Maps remote transform timestamps onto the local game-time clock used by the snapshot
    /// buffer and decides whether a packet is worth consuming.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Transform packets are stamped with the sender's <c>Time.timeAsDouble</c> because the
    /// receiver evaluates snapshots against its own game clock. That clock freezes while the
    /// sender is paused, so timestamps alone cannot order packets: while paused the sender
    /// keeps emitting updates with an unchanged timestamp. The header sequence breaks the tie.
    /// </para>
    /// <para>
    /// The offset between the two game clocks is calibrated on the first packet and re-anchored
    /// when they drift by more than <see cref="ReanchorThresholdMs"/>. A one-sided pause leaves
    /// a permanent offset otherwise, because only the unpaused peer's clock advanced.
    /// </para>
    /// </remarks>
    public sealed class InterpolationClock
    {
        /// <summary>Clock drift that triggers re-anchoring, in milliseconds.</summary>
        public const double ReanchorThresholdMs = 1000.0;

        /// <summary>A timestamp gap larger than this means a session reset or clock wrap.</summary>
        public const long ClockResetThresholdMs = 10000;

        /// <summary>
        /// A freeze shorter than this is treated as a coarse clock tick or a duplicate
        /// packet; longer freezes are re-anchored when the clock resumes.
        /// </summary>
        public const double FrozenReanchorThresholdMs = 250.0;

        private const long MaxSequenceJump = 10000;

        private bool _hasOffset;
        private double _offsetMs;
        private uint _lastTimestampMs;
        private uint _lastSequence;
        private double _frozenSinceLocalMs = -1.0;

        /// <summary>True once an offset has been calibrated.</summary>
        public bool HasOffset => _hasOffset;

        /// <summary>Current mapping offset: local game time minus remote game time.</summary>
        public double OffsetMs => _offsetMs;

        /// <summary>Timestamp of the last consumed packet.</summary>
        public uint LastTimestampMs => _lastTimestampMs;

        /// <summary>Sequence of the last consumed packet.</summary>
        public uint LastSequence => _lastSequence;

        /// <summary>Forgets all calibration, for a new session or scene.</summary>
        public void Reset()
        {
            _hasOffset = false;
            _offsetMs = 0.0;
            _lastTimestampMs = 0;
            _lastSequence = 0;
            _frozenSinceLocalMs = -1.0;
        }

        /// <summary>
        /// Classifies a packet and, when accepted, maps its timestamp into local game time.
        /// </summary>
        /// <param name="timestampMs">Remote game-time timestamp (may be frozen while paused).</param>
        /// <param name="sequence">Remote transform sequence; breaks ties for a frozen clock.</param>
        /// <param name="localNowMs">Local game time in milliseconds.</param>
        /// <param name="localClockRunning">False while the local game is paused or time-scaled to zero.</param>
        /// <param name="mappedTimestampMs">Local game-time timestamp for the snapshot buffer.</param>
        /// <param name="reanchored">True when the offset changed and buffered snapshots must be dropped.</param>
        /// <param name="clockFrozen">True when the packet repeats the previous timestamp.</param>
        /// <returns>True when the packet is newer and should be consumed.</returns>
        public bool TryMap(
            uint timestampMs,
            uint sequence,
            double localNowMs,
            bool localClockRunning,
            out double mappedTimestampMs,
            out bool reanchored,
            out bool clockFrozen)
        {
            mappedTimestampMs = 0.0;
            reanchored = false;
            clockFrozen = false;

            long diff = (long)timestampMs - _lastTimestampMs;
            // Unchecked int subtraction handles sequence wrap after 2^32 sends.
            int sequenceDiff = unchecked((int)(sequence - _lastSequence));
            bool sequenceAdvanced = _hasOffset
                && sequence != 0
                && sequenceDiff > 0
                && sequenceDiff < MaxSequenceJump;

            bool newer = !_hasOffset
                || diff > 0
                || diff < -ClockResetThresholdMs
                || (diff == 0 && sequenceAdvanced);
            if (!newer) return false;

            if (!_hasOffset)
            {
                _offsetMs = localNowMs - timestampMs;
                _hasOffset = true;
            }
            else if (diff == 0)
            {
                // Same instant on the sender's clock: keep the existing mapping. Re-anchoring
                // here would fight the frozen clock and clear the buffer on every packet.
                clockFrozen = true;
                if (_frozenSinceLocalMs < 0.0)
                    _frozenSinceLocalMs = localNowMs;
            }
            else if (diff < -ClockResetThresholdMs)
            {
                // Sender wrapped or restarted; the old offset would map packets far in the past.
                _offsetMs = localNowMs - timestampMs;
                reanchored = true;
                _frozenSinceLocalMs = -1.0;
            }
            else
            {
                // A pause shorter than the drift threshold still shifts the mapping by the
                // pause duration; without this the clone extrapolates (or lags) until a
                // later longer pause. Re-anchor as soon as a meaningful freeze ends.
                if (_frozenSinceLocalMs >= 0.0)
                {
                    double frozenMs = localNowMs - _frozenSinceLocalMs;
                    if (localClockRunning)
                    {
                        _frozenSinceLocalMs = -1.0;
                        if (frozenMs > FrozenReanchorThresholdMs)
                        {
                            _offsetMs = localNowMs - timestampMs;
                            reanchored = true;
                        }
                    }
                    // When the local clock is paused, game time did not advance during the
                    // pause either, so keep the marker and measure the freeze on the first
                    // packet after the local clock starts again.
                }

                if (!reanchored && localClockRunning)
                {
                    double driftMs = timestampMs + _offsetMs - localNowMs;
                    if (driftMs < -ReanchorThresholdMs || driftMs > ReanchorThresholdMs)
                    {
                        _offsetMs = localNowMs - timestampMs;
                        reanchored = true;
                    }
                }
            }

            mappedTimestampMs = timestampMs + _offsetMs;
            _lastTimestampMs = timestampMs;
            _lastSequence = sequence;
            return true;
        }
    }
}
