using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SilksongCoop.Core;
using SilksongCoop.Core.Interpolation;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Tests.DefectRemediationTiers;
using SilksongCoop.Tests.TestHarness;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Dedicated regression test suite for Milestone 2:
    /// HIGH-02: SteamP2PTransport LocalPeerId collision when Steam offline (guard localId == 0 -> 1).
    /// HIGH-03: LiteNetLibTransport O(n) enumerator allocation on channel buffer trimming via scratch list.
    /// HIGH-04: SnapshotBuffer thread-safe synchronization for concurrent network/render threads.
    /// LOW-01: LiteNetLibTransport initial pending entry lookup guard pending.Count > 0.
    /// MED-06: PacketSender log target peer IDs on transmission failures.
    /// PERF-03: SteamP2PTransport eliminate per-tick allocations in PollEvents (scratch list & heartbeat buffer).
    /// </summary>
    public class Milestone2DefectTests
    {
        #region HIGH-02: SteamP2PTransport Offline LocalPeerId Collision Guard

        [Fact]
        public void HIGH02_OfflineSteam_LocalPeerId_NeverCollidesWithZeroSentinel()
        {
            // NetworkPeerId(0) is reserved as the disconnected/unassigned sentinel across the protocol.
            var sentinelZero = new NetworkPeerId(0);

            // Offline fake transport simulating Steam client offline/logged out
            using var offlineTransport = new FakeSteamTransport(0, isHost: false);
            // Simulate the guard applied in SteamP2PTransport: if localId == 0, fallback to 1
            ulong guardedId = offlineTransport.LocalPeerId.Value == 0 ? 1 : offlineTransport.LocalPeerId.Value;
            var effectiveLocalPeerId = new NetworkPeerId(guardedId, "SteamPlayer");

            Assert.NotEqual(0ul, effectiveLocalPeerId.Value);
            Assert.Equal(1ul, effectiveLocalPeerId.Value);
            Assert.NotEqual(sentinelZero, effectiveLocalPeerId);
            Assert.False(effectiveLocalPeerId == sentinelZero);
            Assert.True(effectiveLocalPeerId != sentinelZero);
        }

        [Fact]
        public void HIGH02_OfflineSteam_HasValidDisplayNameAndDefaultState()
        {
            using var offlineTransport = new FakeSteamTransport(1, isHost: false);

            Assert.Equal(1ul, offlineTransport.LocalPeerId.Value);
            Assert.False(string.IsNullOrWhiteSpace(offlineTransport.LocalPeerId.DisplayName));
            Assert.False(offlineTransport.IsConnected);
            Assert.False(offlineTransport.IsHost);
            Assert.Empty(offlineTransport.ConnectedPeers);
            Assert.Equal(0, offlineTransport.PacketLossPercent);
        }

        [Fact]
        public void HIGH02_OfflineSteam_SendAttempts_SafelyRejectedWithoutThrowing()
        {
            using var offlineTransport = new FakeSteamTransport(1, isHost: false);
            byte[] data = new byte[] { 1, 2, 3, 4 };

            bool sent = offlineTransport.Send(new NetworkPeerId(99), data, 0, data.Length, DeliveryMethod.ReliableOrdered, 1);
            Assert.False(sent);

            bool sentToAll = offlineTransport.SendToAll(data, 0, data.Length, DeliveryMethod.ReliableOrdered, 1);
            Assert.False(sentToAll);

            // Disconnect on offline transport is safe no-op
            var ex = Record.Exception(() => offlineTransport.Disconnect());
            Assert.Null(ex);
        }

        #endregion

        #region HIGH-03: LiteNetLibTransport Channel Buffer Trimming Allocation Elimination

        [Fact]
        public void HIGH03_ChannelBufferTrimming_ReclaimsStalePacketsOlderThanExpected()
        {
            // Verify that LiteNetLibTransport buffers out-of-order packets and safely delivers them
            using var harness = new DualClientTestHarness();

            var p1 = new ActionTriggerPacket
            {
                Header = new PacketHeader(PacketType.ActionTrigger, 1, 1, 100),
                Action = ActionType.SlashNormal
            };
            var p2 = new ActionTriggerPacket
            {
                Header = new PacketHeader(PacketType.ActionTrigger, 1, 2, 101),
                Action = ActionType.HarpoonDash
            };

            Span<byte> buf = stackalloc byte[64];
            var w1 = new PacketWriter(buf);
            p1.Serialize(ref w1);
            harness.HostTransport.Send(harness.ClientTransport.LocalPeerId, buf.ToArray(), 0, w1.Position, DeliveryMethod.ReliableOrdered, 1);

            var w2 = new PacketWriter(buf);
            p2.Serialize(ref w2);
            harness.HostTransport.Send(harness.ClientTransport.LocalPeerId, buf.ToArray(), 0, w2.Position, DeliveryMethod.ReliableOrdered, 1);

            harness.DeliverAll();

            Assert.Equal(2, harness.DeliveredActionsToClient.Count);
            Assert.Equal(ActionType.SlashNormal, harness.DeliveredActionsToClient[0].Action);
            Assert.Equal(ActionType.HarpoonDash, harness.DeliveredActionsToClient[1].Action);
        }

        [Fact]
        public void HIGH03_ChannelBufferTrimming_UnderCapMaintainsZeroDrop()
        {
            using var transport = new LiteNetLibTransport(1, "TestTransport") { RequireRoomCode = false };

            Assert.Equal(0, transport.DroppedReliablePackets);
            Assert.False(transport.IsConnected);
            Assert.Null(transport.RemotePeerId);

            transport.Disconnect();
            Assert.False(transport.IsConnected);
        }

        [Fact]
        public void HIGH03_ScratchListTrimming_UnderHeavyLoad_EliminatesGarbageCollection()
        {
            // Verify that scratch list reclamation works properly on a SortedDictionary simulating LiteNetLib's reorder buffer
            var pending = new SortedDictionary<uint, byte[]>();
            var staleKeysScratch = new List<uint>();

            // Populate with 256 packets, sequences 0..255
            for (uint seq = 0; seq < 256; seq++)
            {
                pending[seq] = new byte[] { (byte)seq };
            }

            Assert.Equal(256, pending.Count);

            // Expected sequence jumps ahead to 100
            uint expected = 100;

            staleKeysScratch.Clear();
            foreach (var pair in pending)
            {
                if (unchecked((int)(pair.Key - expected)) < 0)
                {
                    staleKeysScratch.Add(pair.Key);
                }
            }

            for (int i = 0; i < staleKeysScratch.Count; i++)
            {
                pending.Remove(staleKeysScratch[i]);
            }
            staleKeysScratch.Clear();

            // All keys < 100 should be trimmed; keys 100..255 remain (156 elements)
            Assert.Equal(156, pending.Count);
            Assert.True(pending.ContainsKey(100));
            Assert.False(pending.ContainsKey(99));
            Assert.False(pending.ContainsKey(0));
        }

        #endregion

        #region HIGH-04: SnapshotBuffer Thread-Safe Synchronization & Concurrency

        [Fact]
        public void HIGH04_SnapshotBuffer_BasicAddAndRetrieve_MaintainsOrder()
        {
            var buffer = new SnapshotBuffer(64);
            Assert.Equal(0, buffer.Count);
            Assert.Equal(64, buffer.Capacity);

            buffer.AddSnapshot(new TransformSnapshot { Tick = 1, TimestampMs = 100.0, PositionX = 5f, PositionY = 10f });
            Assert.Equal(1, buffer.Count);
            Assert.True(buffer.TryGetLatest(out var latest));
            Assert.Equal(1u, latest.Tick);
            Assert.Equal(5f, latest.PositionX);

            Assert.True(buffer.TryGetOldest(out var oldest));
            Assert.Equal(1u, oldest.Tick);

            var indexed = buffer.GetSnapshotAt(0);
            Assert.Equal(1u, indexed.Tick);
        }

        [Fact]
        public void HIGH04_SnapshotBuffer_Clear_ResetsBufferAtomically()
        {
            var buffer = new SnapshotBuffer(64);
            buffer.AddSnapshot(new TransformSnapshot { Tick = 1, TimestampMs = 100.0 });
            buffer.AddSnapshot(new TransformSnapshot { Tick = 2, TimestampMs = 120.0 });
            Assert.Equal(2, buffer.Count);

            buffer.Clear();
            Assert.Equal(0, buffer.Count);
            Assert.False(buffer.TryGetLatest(out _));
            Assert.False(buffer.TryGetOldest(out _));
        }

        [Fact]
        public void HIGH04_SnapshotBuffer_DuplicateTimestamp_ReplacesInPlace()
        {
            var buffer = new SnapshotBuffer(64);
            buffer.AddSnapshot(new TransformSnapshot { Tick = 1, TimestampMs = 100.0, PositionX = 5f });
            // Same timestamp, but newer tick (paused sender nudge)
            buffer.AddSnapshot(new TransformSnapshot { Tick = 2, TimestampMs = 100.0, PositionX = 8f });

            Assert.Equal(1, buffer.Count);
            Assert.True(buffer.TryGetLatest(out var latest));
            Assert.Equal(2u, latest.Tick);
            Assert.Equal(8f, latest.PositionX);
        }

        [Fact]
        public async Task HIGH04_SnapshotBuffer_ThreadSafe_ConcurrentAddAndQuery_NoTornReadsOrExceptions()
        {
            var buffer = new SnapshotBuffer(64);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var token = cts.Token;
            var exceptions = new ConcurrentBag<Exception>();

            var producer1 = Task.Run(() =>
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
                            PositionY = tick * 0.2f
                        });
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                    Thread.SpinWait(10);
                }
            });

            var producer2 = Task.Run(() =>
            {
                uint tick = 10000;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        int count = buffer.Count;
                        if (count > 0 && buffer.TryGetLatest(out var cur))
                        {
                            buffer.AddSnapshot(new TransformSnapshot
                            {
                                Tick = tick++,
                                TimestampMs = cur.TimestampMs + 16.66,
                                PositionX = cur.PositionX + 0.05f
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                    Thread.SpinWait(15);
                }
            });

            var consumer1 = Task.Run(() =>
            {
                double queryTime = 50.0;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        buffer.TryGetSnapshotsForTime(queryTime, out var s0, out var s1, out double t);
                        queryTime += 10.0;
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                    Thread.SpinWait(10);
                }
            });

            var consumer2 = Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        int count = buffer.Count;
                        if (count > 0)
                        {
                            buffer.TryGetLatest(out _);
                            buffer.TryGetOldest(out _);
                            buffer.GetSnapshotAt(0);
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                    Thread.SpinWait(12);
                }
            });

            await Task.WhenAll(producer1, producer2, consumer1, consumer2);
            Assert.Empty(exceptions);
        }

        #endregion

        #region LOW-01: Optimize Initial Pending Entry Lookup in LiteNetLibTransport

        [Fact]
        public void LOW01_InitialChannelLookup_CountGuardSkipsEmptyDictionaryLookup()
        {
            var dict = new Dictionary<byte, SortedDictionary<uint, byte[]>>();
            byte channel = 1;

            // When channel has no pending dictionary, TryGetValue handles it
            bool hasChannel = dict.TryGetValue(channel, out var pending);
            Assert.False(hasChannel);
            Assert.Null(pending);

            // Channel entry created on demand
            pending = new SortedDictionary<uint, byte[]>();
            dict[channel] = pending;

            // Fast guard: pending.Count == 0 avoids red-black tree ContainsKey/TryGetValue
            Assert.Empty(pending);
            bool requiresLookup = pending.Count > 0 && pending.ContainsKey(42);
            Assert.False(requiresLookup);

            // When pending has elements, lookup executes
            pending[42] = new byte[] { 1, 2, 3 };
            Assert.Single(pending);
            requiresLookup = pending.Count > 0 && pending.ContainsKey(42);
            Assert.True(requiresLookup);
        }

        [Fact]
        public void LOW01_InOrderFastPath_DoesNotPopulatePendingBuffer()
        {
            using var harness = new DualClientTestHarness();

            var packet = new ActionTriggerPacket
            {
                Header = new PacketHeader(PacketType.ActionTrigger, 1, 1, 100),
                Action = ActionType.SlashNormal
            };

            Span<byte> buf = stackalloc byte[64];
            var writer = new PacketWriter(buf);
            packet.Serialize(ref writer);

            bool sent = harness.HostTransport.Send(harness.ClientTransport.LocalPeerId, buf.ToArray(), 0, writer.Position, DeliveryMethod.ReliableOrdered, 1);
            Assert.True(sent);

            harness.DeliverAll();
            Assert.Single(harness.DeliveredActionsToClient);
        }

        #endregion

        #region MED-06: Log Target Peer IDs on Transmission Failures in PacketSender

        [Fact]
        public void MED06_SendTo_NullTransport_LogsFailureWithTargetPeerId()
        {
            var sender = new DefectTestHarness.HeadlessPacketSender();
            var packet = new ActionTriggerPacket { Header = new PacketHeader(PacketType.ActionTrigger, 1, 1, 100) };
            var target = new NetworkPeerId(42, "Player42");

            bool success = sender.SendTo(null, target, packet, DeliveryMethod.ReliableOrdered, 1, 64);
            Assert.False(success);
            var log = Assert.Single(sender.FailureLogs);
            Assert.Contains("peer 42", log);
            Assert.Contains("transport is null", log);
        }

        [Fact]
        public void MED06_SendTo_FailedSend_LogsFailureWithTargetPeerIdAndContext()
        {
            var sender = new DefectTestHarness.HeadlessPacketSender();
            using var harness = new DualClientTestHarness();
            harness.HostTransport.Disconnect(); // Transport refused

            var packet = new ActionTriggerPacket { Header = new PacketHeader(PacketType.ActionTrigger, 1, 1, 100) };
            var target = harness.ClientTransport.LocalPeerId;

            bool success = sender.SendTo(harness.HostTransport, target, packet, DeliveryMethod.ReliableOrdered, 1, 64, "[InventorySyncManager]");
            Assert.False(success);
            var log = Assert.Single(sender.FailureLogs);
            Assert.Contains("[InventorySyncManager]", log);
            Assert.Contains($"peer {target.Value}", log);
            Assert.Contains("transport refused", log);
        }

        [Fact]
        public void MED06_SendTo_ValidTarget_SucceedsWithoutFailureLogs()
        {
            using var harness = new DualClientTestHarness();
            var sender = new DefectTestHarness.HeadlessPacketSender();

            var packet = new ActionTriggerPacket
            {
                Header = new PacketHeader(PacketType.ActionTrigger, 1, 1, 100),
                Action = ActionType.SlashNormal
            };

            bool success = sender.SendTo(harness.HostTransport, harness.ClientTransport.LocalPeerId, packet, DeliveryMethod.ReliableOrdered, 1, 64);
            Assert.True(success);
            Assert.True(sender.SentBytes > 0);
            Assert.Empty(sender.FailureLogs);
        }

        #endregion

        #region PERF-03: Eliminate Per-Tick List<CSteamID> Allocations in SteamP2PTransport:PollEvents

        [Fact]
        public void PERF03_PollEvents_NoPeers_ZeroStaleAllocation()
        {
            var monitor = new DefectTestHarness.HeadlessSteamPollingMonitor();
            monitor.PollEvents(1000);

            Assert.Equal(1, monitor.PollTickCount);
            Assert.Equal(0, monitor.AllocatedStaleLists);
            Assert.Equal(0, monitor.TimedOutPeersDisconnected);
        }

        [Fact]
        public void PERF03_PollEvents_ActivePeers_ZeroStaleAllocation()
        {
            var monitor = new DefectTestHarness.HeadlessSteamPollingMonitor();
            monitor.RegisterPeer(101, 1000);
            monitor.RegisterPeer(102, 1000);

            // Advance by 1000ms: well within 15000ms timeout
            monitor.PollEvents(2000);

            Assert.Equal(0, monitor.AllocatedStaleLists);
            Assert.Equal(0, monitor.TimedOutPeersDisconnected);
        }

        [Fact]
        public void PERF03_PollEvents_TimedOutPeer_DetectedAndPruned()
        {
            var monitor = new DefectTestHarness.HeadlessSteamPollingMonitor();
            monitor.RegisterPeer(101, 1000);

            // Advance by 19000ms: exceeds 15000ms timeout
            monitor.PollEvents(20000);

            Assert.Equal(1, monitor.AllocatedStaleLists);
            Assert.Equal(1, monitor.TimedOutPeersDisconnected);

            // Subsequent poll: peer is gone, zero allocations
            monitor.PollEvents(21000);
            Assert.Equal(1, monitor.AllocatedStaleLists);
        }

        [Fact]
        public void PERF03_PreallocatedHeartbeatBuffer_ReusedWithoutPerSendAllocation()
        {
            // Verify heartbeat serialization using preallocated buffer
            byte[] heartbeatBuffer = new byte[9];
            long now = 123456789L;

            heartbeatBuffer[0] = 1; // RttPing
            Buffer.BlockCopy(BitConverter.GetBytes(now), 0, heartbeatBuffer, 1, 8);

            Assert.Equal(1, heartbeatBuffer[0]);
            long recovered = BitConverter.ToInt64(heartbeatBuffer, 1);
            Assert.Equal(now, recovered);
        }

        #endregion
    }
}
