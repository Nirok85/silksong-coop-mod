using System;
using SilksongCoop.Core.Interpolation;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;
using Xunit;

namespace SilksongCoop.Tests
{
    public class RemoteCloneInterpolationTests
    {
        [Fact]
        public void HermiteSpline_LinearMotion_MaintainsExactMidpoint()
        {
            // Moving from x=0 to x=10 with constant velocity 10.0 over dt = 1.0s
            float p0 = 0f, v0 = 10f;
            float p1 = 10f, v1 = 10f;
            float dt = 1.0f;

            float pMid = HermiteSpline.InterpolatePosition(p0, v0, p1, v1, dt, 0.5f);
            float vMid = HermiteSpline.InterpolateVelocity(p0, v0, p1, v1, dt, 0.5f);

            Assert.Equal(5.0f, pMid, 3);
            Assert.Equal(10.0f, vMid, 3);
        }

        [Fact]
        public void HermiteSpline_Turnaround_SmoothVelocityTransitionWithoutDiscontinuity()
        {
            // Symmetric turnaround reversing from velocity +15 to -15 over dt = 0.5s returning to start position
            float p0 = 20f, v0 = 15f;
            float p1 = 20f, v1 = -15f;
            float dt = 0.5f;

            // Position at apex (t = 0.5) must be higher than start and end (reaches apex 21.875)
            float pApex = HermiteSpline.InterpolatePosition(p0, v0, p1, v1, dt, 0.5f);
            float vApex = HermiteSpline.InterpolateVelocity(p0, v0, p1, v1, dt, 0.5f);

            Assert.True(pApex > p0 && pApex > p1, "Turnaround curve must create an apex point.");
            Assert.Equal(0f, vApex, 1); // Velocity transitions through zero at apex
        }

        [Fact]
        public void SnapshotBuffer_PacketLossExtrapolation_DeadReckonsWithinCap()
        {
            var buffer = new SnapshotBuffer(64);
            buffer.AddSnapshot(new TransformSnapshot { Tick = 1, TimestampMs = 1000.0, PositionX = 10f, VelocityX = 12f });
            buffer.AddSnapshot(new TransformSnapshot { Tick = 2, TimestampMs = 1033.3, PositionX = 10.4f, VelocityX = 12f });

            // Packet loss: Request render time at 1150ms (116.7ms after latest snapshot)
            bool found = buffer.TryGetSnapshotsForTime(1150.0, out var s0, out var s1, out double t);
            Assert.True(found);
            Assert.Equal(1.0, t); // Clamps to newest available snapshot for dead reckoning

            var (predX, _) = DeadReckoning.ExtrapolatePosition(
                s1.PositionX, 0f,
                s1.VelocityX, 0f,
                isGrounded: true,
                deltaSeconds: 0.1167f);

            Assert.True(predX > s1.PositionX, "Dead reckoning must advance forward along velocity vector.");
            Assert.True(predX - s1.PositionX < 3.0f, "Drift must stay bounded below snap threshold.");
        }

        [Fact]
        public void SnapshotBuffer_StaleOutOfOrderPacket_DroppedWithoutReverseSnap()
        {
            var buffer = new SnapshotBuffer(64);
            buffer.AddSnapshot(new TransformSnapshot { Tick = 10, TimestampMs = 2000.0, PositionX = 50f });
            buffer.AddSnapshot(new TransformSnapshot { Tick = 12, TimestampMs = 2066.0, PositionX = 52f });

            // Out-of-order packet arriving with timestamp 2033.0
            buffer.AddSnapshot(new TransformSnapshot { Tick = 11, TimestampMs = 2033.0, PositionX = 51f });

            Assert.Equal(2, buffer.Count);
            Assert.True(buffer.TryGetLatest(out var latest));
            Assert.Equal(2066.0, latest.TimestampMs);
            Assert.Equal(52f, latest.PositionX);
        }

        [Fact]
        public void SnapshotBuffer_AdaptiveDelayCalculation_AccountsForJitterAndRtt()
        {
            double delayLowJitter = SnapshotBuffer.ComputeAdaptiveDelay(rttMs: 60.0, jitterMs: 5.0, tickIntervalMs: 33.33);
            double delayHighJitter = SnapshotBuffer.ComputeAdaptiveDelay(rttMs: 60.0, jitterMs: 25.0, tickIntervalMs: 33.33);

            // Formula: Delay = (RTT / 2) + (2 * Jitter) + TickInterval
            Assert.Equal(30.0 + 10.0 + 33.33, delayLowJitter, 2);
            Assert.Equal(30.0 + 50.0 + 33.33, delayHighJitter, 2);
            Assert.True(delayHighJitter > delayLowJitter);
        }
    }
}
