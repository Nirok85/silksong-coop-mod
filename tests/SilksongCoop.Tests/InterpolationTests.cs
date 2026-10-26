using System;
using SilksongCoop.Core.Interpolation;
using Xunit;

namespace SilksongCoop.Tests
{
    public class InterpolationTests
    {
        [Fact]
        public void HermiteSpline_BoundaryConditions_C1Continuity()
        {
            float p0 = 10.0f;
            float v0 = 5.0f;
            float p1 = 25.0f;
            float v1 = 2.0f;
            float dt = 0.5f;

            // At t = 0: Position must equal p0, Velocity must equal v0
            float posAt0 = HermiteSpline.InterpolatePosition(p0, v0, p1, v1, dt, 0f);
            float velAt0 = HermiteSpline.InterpolateVelocity(p0, v0, p1, v1, dt, 0f);

            Assert.Equal(p0, posAt0, 4);
            Assert.Equal(v0, velAt0, 4);

            // At t = 1: Position must equal p1, Velocity must equal v1
            float posAt1 = HermiteSpline.InterpolatePosition(p0, v0, p1, v1, dt, 1f);
            float velAt1 = HermiteSpline.InterpolateVelocity(p0, v0, p1, v1, dt, 1f);

            Assert.Equal(p1, posAt1, 4);
            Assert.Equal(v1, velAt1, 4);
        }

        [Fact]
        public void HermiteSpline_2DInterpolation_InterpolatesBothAxesIndependently()
        {
            float dt = 1.0f;
            var (posX, posY) = HermiteSpline.InterpolatePosition2D(
                0f, 0f,
                10f, 0f,
                10f, 20f,
                0f, 10f,
                dt, 0.5f);

            Assert.True(posX > 0f && posX < 10f);
            Assert.True(posY > 0f && posY < 20f);
        }

        [Fact]
        public void SnapshotBuffer_CircularWrapAround_RetainsLatest64Snapshots()
        {
            var buffer = new SnapshotBuffer(64);

            // Push 100 snapshots
            for (uint i = 0; i < 100; i++)
            {
                buffer.AddSnapshot(new TransformSnapshot
                {
                    Tick = i,
                    TimestampMs = i * 33.33,
                    PositionX = i * 1.0f,
                    PositionY = i * 0.5f
                });
            }

            Assert.Equal(64, buffer.Count);
            Assert.Equal(64, buffer.Capacity);

            // Oldest retained should be tick 36 (100 - 64)
            Assert.True(buffer.TryGetOldest(out var oldest));
            Assert.Equal((uint)36, oldest.Tick);

            // Latest should be tick 99
            Assert.True(buffer.TryGetLatest(out var latest));
            Assert.Equal((uint)99, latest.Tick);
        }

        [Fact]
        public void SnapshotBuffer_TryGetSnapshotsForTime_FindsCorrectBracketAndInterpolationFactor()
        {
            var buffer = new SnapshotBuffer(64);
            buffer.AddSnapshot(new TransformSnapshot { Tick = 1, TimestampMs = 100.0, PositionX = 10f });
            buffer.AddSnapshot(new TransformSnapshot { Tick = 2, TimestampMs = 200.0, PositionX = 20f });
            buffer.AddSnapshot(new TransformSnapshot { Tick = 3, TimestampMs = 300.0, PositionX = 30f });

            // Sample at renderTime = 150.0 (midway between 100 and 200)
            bool found = buffer.TryGetSnapshotsForTime(150.0, out var s0, out var s1, out double t);

            Assert.True(found);
            Assert.Equal(100.0, s0.TimestampMs);
            Assert.Equal(200.0, s1.TimestampMs);
            Assert.Equal(0.5, t, 3);

            // Sample at renderTime = 225.0 (25% between 200 and 300)
            found = buffer.TryGetSnapshotsForTime(225.0, out s0, out s1, out t);
            Assert.True(found);
            Assert.Equal(200.0, s0.TimestampMs);
            Assert.Equal(300.0, s1.TimestampMs);
            Assert.Equal(0.25, t, 3);
        }

        [Fact]
        public void SnapshotBuffer_ComputeAdaptiveDelay_FormulatesCorrectJitterWindow()
        {
            double delay = SnapshotBuffer.ComputeAdaptiveDelay(rttMs: 60.0, jitterMs: 10.0, tickIntervalMs: 33.33);

            // Delay = (60/2) + (2*10) + 33.33 = 30 + 20 + 33.33 = 83.33 ms
            Assert.Equal(83.33, delay, 2);
        }

        [Fact]
        public void DeadReckoning_GroundedExtrapolation_DampsHorizontalVelocity()
        {
            float startX = 0f;
            float startY = 5f;
            float vx = 10f;
            float vy = 0f;

            var (posX, posY) = DeadReckoning.ExtrapolatePosition(
                startX, startY, vx, vy,
                isGrounded: true,
                deltaSeconds: 0.1f,
                dampingRate: 1.5f);

            // In 0.1s without damping, pos = 1.0f. With damping, pos < 1.0f
            Assert.True(posX > 0.8f && posX < 1.0f);
            Assert.Equal(startY, posY); // Grounded Y stays anchored
        }

        [Fact]
        public void DeadReckoning_AirborneExtrapolation_AppliesGravity()
        {
            float startX = 0f;
            float startY = 10f;
            float vx = 0f;
            float vy = 0f; // Initially stationary in air

            var (posX, posY) = DeadReckoning.ExtrapolatePosition(
                startX, startY, vx, vy,
                isGrounded: false,
                deltaSeconds: 0.1f,
                gravity: -38.0f);

            // Y should drop due to gravity: 10 + 0.5 * (-38) * (0.1)^2 = 10 - 0.19 = 9.81
            Assert.Equal(0f, posX);
            Assert.True(posY < startY);
            Assert.Equal(9.81f, posY, 2);
        }

        [Fact]
        public void DeadReckoning_SnapCutoff_TriggersWhenDistanceExceedsThreshold()
        {
            // Close distance (< 2.5) -> should NOT snap
            Assert.False(DeadReckoning.ShouldSnap(0f, 0f, 1.5f, 1.0f, threshold: 2.5f));

            // Far distance (> 2.5) -> MUST snap
            Assert.True(DeadReckoning.ShouldSnap(0f, 0f, 10.0f, 0f, threshold: 2.5f));
            Assert.True(DeadReckoning.ShouldSnap(0f, 0f, 2.0f, 2.0f, threshold: 2.5f)); // dist = sqrt(8) ~ 2.82 > 2.5
        }
    }
}
