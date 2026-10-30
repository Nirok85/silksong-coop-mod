using SilksongCoop.Core.Interpolation;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Covers the packet-ordering and clock-mapping rules used by the remote clone.
    /// </summary>
    public class InterpolationClockTests
    {
        [Fact]
        public void FirstPacket_CalibratesOffset()
        {
            var clock = new InterpolationClock();

            bool accepted = clock.TryMap(
                timestampMs: 1000,
                sequence: 1,
                localNowMs: 2500,
                localClockRunning: true,
                out double mapped,
                out bool reanchored,
                out bool frozen);

            Assert.True(accepted);
            Assert.True(clock.HasOffset);
            Assert.Equal(1500.0, clock.OffsetMs);
            Assert.Equal(2500.0, mapped);
            Assert.False(reanchored);
            Assert.False(frozen);
        }

        [Fact]
        public void NormalAdvance_IsAcceptedWithoutReanchoring()
        {
            var clock = Calibrated();

            bool accepted = clock.TryMap(1033, 2, 2533, true, out double mapped, out bool reanchored, out bool frozen);

            Assert.True(accepted);
            Assert.Equal(2533.0, mapped);
            Assert.False(reanchored);
            Assert.False(frozen);
        }

        [Fact]
        public void StalePacket_IsRejected()
        {
            var clock = Calibrated();
            Assert.True(clock.TryMap(1100, 5, 2600, true, out _, out _, out _));

            bool accepted = clock.TryMap(1033, 4, 2533, true, out _, out _, out _);

            Assert.False(accepted);
            Assert.Equal(1100u, clock.LastTimestampMs);
            Assert.Equal(5u, clock.LastSequence);
        }

        [Fact]
        public void FrozenClock_RepeatedSequence_IsRejected()
        {
            var clock = Calibrated();
            Assert.True(clock.TryMap(1000, 7, 2500, true, out _, out _, out _));

            bool accepted = clock.TryMap(1000, 7, 2500, true, out _, out _, out bool frozen);

            Assert.False(accepted);
            Assert.False(frozen);
        }

        [Fact]
        public void FrozenClock_NewerSequence_IsAcceptedAndFlaggedFrozen()
        {
            var clock = Calibrated();
            Assert.True(clock.TryMap(1000, 7, 2500, true, out _, out _, out _));

            bool accepted = clock.TryMap(1000, 8, 2600, true, out double mapped, out bool reanchored, out bool frozen);

            Assert.True(accepted);
            Assert.Equal(2500.0, mapped);
            Assert.False(reanchored);
            Assert.True(frozen);
        }

        [Fact]
        public void OneSidedPause_ReanchorsOnResume()
        {
            var clock = Calibrated();
            Assert.True(clock.TryMap(1000, 2, 2500, true, out _, out _, out bool frozen));
            Assert.True(frozen); // sender paused; timestamp repeats while local clock advanced

            // Sender resumed after a 2 s one-sided pause.
            bool accepted = clock.TryMap(1100, 3, 4600, true, out double mapped, out bool reanchored, out frozen);

            Assert.True(accepted);
            Assert.True(reanchored);
            Assert.False(frozen);
            Assert.Equal(4600.0, mapped);
            Assert.Equal(3500.0, clock.OffsetMs);
        }

        [Fact]
        public void LocalPause_DoesNotReanchor()
        {
            var clock = Calibrated();

            bool accepted = clock.TryMap(1300, 2, 2500, localClockRunning: false, out double mapped, out bool reanchored, out _);

            Assert.True(accepted);
            Assert.False(reanchored);
            Assert.Equal(2800.0, mapped);
        }

        [Fact]
        public void SubSecondFreeze_ReanchorsWhenItEnds()
        {
            var clock = Calibrated();

            // Sender frozen for ~300 ms of local game time while this client keeps running.
            Assert.True(clock.TryMap(1000, 2, 2500, true, out _, out _, out bool frozen));
            Assert.True(frozen);
            Assert.True(clock.TryMap(1000, 3, 2800, true, out _, out _, out frozen));
            Assert.True(frozen);

            bool accepted = clock.TryMap(1033, 4, 2833, true, out double mapped, out bool reanchored, out frozen);

            Assert.True(accepted);
            Assert.True(reanchored);
            Assert.False(frozen);
            Assert.Equal(2833.0, mapped);
        }

        [Fact]
        public void BriefDuplicate_DoesNotReanchor()
        {
            var clock = Calibrated();

            Assert.True(clock.TryMap(1000, 2, 2500, true, out _, out _, out _));

            bool accepted = clock.TryMap(1033, 3, 2533, true, out double mapped, out bool reanchored, out _);

            Assert.True(accepted);
            Assert.False(reanchored);
            Assert.Equal(2533.0, mapped);
        }

        [Fact]
        public void FreezeEndDuringLocalPause_ReanchorsAfterLocalResume()
        {
            var clock = Calibrated();

            Assert.True(clock.TryMap(1000, 2, 2500, true, out _, out _, out bool frozen));
            Assert.True(frozen);
            Assert.True(clock.TryMap(1000, 3, 2800, true, out _, out _, out _));

            // Sender resumes while this client is paused. Game time did not advance during
            // the local pause, so the correction must be deferred, not discarded.
            Assert.True(clock.TryMap(1033, 4, 2800, localClockRunning: false,
                out _, out bool reanchoredWhilePaused, out _));
            Assert.False(reanchoredWhilePaused);

            Assert.True(clock.TryMap(1066, 5, 2833, localClockRunning: true,
                out double mapped, out bool reanchored, out _));
            Assert.True(reanchored);
            Assert.Equal(2833.0, mapped);
        }

        [Fact]
        public void SessionReset_Reanchors()
        {
            var clock = Calibrated();
            Assert.True(clock.TryMap(50_000, 10, 51_500, true, out _, out _, out _));

            bool accepted = clock.TryMap(1000, 1, 52_000, true, out double mapped, out bool reanchored, out _);

            Assert.True(accepted);
            Assert.True(reanchored);
            Assert.Equal(52_000.0, mapped);
        }

        [Fact]
        public void SequenceWrap_StillOrdersFrozenClockUpdates()
        {
            var clock = Calibrated();
            Assert.True(clock.TryMap(1033, uint.MaxValue - 1, 2533, true, out _, out _, out _));
            Assert.True(clock.TryMap(1033, uint.MaxValue, 2566, true, out _, out _, out bool frozen));
            Assert.True(frozen);

            // The actual wrap goes max -> 0 -> 1. Sequence 0 means "unknown" by design and
            // is dropped; the next packet still orders correctly thanks to int wrap-around.
            Assert.False(clock.TryMap(1033, 0, 2600, true, out _, out _, out _));
            Assert.True(clock.TryMap(1033, 1, 2633, true, out _, out _, out frozen));
            Assert.True(frozen);
        }

        [Fact]
        public void Reset_ForgetsCalibration()
        {
            var clock = Calibrated();
            clock.Reset();

            Assert.False(clock.HasOffset);
            Assert.True(clock.TryMap(5000, 1, 9000, true, out double mapped, out _, out _));
            Assert.Equal(9000.0, mapped);
        }

        private static InterpolationClock Calibrated()
        {
            var clock = new InterpolationClock();
            Assert.True(clock.TryMap(1000, 1, 2500, true, out _, out _, out _));
            return clock;
        }
    }
}
