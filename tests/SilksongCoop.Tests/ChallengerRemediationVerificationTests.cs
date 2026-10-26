using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SilksongCoop.Core.Interpolation;
using SilksongCoop.Core.Networking;
using Xunit;

namespace SilksongCoop.Tests
{
    public class ChallengerRemediationVerificationTests
    {
        #region 1. LiteNetLibTransport Socket Polling Reentrancy & Concurrency Stress

        [Fact]
        public void LiteNetLibTransport_DisconnectInsideDataCallback_QueueOf50Packets_GracefullyAbortsWithoutException()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            using var client = new LiteNetLibTransport(2, "Client");

            host.StartHost(0);
            client.Connect($"127.0.0.1:{host.BoundPort}");

            for (int i = 0; i < 30 && (!host.IsConnected || !client.IsConnected); i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(10);
            }

            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);

            // Queue 50 datagrams to host so host socket buffer is full of pending packets
            for (int i = 0; i < 50; i++)
            {
                byte[] data = BitConverter.GetBytes(i);
                client.Send(host.LocalPeerId, data, 0, data.Length, DeliveryMethod.Unreliable, 0);
            }

            // Small sleep to ensure UDP packets arrived at the OS socket buffer
            Thread.Sleep(30);

            int callbackCount = 0;
            host.OnDataReceived += (peer, data, offset, len) =>
            {
                callbackCount++;
                // Reentrant disconnect on the very first datagram processed
                host.Disconnect();
            };

            // Polling events should process datagram 1, trigger Disconnect(), and exit loop cleanly
            // without throwing NullReferenceException or ObjectDisposedException
            var ex = Record.Exception(() => host.PollEvents());

            Assert.Null(ex);
            Assert.Equal(1, callbackCount);
            Assert.False(host.IsConnected);
            Assert.Null(host.RemotePeerId);
        }

        [Fact]
        public void LiteNetLibTransport_DisconnectInsideOnPeerConnected_GracefullyAbortsWithoutException()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            using var client = new LiteNetLibTransport(2, "Client");

            host.StartHost(0);

            bool hostConnectedFired = false;
            host.OnPeerConnected += peer =>
            {
                hostConnectedFired = true;
                // Immediate disconnect upon connection
                host.Disconnect();
            };

            client.Connect($"127.0.0.1:{host.BoundPort}");

            var ex = Record.Exception(() =>
            {
                for (int i = 0; i < 30 && !hostConnectedFired; i++)
                {
                    host.PollEvents();
                    client.PollEvents();
                    Thread.Sleep(10);
                }
            });

            Assert.Null(ex);
            Assert.True(hostConnectedFired);
            Assert.False(host.IsConnected);
        }

        [Fact]
        public void LiteNetLibTransport_DisconnectInsideOnPeerDisconnected_MultipleDisconnectCalls_Idempotent()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            using var client = new LiteNetLibTransport(2, "Client");

            host.StartHost(0);
            client.Connect($"127.0.0.1:{host.BoundPort}");

            for (int i = 0; i < 30 && (!host.IsConnected || !client.IsConnected); i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(10);
            }

            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);

            int disconnectCallbacks = 0;
            client.OnPeerDisconnected += (peer, reason) =>
            {
                disconnectCallbacks++;
                // Multiple redundant disconnect calls inside the callback
                client.Disconnect();
                client.Disconnect(DisconnectReason.Timeout);
                client.Disconnect(DisconnectReason.ConnectionFailed);
            };

            host.Disconnect();

            var ex = Record.Exception(() =>
            {
                for (int i = 0; i < 20; i++)
                {
                    client.PollEvents();
                    Thread.Sleep(5);
                }
            });

            Assert.Null(ex);
            Assert.Equal(1, disconnectCallbacks);
            Assert.False(client.IsConnected);
        }

        [Fact]
        public async Task LiteNetLibTransport_HighContention_ConcurrentSendPollDisconnectRestart_Stress()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            using var client = new LiteNetLibTransport(2, "Client");

            host.StartHost(0);
            client.Connect($"127.0.0.1:{host.BoundPort}");

            var exceptions = new ConcurrentBag<Exception>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.0));

            var t1 = Task.Run(() =>
            {
                int counter = 0;
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        byte[] msg = BitConverter.GetBytes(counter++);
                        client.Send(host.LocalPeerId, msg, 0, msg.Length, DeliveryMethod.Unreliable, 0);
                        client.PollEvents();
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                    Thread.SpinWait(100);
                }
            });

            var t2 = Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        host.PollEvents();
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                    Thread.SpinWait(100);
                }
            });

            var t3 = Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        // Periodic disconnect/reconnect cycle under active polling
                        Thread.Sleep(50);
                        if (client.IsConnected)
                        {
                            client.Disconnect();
                        }
                        else
                        {
                            client.Connect($"127.0.0.1:{host.BoundPort}");
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }
            });

            await Task.WhenAll(t1, t2, t3);
            Assert.Empty(exceptions);
        }

        #endregion

        #region 2. SnapshotBuffer Monotonicity, Out-Of-Order Rejection & Interpolation Sweep

        [Fact]
        public void SnapshotBuffer_OutOfOrderIngestion_MonotonicRenderTimeSweep_NoBackwardJumps()
        {
            var buffer = new SnapshotBuffer(64);

            // Chronological base packets: linear motion from x=10 to x=40
            buffer.AddSnapshot(new TransformSnapshot { Tick = 10, TimestampMs = 1000.0, PositionX = 10f, VelocityX = 300f });
            buffer.AddSnapshot(new TransformSnapshot { Tick = 12, TimestampMs = 1033.3, PositionX = 20f, VelocityX = 300f });
            buffer.AddSnapshot(new TransformSnapshot { Tick = 14, TimestampMs = 1066.6, PositionX = 30f, VelocityX = 300f });
            buffer.AddSnapshot(new TransformSnapshot { Tick = 16, TimestampMs = 1100.0, PositionX = 40f, VelocityX = 300f });

            // Now adversarial out-of-order packets arrive late (network jitter)
            buffer.AddSnapshot(new TransformSnapshot { Tick = 11, TimestampMs = 1016.6, PositionX = 15f, VelocityX = 300f }); // Stale
            buffer.AddSnapshot(new TransformSnapshot { Tick = 13, TimestampMs = 1050.0, PositionX = 25f, VelocityX = 300f }); // Stale
            buffer.AddSnapshot(new TransformSnapshot { Tick = 15, TimestampMs = 1083.3, PositionX = 35f, VelocityX = 300f }); // Stale
            buffer.AddSnapshot(new TransformSnapshot { Tick = 9,  TimestampMs = 983.3,  PositionX = 5f,  VelocityX = 300f }); // Stale

            // All 4 stale packets MUST have been dropped to preserve chronological order
            Assert.Equal(4, buffer.Count);

            // Verify chronological integrity of stored snapshots
            for (int i = 0; i < buffer.Count - 1; i++)
            {
                Assert.True(buffer.GetSnapshotAt(i).TimestampMs < buffer.GetSnapshotAt(i + 1).TimestampMs,
                    $"Snapshot at index {i} ({buffer.GetSnapshotAt(i).TimestampMs}) is not strictly less than index {i+1} ({buffer.GetSnapshotAt(i+1).TimestampMs})");
            }

            // Continuous render time sweep from 950ms to 1150ms in 0.5ms increments (400 sample points)
            float previousPos = -float.MaxValue;
            for (double tRender = 950.0; tRender <= 1150.0; tRender += 0.5)
            {
                bool ok = buffer.TryGetSnapshotsForTime(tRender, out var s0, out var s1, out double t);
                Assert.True(ok);

                float dt = (float)(s1.TimestampMs - s0.TimestampMs) * 0.001f;
                float currentPos;
                if (dt <= 0.0001f)
                {
                    currentPos = s0.PositionX;
                }
                else
                {
                    currentPos = HermiteSpline.InterpolatePosition(s0.PositionX, s0.VelocityX, s1.PositionX, s1.VelocityX, dt, (float)t);
                }

                // Verify monotonically non-decreasing position: no backward teleportation or rubberbanding!
                Assert.True(currentPos >= previousPos - 1e-4f,
                    $"Backward jump detected at renderTime={tRender}: currentPos={currentPos} < previousPos={previousPos}");
                previousPos = currentPos;
            }
        }

        [Fact]
        public void SnapshotBuffer_DuplicateTimestamps_DroppedSafely()
        {
            var buffer = new SnapshotBuffer(64);

            buffer.AddSnapshot(new TransformSnapshot { Tick = 10, TimestampMs = 1000.0, PositionX = 10f });
            buffer.AddSnapshot(new TransformSnapshot { Tick = 10, TimestampMs = 1000.0, PositionX = 10f });
            buffer.AddSnapshot(new TransformSnapshot { Tick = 10, TimestampMs = 1000.0, PositionX = 10f });

            Assert.Equal(1, buffer.Count);

            bool ok = buffer.TryGetSnapshotsForTime(1000.0, out var s0, out var s1, out double t);
            Assert.True(ok);
            Assert.Equal(10f, s0.PositionX);
            Assert.Equal(10f, s1.PositionX);
            // With single-snapshot dead reckoning fix, renderTime >= s0.TimestampMs yields t = 1.0
            Assert.Equal(1.0, t);
        }

        [Fact]
        public void SnapshotBuffer_ClockWrapOrSessionReset_ClearsAndResets()
        {
            var buffer = new SnapshotBuffer(64);

            // Previous session at high timestamp
            buffer.AddSnapshot(new TransformSnapshot { Tick = 1000, TimestampMs = 50000.0, PositionX = 100f });
            buffer.AddSnapshot(new TransformSnapshot { Tick = 1001, TimestampMs = 50033.0, PositionX = 101f });
            Assert.Equal(2, buffer.Count);

            // New session starts at 1000ms (> 10,000ms in the past)
            buffer.AddSnapshot(new TransformSnapshot { Tick = 1, TimestampMs = 1000.0, PositionX = 5f });

            // Buffer should have cleared the old session and accepted the new snapshot
            Assert.Equal(1, buffer.Count);
            Assert.True(buffer.TryGetLatest(out var latest));
            Assert.Equal((uint)1, latest.Tick);
            Assert.Equal(1000.0, latest.TimestampMs);
            Assert.Equal(5f, latest.PositionX);
        }

        [Fact]
        public void SnapshotBuffer_StressInterleavedRandomArrivals_ChronologicalOrderStrictlyPreserved()
        {
            var buffer = new SnapshotBuffer(64);
            var rng = new Random(42);

            double currentVirtualTime = 1000.0;
            uint tick = 0;

            for (int i = 0; i < 500; i++)
            {
                // Advance virtual time
                currentVirtualTime += rng.Next(15, 45);
                tick++;

                // Send forward snapshot
                buffer.AddSnapshot(new TransformSnapshot
                {
                    Tick = tick,
                    TimestampMs = currentVirtualTime,
                    PositionX = (float)currentVirtualTime * 0.1f
                });

                // 40% chance to simulate jitter/delayed packet from the past (up to 200ms late)
                if (rng.NextDouble() < 0.4)
                {
                    double staleTimestamp = currentVirtualTime - rng.Next(5, 200);
                    buffer.AddSnapshot(new TransformSnapshot
                    {
                        Tick = tick - 1,
                        TimestampMs = staleTimestamp,
                        PositionX = (float)staleTimestamp * 0.1f
                    });
                }

                // Invariant: all elements currently in buffer must be strictly monotonically increasing in timestamp
                for (int c = 0; c < buffer.Count - 1; c++)
                {
                    var a = buffer.GetSnapshotAt(c);
                    var b = buffer.GetSnapshotAt(c + 1);
                    Assert.True(a.TimestampMs < b.TimestampMs,
                        $"Invariant violated at step {i}: index {c} ({a.TimestampMs}) >= index {c+1} ({b.TimestampMs})");
                }
            }
        }

        #endregion
    }
}
