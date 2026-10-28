using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SilksongCoop.Core.Interpolation;
using SilksongCoop.Core.Networking;
using Xunit;

namespace SilksongCoop.Tests
{
    public class ChallengerInterpolationAndConcurrencyTests
    {
        #region 1. Mathematical Verification: HermiteSpline Boundary & Continuity

        [Theory]
        [InlineData(0f, 0f, 10f, 0f, 0.1f)]
        [InlineData(-50f, 100f, 50f, -100f, 0.0333f)]
        [InlineData(1000f, -200f, -1000f, 200f, 1.0f)]
        [InlineData(15.2f, 30.5f, 15.2f, -30.5f, 0.0166f)] // 180 snap turn at same position
        [InlineData(0f, 500f, 100f, -500f, 0.5f)] // Extreme velocity snap
        public void HermiteSpline_BoundaryConditions_ExactMatch(float p0, float v0, float p1, float v1, float dt)
        {
            // P(0) == p0, V(0) == v0
            float pAt0 = HermiteSpline.InterpolatePosition(p0, v0, p1, v1, dt, 0f);
            float vAt0 = HermiteSpline.InterpolateVelocity(p0, v0, p1, v1, dt, 0f);

            Assert.Equal(p0, pAt0, 3);
            Assert.Equal(v0, vAt0, 3);

            // P(1) == p1, V(1) == v1
            float pAt1 = HermiteSpline.InterpolatePosition(p0, v0, p1, v1, dt, 1f);
            float vAt1 = HermiteSpline.InterpolateVelocity(p0, v0, p1, v1, dt, 1f);

            Assert.Equal(p1, pAt1, 3);
            Assert.Equal(v1, vAt1, 3);
        }

        [Fact]
        public void HermiteSpline_ExhaustiveGridBoundaryVerification()
        {
            float[] testPositions = { -100f, -1f, 0f, 1f, 100f };
            float[] testVelocities = { -50f, -10f, 0f, 10f, 50f };
            float[] testDts = { 0.0166f, 0.0333f, 0.05f, 0.1f, 1.0f };

            foreach (var p0 in testPositions)
            {
                foreach (var p1 in testPositions)
                {
                    foreach (var v0 in testVelocities)
                    {
                        foreach (var v1 in testVelocities)
                        {
                            foreach (var dt in testDts)
                            {
                                float pos0 = HermiteSpline.InterpolatePosition(p0, v0, p1, v1, dt, 0f);
                                float vel0 = HermiteSpline.InterpolateVelocity(p0, v0, p1, v1, dt, 0f);
                                float pos1 = HermiteSpline.InterpolatePosition(p0, v0, p1, v1, dt, 1f);
                                float vel1 = HermiteSpline.InterpolateVelocity(p0, v0, p1, v1, dt, 1f);

                                Assert.True(Math.Abs(pos0 - p0) < 1e-4f, $"P(0) mismatch: got {pos0}, expected {p0}");
                                Assert.True(Math.Abs(vel0 - v0) < 1e-3f, $"V(0) mismatch: got {vel0}, expected {v0}");
                                Assert.True(Math.Abs(pos1 - p1) < 1e-4f, $"P(1) mismatch: got {pos1}, expected {p1}");
                                Assert.True(Math.Abs(vel1 - v1) < 1e-3f, $"V(1) mismatch: got {vel1}, expected {v1}");
                            }
                        }
                    }
                }
            }
        }

        [Fact]
        public void HermiteSpline_180DegreeSnapTurn_DerivativeContinuityMatchesNumericalDerivative()
        {
            // Hornet dashing right at +30 units/s, snaps to dash left at -30 units/s
            float p0 = 10.0f;
            float v0 = 30.0f;
            float p1 = 10.5f;
            float v1 = -30.0f;
            float dt = 0.0333f; // 30Hz network snapshot interval

            int steps = 100;
            float eps = 0.005f; // Optimal central difference step for single-precision float (avoid float cancellation)

            for (int i = 5; i < steps - 5; i++)
            {
                float t = i / (float)steps;

                // Analytical velocity from HermiteSpline derivative
                float analyticalVel = HermiteSpline.InterpolateVelocity(p0, v0, p1, v1, dt, t);

                // Central difference numerical derivative: dP/ds = (P(t + eps) - P(t - eps)) / (2 * eps * dt)
                float pPlus = HermiteSpline.InterpolatePosition(p0, v0, p1, v1, dt, t + eps);
                float pMinus = HermiteSpline.InterpolatePosition(p0, v0, p1, v1, dt, t - eps);
                float numericalVel = (pPlus - pMinus) / (2.0f * eps * dt);

                float diff = Math.Abs(analyticalVel - numericalVel);
                Assert.True(diff < 0.1f, $"Velocity derivative mismatch at t={t}: Analytical={analyticalVel}, Numerical={numericalVel}, Diff={diff}");
            }
        }

        [Fact]
        public void HermiteSpline_TwoSegmentJoint_ZeroVelocityDiscontinuityAcrossBoundary()
        {
            // Segment A: [0 to 1] with end velocity V_turn
            // Segment B: [1 to 2] with start velocity V_turn
            float pA0 = 0f; float vA0 = 20f;
            float pA1 = 5f; float vA1 = -20f; // Turnaround endpoint of Segment A
            float dtA = 0.0333f;

            float pB0 = pA1; float vB0 = vA1; // Start of Segment B matches end of Segment A
            float pB1 = 3f; float vB1 = -20f;
            float dtB = 0.0333f;

            // Evaluate end of Segment A (t -> 1.0)
            float posAEnd = HermiteSpline.InterpolatePosition(pA0, vA0, pA1, vA1, dtA, 1.0f);
            float velAEnd = HermiteSpline.InterpolateVelocity(pA0, vA0, pA1, vA1, dtA, 1.0f);

            // Evaluate start of Segment B (t -> 0.0)
            float posBStart = HermiteSpline.InterpolatePosition(pB0, vB0, pB1, vB1, dtB, 0.0f);
            float velBStart = HermiteSpline.InterpolateVelocity(pB0, vB0, pB1, vB1, dtB, 0.0f);

            Assert.Equal(posAEnd, posBStart, 4);
            Assert.Equal(velAEnd, velBStart, 4);
        }

        [Fact]
        public void HermiteSpline_DegenerateDeltaTime_ReturnsP1AndV1Safely()
        {
            float p0 = 5f; float v0 = 10f;
            float p1 = 20f; float v1 = -5f;

            float posZero = HermiteSpline.InterpolatePosition(p0, v0, p1, v1, dt: 0f, t: 0.5f);
            float velZero = HermiteSpline.InterpolateVelocity(p0, v0, p1, v1, dt: 0f, t: 0.5f);

            Assert.Equal(p1, posZero);
            Assert.Equal(v1, velZero);

            float posTiny = HermiteSpline.InterpolatePosition(p0, v0, p1, v1, dt: 0.00001f, t: 0.5f);
            float velTiny = HermiteSpline.InterpolateVelocity(p0, v0, p1, v1, dt: 0.00001f, t: 0.5f);

            Assert.Equal(p1, posTiny);
            Assert.Equal(v1, velTiny);
        }

        [Fact]
        public void HermiteSpline_ParameterTOutOfRange_ClampsToUnitInterval()
        {
            float p0 = 10f; float v0 = 0f;
            float p1 = 20f; float v1 = 0f;
            float dt = 1.0f;

            float posBelow = HermiteSpline.InterpolatePosition(p0, v0, p1, v1, dt, -5.0f);
            float posAbove = HermiteSpline.InterpolatePosition(p0, v0, p1, v1, dt, 5.0f);

            Assert.Equal(p0, posBelow, 4);
            Assert.Equal(p1, posAbove, 4);
        }

        #endregion

        #region 2. SnapshotBuffer Edge Cases

        [Fact]
        public void SnapshotBuffer_ExtremeWrapAround_10000Snapshots_MaintainsIntegrity()
        {
            const int capacity = 64;
            var buffer = new SnapshotBuffer(capacity);

            const int totalSnapshots = 10000;
            for (uint i = 0; i < totalSnapshots; i++)
            {
                buffer.AddSnapshot(new TransformSnapshot
                {
                    Tick = i,
                    TimestampMs = i * 33.333,
                    PositionX = i * 1.5f,
                    PositionY = i * 0.8f,
                    VelocityX = 10.0f,
                    VelocityY = -5.0f
                });

                int expectedCount = Math.Min((int)i + 1, capacity);
                Assert.Equal(expectedCount, buffer.Count);

                uint expectedOldestTick = i < capacity ? 0 : i - capacity + 1;
                uint expectedLatestTick = i;

                Assert.True(buffer.TryGetOldest(out var oldest));
                Assert.True(buffer.TryGetLatest(out var latest));
                Assert.Equal(expectedOldestTick, oldest.Tick);
                Assert.Equal(expectedLatestTick, latest.Tick);
            }

            // Verify chronological indexing for all 64 retained items
            for (int k = 0; k < capacity; k++)
            {
                var snap = buffer.GetSnapshotAt(k);
                uint expectedTick = (uint)(totalSnapshots - capacity + k);
                Assert.Equal(expectedTick, snap.Tick);
            }
        }

        [Fact]
        public void SnapshotBuffer_OutOfOrderTimestamps_EmpiricalBehavior()
        {
            var buffer = new SnapshotBuffer(64);

            // Adding snapshots in non-monotonic order
            buffer.AddSnapshot(new TransformSnapshot { Tick = 1, TimestampMs = 100.0, PositionX = 10f });
            buffer.AddSnapshot(new TransformSnapshot { Tick = 3, TimestampMs = 300.0, PositionX = 30f });
            buffer.AddSnapshot(new TransformSnapshot { Tick = 2, TimestampMs = 200.0, PositionX = 20f }); // Out of order!

            // Out-of-order packet dropped: buffer count remains 2
            Assert.Equal(2, buffer.Count);

            // Query at renderTime = 150
            bool found = buffer.TryGetSnapshotsForTime(150.0, out var s0, out var s1, out double t);
            Assert.True(found);

            Assert.Equal(100.0, s0.TimestampMs);
            Assert.Equal(300.0, s1.TimestampMs);
            Assert.Equal(0.25, t, 2);
        }

        [Fact]
        public void SnapshotBuffer_TimestampsInTheFutureAndPast_ClampsGracefully()
        {
            var buffer = new SnapshotBuffer(64);
            buffer.AddSnapshot(new TransformSnapshot { Tick = 10, TimestampMs = 1000.0, PositionX = 10f });
            buffer.AddSnapshot(new TransformSnapshot { Tick = 11, TimestampMs = 1033.3, PositionX = 11f });
            buffer.AddSnapshot(new TransformSnapshot { Tick = 12, TimestampMs = 1066.6, PositionX = 12f });

            // Case A: renderTime in the past (< oldest timestamp)
            bool foundPast = buffer.TryGetSnapshotsForTime(500.0, out var pastS0, out var pastS1, out double pastT);
            Assert.True(foundPast);
            Assert.Equal(1000.0, pastS0.TimestampMs);
            Assert.Equal(1000.0, pastS1.TimestampMs);
            Assert.Equal(0.0, pastT);

            // Case B: renderTime in the future (> newest timestamp)
            bool foundFuture = buffer.TryGetSnapshotsForTime(2000.0, out var futS0, out var futS1, out double futT);
            Assert.True(foundFuture);
            Assert.Equal(1066.6, futS0.TimestampMs);
            Assert.Equal(1066.6, futS1.TimestampMs);
            Assert.Equal(1.0, futT);
        }

        [Fact]
        public void DeadReckoning_ExtremeTimeGaps_TriggersCutoffAndSnap()
        {
            // Case 1: Gap of 500ms (0.5s) exceeds MaxExtrapolationSeconds (0.250s)
            float startX = 0f; float startY = 0f;
            float vx = 10f; float vy = 0f;

            var (extrapX500, extrapY500) = DeadReckoning.ExtrapolatePosition(
                startX, startY, vx, vy,
                isGrounded: true,
                deltaSeconds: 0.500f,
                dampingRate: 1.5f);

            var (extrapX250, extrapY250) = DeadReckoning.ExtrapolatePosition(
                startX, startY, vx, vy,
                isGrounded: true,
                deltaSeconds: 0.250f,
                dampingRate: 1.5f);

            // Position at 500ms MUST equal position at 250ms due to MaxExtrapolationSeconds clamping
            Assert.Equal(extrapX250, extrapX500, 3);
            Assert.Equal(extrapY250, extrapY500, 3);

            // Case 2: Extreme time gap of 2.0s
            var (extrapX2000, _) = DeadReckoning.ExtrapolatePosition(
                startX, startY, vx, vy,
                isGrounded: true,
                deltaSeconds: 2.0f);

            Assert.Equal(extrapX250, extrapX2000, 3);

            // Case 3: Snap distance threshold (2.5 units) triggers snap
            float targetX = 5.0f; float targetY = 0.0f;
            bool shouldSnap = DeadReckoning.ShouldSnap(extrapX500, extrapY500, targetX, targetY, threshold: 2.5f);
            Assert.True(shouldSnap, "Drift beyond 2.5 units should trigger instant snap");
        }

        #endregion

        #region 3. Concurrency Stress: Socket Polling & Queue Draining

        [Fact]
        public async Task LiteNetLibTransport_ConcurrentSendAndPoll_ThreadSafetyStress()
        {
            using var host = new LiteNetLibTransport(1, "StressHost");
            using var client = new LiteNetLibTransport(2, "StressClient");

            host.StartHost(0);
            int hostPort = host.BoundPort;
            Assert.True(hostPort > 0);

            client.Connect($"127.0.0.1:{hostPort}");

            // Complete handshake
            for (int i = 0; i < 20; i++)
            {
                host.PollEvents();
                client.PollEvents();
                if (host.IsConnected && client.IsConnected) break;
                await Task.Delay(10);
            }

            Assert.True(host.IsConnected, "Host failed to connect in loopback test");
            Assert.True(client.IsConnected, "Client failed to connect in loopback test");

            var hostReceivedBytes = new ConcurrentBag<int>();
            host.OnDataReceived += (peer, data, offset, length) =>
            {
                hostReceivedBytes.Add(length);
            };

            var clientReceivedBytes = new ConcurrentBag<int>();
            client.OnDataReceived += (peer, data, offset, length) =>
            {
                clientReceivedBytes.Add(length);
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var token = cts.Token;

            // Worker threads sending packets concurrently. LongRunning gives the workers
            // dedicated threads, so a busy xUnit thread pool cannot starve them past the
            // timeout (the old test occasionally recorded zero sends on loaded CI).
            int sentPackets = 0;
            var sendTask1 = Task.Factory.StartNew(() =>
            {
                byte[] payload = new byte[32];
                while (!token.IsCancellationRequested)
                {
                    client.Send(host.LocalPeerId, payload, 0, payload.Length, DeliveryMethod.Unreliable, 0);
                    Interlocked.Increment(ref sentPackets);
                    Thread.SpinWait(50);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            var sendTask2 = Task.Factory.StartNew(() =>
            {
                byte[] payload = new byte[32];
                while (!token.IsCancellationRequested)
                {
                    host.Send(client.LocalPeerId, payload, 0, payload.Length, DeliveryMethod.ReliableOrdered, 1);
                    Interlocked.Increment(ref sentPackets);
                    Thread.SpinWait(50);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            // Poller threads continuously draining events
            var pollTask1 = Task.Factory.StartNew(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    host.PollEvents();
                    Thread.SpinWait(100);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            var pollTask2 = Task.Factory.StartNew(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    client.PollEvents();
                    Thread.SpinWait(100);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            await Task.WhenAll(sendTask1, sendTask2, pollTask1, pollTask2);

            Assert.True(sentPackets > 100, $"Expected high packet throughput, got {sentPackets}");
            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);
        }

        [Fact]
        public void MockNetworkTransport_ConcurrentEnqueueAndPoll_ThreadSafety()
        {
            var (host, client) = MockNetworkTransport.CreatePair(useVirtualTime: true);

            var receivedData = new ConcurrentBag<int>();
            client.OnDataReceived += (peer, data, offset, length) =>
            {
                receivedData.Add(BitConverter.ToInt32(data, offset));
            };

            const int totalPackets = 1000;
            var exceptions = new ConcurrentBag<Exception>();

            // Concurrent producers calling Send / EnqueueIncoming
            Parallel.For(0, totalPackets, i =>
            {
                try
                {
                    byte[] data = BitConverter.GetBytes(i);
                    host.Send(client.LocalPeerId, data, 0, data.Length, DeliveryMethod.Unreliable, 0);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            Assert.Empty(exceptions);

            // Single-threaded PollEvents to drain (matching Unity main thread execution model)
            client.PollEvents();

            Assert.NotEmpty(receivedData);
        }

        [Fact]
        public async Task SnapshotBuffer_ConcurrentAddAndQuery_ThreadSafetyStress()
        {
            var buffer = new SnapshotBuffer(64);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var token = cts.Token;
            var exceptions = new ConcurrentBag<Exception>();

            // Producer thread: adding snapshots at high rate (simulating network recv)
            var producer = Task.Run(() =>
            {
                uint tick = 0;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        buffer.AddSnapshot(new TransformSnapshot
                        {
                            Tick = tick++,
                            TimestampMs = tick * 16.66,
                            PositionX = tick * 0.1f,
                            PositionY = 5.0f
                        });
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                    Thread.SpinWait(50);
                }
            });

            // Consumer thread: querying snapshots for render interpolation (simulating render thread)
            var consumer = Task.Run(() =>
            {
                double queryTime = 100.0;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        buffer.TryGetSnapshotsForTime(queryTime, out _, out _, out _);
                        queryTime += 5.0;
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                    Thread.SpinWait(20);
                }
            });

            await Task.WhenAll(producer, consumer);

            // SnapshotBuffer does not have internal locks; let's record whether exceptions occurred
            Assert.Empty(exceptions);
        }

        [Fact]
        public void LiteNetLibTransport_DisconnectInsideDataCallback_HandledGracefullyWithoutException()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            using var client = new LiteNetLibTransport(2, "Client");

            host.StartHost(0);
            client.Connect($"127.0.0.1:{host.BoundPort}");

            for (int i = 0; i < 20 && (!host.IsConnected || !client.IsConnected); i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(10);
            }

            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);

            // Send multiple packets so socket has pending bytes
            byte[] data = new byte[16];
            client.Send(host.LocalPeerId, data, 0, data.Length, DeliveryMethod.Unreliable, 0);
            client.Send(host.LocalPeerId, data, 0, data.Length, DeliveryMethod.Unreliable, 0);

            bool callbackInvoked = false;
            host.OnDataReceived += (peer, d, off, len) =>
            {
                callbackInvoked = true;
                host.Disconnect();
            };

            var ex = Record.Exception(() => host.PollEvents());
            Assert.Null(ex);
            Assert.True(callbackInvoked);
            Assert.False(host.IsConnected);
        }

        [Fact]
        public void SnapshotBuffer_OutOfOrderArrival_DropsStaleSnapshotAndPreservesInterpolation()
        {
            var buffer = new SnapshotBuffer(64);
            buffer.AddSnapshot(new TransformSnapshot { Tick = 10, TimestampMs = 1000.0, PositionX = 10f });
            buffer.AddSnapshot(new TransformSnapshot { Tick = 12, TimestampMs = 1066.0, PositionX = 12f });
            // Out-of-order packet arrives: Tick 11 (Timestamp 1033) arrived after Tick 12
            buffer.AddSnapshot(new TransformSnapshot { Tick = 11, TimestampMs = 1033.0, PositionX = 11f });

            Assert.Equal(2, buffer.Count);

            // Query at renderTime = 1050 (between 1000 and 1066)
            bool found = buffer.TryGetSnapshotsForTime(1050.0, out var s0, out var s1, out double t);
            Assert.True(found);

            Assert.Equal((uint)10, s0.Tick);
            Assert.Equal((uint)12, s1.Tick);
            Assert.Equal(1000.0, s0.TimestampMs);
            Assert.Equal(1066.0, s1.TimestampMs);
            Assert.True(t >= 0.75 && t <= 0.77, $"Expected t between 0.75 and 0.77, got {t}");
        }

        #endregion
    }
}
