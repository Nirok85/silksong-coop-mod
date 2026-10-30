using System;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Shared wall-clock round numbers replace per-peer counters so both sides compare
    /// samples that describe the same instant.
    /// </summary>
    public class DesyncRoundScheduleTests
    {
        [Fact]
        public void SameAbsoluteTime_ProducesSameRound()
        {
            long now = 1_700_000_123_456;
            Assert.Equal(
                DesyncRoundSchedule.ComputeRound(now, 5000),
                DesyncRoundSchedule.ComputeRound(now, 5000));
        }

        [Fact]
        public void RoundAdvancesAtIntervalBoundary()
        {
            const int interval = 5000;
            Assert.Equal(0u, DesyncRoundSchedule.ComputeRound(0, interval));
            Assert.Equal(0u, DesyncRoundSchedule.ComputeRound(4999, interval));
            Assert.Equal(1u, DesyncRoundSchedule.ComputeRound(5000, interval));
            Assert.Equal(1u, DesyncRoundSchedule.ComputeRound(9999, interval));
            Assert.Equal(2u, DesyncRoundSchedule.ComputeRound(10000, interval));
        }

        [Fact]
        public void PeersWithDifferentFrameRates_ShareRoundLabels()
        {
            // Peer A samples on 16 ms frames, peer B on 33 ms frames. Around a boundary the
            // first sample after it must carry the same label on both sides.
            const int interval = 5000;
            long boundary = 3 * interval;

            uint fast = DesyncRoundSchedule.ComputeRound(boundary + 16, interval);
            uint slow = DesyncRoundSchedule.ComputeRound(boundary + 33, interval);

            Assert.Equal(fast, slow);
            Assert.Equal(3u, fast);
        }

        [Fact]
        public void MissedInterval_DoesNotShiftLaterRounds()
        {
            // Peer A was suspended and skipped round 3 entirely. Once it resumes, its label
            // matches peer B's instead of drifting one round behind forever.
            const int interval = 5000;
            uint beforePause = DesyncRoundSchedule.ComputeRound(2 * interval + 100, interval);
            uint afterResume = DesyncRoundSchedule.ComputeRound(4 * interval + 100, interval);

            Assert.Equal(2u, beforePause);
            Assert.Equal(4u, afterResume);
            Assert.Equal(afterResume, DesyncRoundSchedule.ComputeRound(4 * interval + 200, interval));
        }

        [Fact]
        public void TinyInterval_IsClamped()
        {
            Assert.Equal(
                DesyncRoundSchedule.ComputeRound(10_000, DesyncRoundSchedule.MinIntervalMs),
                DesyncRoundSchedule.ComputeRound(10_000, intervalMs: 1));
        }

        [Fact]
        public void NegativeTimestamps_UseFloorSemantics()
        {
            Assert.Equal(unchecked((uint)-1), DesyncRoundSchedule.ComputeRound(-1, 5000));
            Assert.Equal(unchecked((uint)-2), DesyncRoundSchedule.ComputeRound(-5001, 5000));
        }
    }
}
