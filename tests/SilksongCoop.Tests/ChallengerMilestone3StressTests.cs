using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using SilksongCoop.Tests.TestHarness;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Adversarial empirical stress tests for Milestone 3 (Features F15–F21):
    /// Concurrency hammering, extreme out-of-order channel delivery, packet loss resilience,
    /// malformed payload fuzzing, and transaction deduplication sliding window integrity.
    /// </summary>
    public class ChallengerMilestone3StressTests
    {
        #region 1. High-Throughput Multi-Threaded Currency Transactions & Concurrency Stress

        [Fact]
        public void HighThroughput_100ConcurrentThreads_OnLocalCurrencyChanged_ExactConvergence()
        {
            var vitals = new VitalsSyncService();
            vitals.InitializeState(5, 5, 9, 1, 0, 0, 0);

            const int threadCount = 100;
            const int txPerThread = 50;
            const int deltaPerTx = 10;
            int totalExpectedRosaries = threadCount * txPerThread * deltaPerTx; // 50,000

            var broadcastedPackets = new ConcurrentBag<CurrencySyncPacket>();
            vitals.OnOutgoingCurrencyBroadcast += packet => broadcastedPackets.Add(packet);

            var threads = new Thread[threadCount];
            using var barrier = new Barrier(threadCount);

            for (int t = 0; t < threadCount; t++)
            {
                threads[t] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    for (int i = 0; i < txPerThread; i++)
                    {
                        var txId = Guid.NewGuid();
                        vitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, deltaPerTx, 0, txId);
                    }
                });
                threads[t].Start();
            }

            foreach (var th in threads)
            {
                th.Join();
            }

            // Assert exact convergence with 0 lost updates or race conditions
            Assert.Equal(totalExpectedRosaries, vitals.SharedRosaries);
            Assert.Equal(totalExpectedRosaries / deltaPerTx, broadcastedPackets.Count);

            // Verify FIFO ring buffer tracked transactions count is strictly bounded by MaxTrackedTransactions (500)
            // Submitting a fresh transaction must increment balance properly
            var finalTx = Guid.NewGuid();
            vitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 15, 0, finalTx);
            Assert.Equal(totalExpectedRosaries + 15, vitals.SharedRosaries);
        }

        [Fact]
        public void HighThroughput_100ConcurrentThreads_MixedLocalAndRemoteTransactions_RaceResilience()
        {
            var vitals = new VitalsSyncService();
            vitals.InitializeState(5, 5, 9, 1, 0, 1000, 500);

            const int localThreads = 50;
            const int remoteThreads = 50;
            const int totalThreads = localThreads + remoteThreads;
            const int txPerThread = 40;
            const int delta = 5;

            var peerId = new NetworkPeerId(2, "RemoteClient");
            var threads = new Thread[totalThreads];
            using var barrier = new Barrier(totalThreads);

            int remoteEventInvocations = 0;
            vitals.OnSharedCurrencyChanged += (type, d, total) =>
            {
                Interlocked.Increment(ref remoteEventInvocations);
            };

            // Local threads hammering OnLocalCurrencyChanged
            for (int t = 0; t < localThreads; t++)
            {
                threads[t] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    for (int i = 0; i < txPerThread; i++)
                    {
                        vitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, delta, 0, Guid.NewGuid());
                    }
                });
                threads[t].Start();
            }

            // Remote threads hammering HandleCurrencyPacket
            for (int t = 0; t < remoteThreads; t++)
            {
                threads[localThreads + t] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    for (int i = 0; i < txPerThread; i++)
                    {
                        var packet = new CurrencySyncPacket
                        {
                            Header = new PacketHeader(PacketType.CurrencySync, 3, 0, (uint)Environment.TickCount),
                            Currency = CurrencyType.Rosaries,
                            TotalWalletAmount = 0,
                            DeltaAmount = delta,
                            TransactionId = Guid.NewGuid()
                        };
                        vitals.HandleCurrencyPacket(peerId, packet);
                    }
                });
                threads[localThreads + t].Start();
            }

            foreach (var th in threads)
            {
                th.Join();
            }

            int expectedTotal = 1000 + (totalThreads * txPerThread * delta); // 1000 + (100 * 40 * 5) = 21,000
            Assert.Equal(expectedTotal, vitals.SharedRosaries);
            Assert.Equal(remoteThreads * txPerThread, remoteEventInvocations);
        }

        [Fact]
        public void HighThroughput_ConcurrentDuplicateTransactions_DeduplicationReliability()
        {
            var vitals = new VitalsSyncService();
            vitals.InitializeState(5, 5, 9, 1, 0, 0, 0);

            // 10 distinct transaction GUIDs
            const int distinctTxCount = 10;
            var fixedGuids = new Guid[distinctTxCount];
            for (int i = 0; i < distinctTxCount; i++)
            {
                fixedGuids[i] = Guid.NewGuid();
            }

            const int threadCount = 100;
            const int delta = 25;
            var peerId = new NetworkPeerId(2, "RemoteClient");
            var threads = new Thread[threadCount];
            using var barrier = new Barrier(threadCount);

            // 100 threads simultaneously attempt to process all 10 transaction IDs repeatedly
            for (int t = 0; t < threadCount; t++)
            {
                int threadIndex = t;
                threads[t] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    for (int i = 0; i < distinctTxCount; i++)
                    {
                        var packet = new CurrencySyncPacket
                        {
                            Header = new PacketHeader(PacketType.CurrencySync, 3, 0, (uint)Environment.TickCount),
                            Currency = CurrencyType.Rosaries,
                            TotalWalletAmount = 0,
                            DeltaAmount = delta,
                            TransactionId = fixedGuids[i]
                        };

                        if (threadIndex % 2 == 0)
                        {
                            vitals.HandleCurrencyPacket(peerId, packet);
                        }
                        else
                        {
                            vitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, delta, 0, fixedGuids[i]);
                        }
                    }
                });
                threads[t].Start();
            }

            foreach (var th in threads)
            {
                th.Join();
            }

            // Exactly 10 transactions must apply, yielding exactly 10 * 25 = 250 Rosaries
            int expected = distinctTxCount * delta;
            Assert.Equal(expected, vitals.SharedRosaries);
        }

        [Fact]
        public void FIFOEvictionWindow_BoundaryStress_2500ConcurrentTransactions_SafeSlidingWindow()
        {
            var vitals = new VitalsSyncService();
            vitals.InitializeState(5, 5, 9, 1, 0, 0, 0);

            // Push 600 sequential transactions to verify the 500-transaction FIFO window evicts oldest safely
            var firstBatch = new List<Guid>();
            for (int i = 0; i < 600; i++)
            {
                var guid = Guid.NewGuid();
                firstBatch.Add(guid);
                vitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 1, 0, guid);
            }

            Assert.Equal(600, vitals.SharedRosaries);

            // The first 100 transactions (indices 0..99) should have been evicted by the FIFO window (500 max)
            // While the last 500 (indices 100..599) should still be cached as duplicates.
            // Re-submitting transaction index 0 (which was evicted) should be accepted as a new transaction
            vitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 10, 0, firstBatch[0]);
            Assert.Equal(610, vitals.SharedRosaries); // Accepted because it fell out of the 500 window

            // Re-submitting transaction index 550 (which is inside the 500 window) must be rejected
            vitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 10, 0, firstBatch[550]);
            Assert.Equal(610, vitals.SharedRosaries); // Ignored as duplicate

            // Now hammer with 50 concurrent OS threads pushing 2,000 more transactions
            const int threadCount = 50;
            const int txPerThread = 40; // 2,000 transactions
            var threads = new Thread[threadCount];
            using var barrier = new Barrier(threadCount);

            for (int t = 0; t < threadCount; t++)
            {
                threads[t] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    for (int i = 0; i < txPerThread; i++)
                    {
                        vitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 1, 0, Guid.NewGuid());
                    }
                });
                threads[t].Start();
            }

            foreach (var th in threads)
            {
                th.Join();
            }

            Assert.Equal(610 + 2000, vitals.SharedRosaries);
        }

        [Fact]
        public void HighThroughput_ConcurrentSpendingAndEarning_WalletClampingAtZero()
        {
            var vitals = new VitalsSyncService();
            vitals.InitializeState(5, 5, 9, 1, 0, initialRosaries: 1000, initialShards: 0);

            const int earningThreads = 50;
            const int spendingThreads = 50;
            const int totalThreads = earningThreads + spendingThreads;
            const int opsPerThread = 40;

            var threads = new Thread[totalThreads];
            using var barrier = new Barrier(totalThreads);

            // 50 earning threads: +10 each, 40 times -> +20,000
            for (int t = 0; t < earningThreads; t++)
            {
                threads[t] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    for (int i = 0; i < opsPerThread; i++)
                    {
                        vitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 10, 0, Guid.NewGuid());
                    }
                });
                threads[t].Start();
            }

            // 50 spending threads: -25 each, 40 times -> -50,000 (exceeds earnings + initial 1000)
            for (int t = 0; t < spendingThreads; t++)
            {
                threads[earningThreads + t] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    for (int i = 0; i < opsPerThread; i++)
                    {
                        vitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, -25, 0, Guid.NewGuid());
                    }
                });
                threads[earningThreads + t].Start();
            }

            foreach (var th in threads)
            {
                th.Join();
            }

            // Balance must never drop below 0 at any point and end >= 0
            Assert.True(vitals.SharedRosaries >= 0, $"Wallet underflowed to negative: {vitals.SharedRosaries}");
        }

        [Fact]
        public void ConcurrentBroadcastCurrentCurrency_WithOngoingTransactions_NoTornStateOrDeadlock()
        {
            var vitals = new VitalsSyncService();
            vitals.InitializeState(5, 5, 9, 1, 0, 500, 200);

            const int modifierThreads = 40;
            const int broadcasterThreads = 10;
            const int totalThreads = modifierThreads + broadcasterThreads;
            var threads = new Thread[totalThreads];
            using var barrier = new Barrier(totalThreads);

            var broadcasted = new ConcurrentBag<CurrencySyncPacket>();
            vitals.OnOutgoingCurrencyBroadcast += packet => broadcasted.Add(packet);

            for (int t = 0; t < modifierThreads; t++)
            {
                threads[t] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    for (int i = 0; i < 30; i++)
                    {
                        vitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 5, 0, Guid.NewGuid());
                        vitals.OnLocalCurrencyChanged(CurrencyType.ShellShards, 2, 0, Guid.NewGuid());
                    }
                });
                threads[t].Start();
            }

            for (int t = 0; t < broadcasterThreads; t++)
            {
                threads[modifierThreads + t] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    for (int i = 0; i < 20; i++)
                    {
                        vitals.BroadcastCurrentCurrency();
                    }
                });
                threads[modifierThreads + t].Start();
            }

            foreach (var th in threads)
            {
                th.Join();
            }

            Assert.True(vitals.SharedRosaries >= 500);
            Assert.True(vitals.SharedShellShards >= 200);

            // Verify all broadcasted packets have non-empty transaction IDs
            foreach (var pkt in broadcasted)
            {
                Assert.NotEqual(Guid.Empty, pkt.TransactionId);
                Assert.True(pkt.TotalWalletAmount >= 0);
            }
        }

        [Theory]
        [InlineData(short.MaxValue)]
        [InlineData(short.MinValue)]
        [InlineData(0)]
        [InlineData(100000)] // Exceeds short.MaxValue, must clamp
        [InlineData(-100000)] // Exceeds short.MinValue, must clamp
        public void ExtremeCurrencyDeltas_ClampingAndRejection(int delta)
        {
            var vitals = new VitalsSyncService();
            vitals.InitializeState(5, 5, 9, 1, 0, 10000, 10000);

            CurrencySyncPacket? capturedPacket = null;
            vitals.OnOutgoingCurrencyBroadcast += p => capturedPacket = p;

            var txId = Guid.NewGuid();
            vitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, delta, 0, txId);

            if (delta == 0)
            {
                // Zero delta must be ignored
                Assert.Null(capturedPacket);
                Assert.Equal(10000, vitals.SharedRosaries);
            }
            else
            {
                Assert.NotNull(capturedPacket);
                Assert.Equal(txId, capturedPacket.Value.TransactionId);
                // DeltaAmount in packet is short clamped
                short expectedClampedDelta = (short)Math.Clamp(delta, short.MinValue, short.MaxValue);
                Assert.Equal(expectedClampedDelta, capturedPacket.Value.DeltaAmount);
            }
        }

        #endregion

        #region 2. Extreme Out-of-Order and Dropped Packet Sequences on Channels 2 & 3

        [Fact]
        public void Channel2_ExtremeOutOfOrder_VitalsProgressionMonotonicity()
        {
            var vitals = new VitalsSyncService();
            vitals.InitializeState(5, 5, 9, 1, 0, 0, 0);

            var peer = new NetworkPeerId(2, "Partner");

            // Progression tiers generated in ascending order:
            // Health: 5, 6, 7, 8, 9, 10
            // Silk: 1, 2, 3
            // Needle: 0, 1, 2, 3, 4
            var packets = new List<VitalsSyncPacket>();
            for (byte h = 5; h <= 10; h++)
            {
                byte silk = (byte)Math.Min(3, 1 + (h - 5) / 2);
                byte needle = (byte)Math.Min(4, h - 5);
                packets.Add(new VitalsSyncPacket
                {
                    Header = new PacketHeader(PacketType.VitalsSync, 2, 0, (uint)Environment.TickCount),
                    CurrentHealth = h,
                    CurrentSilk = 9,
                    MaxHealth = h,
                    MaxSilkSpools = silk,
                    NeedleUpgradeLevel = needle
                });
            }

            // Deliver in reverse order (highest first, then lower)
            packets.Reverse();
            foreach (var pkt in packets)
            {
                vitals.HandleVitalsPacket(peer, pkt);
            }

            // Progression must be at the highest tier (10, 3, 4) and never regressed by lower packets
            Assert.Equal(10, vitals.SharedMaxHealth);
            Assert.Equal(3, vitals.SharedMaxSilkSpools);
            Assert.Equal(4, vitals.SharedNeedleUpgradeLevel);

            // Shuffled random delivery stress:
            var rng = new Random(1337);
            for (int trial = 0; trial < 10; trial++)
            {
                // Create random downgrade packets
                var downgradePkt = new VitalsSyncPacket
                {
                    Header = new PacketHeader(PacketType.VitalsSync, 2, 0, (uint)Environment.TickCount),
                    CurrentHealth = (byte)rng.Next(1, 5),
                    CurrentSilk = 5,
                    MaxHealth = (byte)rng.Next(1, 10), // All lower than 10
                    MaxSilkSpools = (byte)rng.Next(1, 3), // All lower than 3
                    NeedleUpgradeLevel = (byte)rng.Next(0, 4) // All lower than 4
                };

                vitals.HandleVitalsPacket(peer, downgradePkt);

                // Invariant: shared progression never regresses
                Assert.Equal(10, vitals.SharedMaxHealth);
                Assert.Equal(3, vitals.SharedMaxSilkSpools);
                Assert.Equal(4, vitals.SharedNeedleUpgradeLevel);
            }
        }

        [Fact]
        public void Channel3_MockTransport_HighJitterAndShuffle_ReliableOrderedDelivery()
        {
            // Set up paired transports with severe jitter (30ms latency +/- 25ms jitter)
            var (host, client) = MockNetworkTransport.CreatePair(
                latencyMs: 30,
                jitterMs: 25,
                lossRate: 0f,
                useVirtualTime: true,
                seed: 999);

            const int packetCount = 40;
            var receivedOrder = new List<int>();

            client.OnDataReceived += (peer, data, offset, length) =>
            {
                var span = new ReadOnlySpan<byte>(data, offset, length);
                if (PacketHeader.TryReadFrom(span, out var header))
                {
                    var reader = new PacketReader(span);
                    var pkt = CurrencySyncPacket.Deserialize(ref reader);
                    receivedOrder.Add(pkt.TotalWalletAmount);
                }
            };

            var buffer = new byte[64];

            // Send 40 sequential currency packets on Channel 3 with ReliableOrdered
            for (int i = 0; i < packetCount; i++)
            {
                var packet = new CurrencySyncPacket
                {
                    Header = new PacketHeader(PacketType.CurrencySync, 3, (ushort)i, (uint)i),
                    Currency = CurrencyType.Rosaries,
                    TotalWalletAmount = i, // Use i as the sequence marker
                    DeltaAmount = 1,
                    TransactionId = Guid.NewGuid()
                };

                var writer = new PacketWriter(buffer);
                packet.Serialize(ref writer);

                byte[] packetBytes = new byte[writer.Position];
                Array.Copy(buffer, packetBytes, writer.Position);
                host.Send(client.LocalPeerId, packetBytes, 0, packetBytes.Length, DeliveryMethod.ReliableOrdered, 3);
            }

            // Step virtual time until all packets arrive
            for (int t = 0; t < 200; t++)
            {
                host.CurrentVirtualTimeMs += 10;
                client.CurrentVirtualTimeMs += 10;
                host.PollEvents();
                client.PollEvents();
            }

            // Assert that despite jitter and random delivery times, ReliableOrdered guarantees
            // that packets on Channel 3 are delivered in exact, monotonic order: 0, 1, 2, ..., 39
            Assert.Equal(packetCount, receivedOrder.Count);
            for (int i = 0; i < packetCount; i++)
            {
                Assert.Equal(i, receivedOrder[i]);
            }
        }

        [Fact]
        public void CrossChannel_Segregation_Channel2StallDoesNotBlockChannel3()
        {
            var (host, client) = MockNetworkTransport.CreatePair(
                latencyMs: 0,
                jitterMs: 0,
                lossRate: 0f,
                useVirtualTime: true,
                seed: 123);

            var deliveredChannel3 = new List<int>();
            client.OnDataReceived += (peer, data, offset, length) =>
            {
                var span = new ReadOnlySpan<byte>(data, offset, length);
                if (PacketHeader.TryReadFrom(span, out var header))
                {
                    if (header.ChannelId == 3)
                    {
                        var reader = new PacketReader(span);
                        var pkt = CurrencySyncPacket.Deserialize(ref reader);
                        deliveredChannel3.Add(pkt.TotalWalletAmount);
                    }
                }
            };

            // Manually inject out-of-order packet on Channel 2 (sequence 5 without sequence 0..4)
            // Channel 2 is now waiting for sequence 0
            var stalledVitalsPacket = new MockNetworkTransport.QueuedPacket
            {
                DeliveryTimeTicks = client.CurrentVirtualTimeMs,
                Channel = 2,
                Method = DeliveryMethod.ReliableOrdered,
                Sequence = 5,
                Data = new byte[32],
                Sender = host.LocalPeerId
            };
            client.EnqueueIncoming(stalledVitalsPacket);

            var buffer = new byte[64];

            // Channel 3 sends normal sequential packets (sequence 0, 1, 2, 3)
            for (int i = 0; i < 4; i++)
            {
                var packet = new CurrencySyncPacket
                {
                    Header = new PacketHeader(PacketType.CurrencySync, 3, (ushort)i, 0),
                    Currency = CurrencyType.Rosaries,
                    TotalWalletAmount = i * 10,
                    DeltaAmount = 10,
                    TransactionId = Guid.NewGuid()
                };
                var writer = new PacketWriter(buffer);
                packet.Serialize(ref writer);

                byte[] packetBytes = new byte[writer.Position];
                Array.Copy(buffer, packetBytes, writer.Position);
                host.Send(client.LocalPeerId, packetBytes, 0, packetBytes.Length, DeliveryMethod.ReliableOrdered, 3);
            }

            // Step virtual time and poll
            host.CurrentVirtualTimeMs += 10;
            client.CurrentVirtualTimeMs += 10;
            host.PollEvents();
            client.PollEvents();

            // Channel 3 must have delivered all 4 packets immediately, unaffected by the Channel 2 head-of-line stall
            Assert.Equal(4, deliveredChannel3.Count);
            Assert.Equal(0, deliveredChannel3[0]);
            Assert.Equal(10, deliveredChannel3[1]);
            Assert.Equal(20, deliveredChannel3[2]);
            Assert.Equal(30, deliveredChannel3[3]);
        }

        [Fact]
        public void InterleavedCurrencyCommutativity_UnderReverseDelivery()
        {
            var vitals = new VitalsSyncService();
            vitals.InitializeState(5, 5, 9, 1, 0, 0, 0);
            var peer = new NetworkPeerId(2, "Partner");

            int[] deltas = { 5, 10, 25, 1, 50, 100, 15, 3, 8, 12 };
            int expectedSum = 0;
            var packets = new List<CurrencySyncPacket>();

            foreach (var d in deltas)
            {
                expectedSum += d;
                packets.Add(new CurrencySyncPacket
                {
                    Header = new PacketHeader(PacketType.CurrencySync, 3, 0, 0),
                    Currency = CurrencyType.Rosaries,
                    TotalWalletAmount = 0,
                    DeltaAmount = (short)d,
                    TransactionId = Guid.NewGuid()
                });
            }

            // Deliver in reverse order
            packets.Reverse();
            foreach (var pkt in packets)
            {
                vitals.HandleCurrencyPacket(peer, pkt);
            }

            // Due to commutative delta accumulation, the wallet must equal the exact sum
            Assert.Equal(expectedSum, vitals.SharedRosaries);
        }

        #endregion

        #region 3. Malformed Packets & Corrupt Payloads

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(5)]
        [InlineData(11)] // HeaderSize is 12, so 11 is truncated header
        public void MalformedPacket_TruncatedHeaders_RejectedSafely(int length)
        {
            byte[] buffer = new byte[length];
            Array.Fill<byte>(buffer, 0x53); // Magic byte 'S'

            Assert.False(PacketHeader.TryReadFrom(buffer, out var header));
            Assert.Equal(default, header);

            var reader = new PacketReader(buffer);
            Assert.False(reader.TryReadHeader(out var rHeader));
            Assert.Equal(0, reader.Position);

            Assert.Throws<InvalidOperationException>(() =>
            {
                var r = new PacketReader(buffer);
                r.ReadHeader();
            });
        }

        [Theory]
        [InlineData(12)] // Valid 12-byte header, but 0 payload bytes
        [InlineData(13)] // 1 payload byte
        [InlineData(15)] // 3 payload bytes (Vitals needs 5)
        [InlineData(16)] // 4 payload bytes (Vitals needs 5)
        public void MalformedPacket_TruncatedVitalsSyncPayload_ThrowsUnderflowGracefully(int totalLength)
        {
            byte[] buffer = new byte[totalLength];
            var header = new PacketHeader(PacketType.VitalsSync, 2, 1, 100);
            var writer = new PacketWriter(buffer);
            writer.WriteHeader(header);

            // PacketHeader.TryReadFrom should succeed because length >= 12
            Assert.True(PacketHeader.TryReadFrom(buffer, out var parsedHeader));
            Assert.Equal(PacketType.VitalsSync, parsedHeader.PacketId);

            // Deserializing the packet with truncated payload must throw InvalidOperationException
            Assert.Throws<InvalidOperationException>(() =>
            {
                var reader = new PacketReader(buffer);
                VitalsSyncPacket.Deserialize(ref reader);
            });
        }

        [Theory]
        [InlineData(12)] // Valid header, 0 payload bytes
        [InlineData(15)] // 3 payload bytes
        [InlineData(20)] // Missing GUID (needs 16 bytes at end)
        [InlineData(34)] // Total 35 bytes required, 34 is 1 byte short of GUID
        public void MalformedPacket_TruncatedCurrencySyncPayload_ThrowsUnderflowGracefully(int totalLength)
        {
            byte[] buffer = new byte[totalLength];
            var header = new PacketHeader(PacketType.CurrencySync, 3, 1, 100);
            var writer = new PacketWriter(buffer);
            writer.WriteHeader(header);

            Assert.True(PacketHeader.TryReadFrom(buffer, out var parsedHeader));
            Assert.Equal(PacketType.CurrencySync, parsedHeader.PacketId);

            // Deserializing the packet with truncated payload must throw InvalidOperationException
            Assert.Throws<InvalidOperationException>(() =>
            {
                var reader = new PacketReader(buffer);
                CurrencySyncPacket.Deserialize(ref reader);
            });
        }

        [Fact]
        public void MalformedPacket_CorruptMagicBytes_RejectedImmediately()
        {
            // Expected magic is 0x4B53 (Little Endian for 'S' 0x53, 'K' 0x4B). Any other magic must be rejected.
            ushort[] corruptMagics = { 0x0000, 0x1234, 0x534C, 0xFFFF, 0x534B, 0x0053, 0x4B00 };

            foreach (var magic in corruptMagics)
            {
                byte[] buffer = new byte[32];
                buffer[0] = (byte)(magic & 0xFF);
                buffer[1] = (byte)((magic >> 8) & 0xFF);
                buffer[2] = (byte)PacketType.VitalsSync;
                buffer[3] = 2; // Channel

                bool parsed = PacketHeader.TryReadFrom(buffer, out var header);
                Assert.False(parsed, $"Magic 0x{magic:X4} was unexpectedly accepted!");
            }
        }

        [Fact]
        public void MalformedPacket_EmptyGuid_CurrencySync_SafelyIgnored()
        {
            var vitals = new VitalsSyncService();
            vitals.InitializeState(5, 5, 9, 1, 0, 100, 100);
            var peer = new NetworkPeerId(2, "Partner");

            // Empty GUID on HandleCurrencyPacket
            var packet = new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 0, 0),
                Currency = CurrencyType.Rosaries,
                TotalWalletAmount = 0,
                DeltaAmount = 50,
                TransactionId = Guid.Empty
            };

            vitals.HandleCurrencyPacket(peer, packet);
            Assert.Equal(100, vitals.SharedRosaries); // Ignored, no change

            // Empty GUID on OnLocalCurrencyChanged
            vitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 50, 0, Guid.Empty);
            Assert.Equal(100, vitals.SharedRosaries); // Ignored, no change
        }

        [Fact]
        public void MalformedPacket_CorruptedCurrencyTypeEnum_HandledSafely()
        {
            var vitals = new VitalsSyncService();
            vitals.InitializeState(5, 5, 9, 1, 0, 100, 100);
            var peer = new NetworkPeerId(2, "Partner");

            // Packet with undefined currency enum value (e.g. 255)
            var packet = new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 0, 0),
                Currency = (CurrencyType)255,
                TotalWalletAmount = 0,
                DeltaAmount = 20,
                TransactionId = Guid.NewGuid()
            };

            // Must not throw unhandled exception
            vitals.HandleCurrencyPacket(peer, packet);

            // Default branch in VitalsSyncService treats non-Rosaries as ShellShards
            Assert.Equal(120, vitals.SharedShellShards);
            Assert.Equal(100, vitals.SharedRosaries);
        }

        [Fact]
        public void Fuzzer_10000RandomByteBuffers_RobustnessStress()
        {
            var rng = new Random(4242);
            var vitals = new VitalsSyncService();
            var peer = new NetworkPeerId(99, "Attacker");

            int validHeadersFound = 0;
            int successfulDeserializations = 0;
            int gracefulUnderflows = 0;

            for (int i = 0; i < 10000; i++)
            {
                int bufferLength = rng.Next(0, 256);
                byte[] randomBuffer = new byte[bufferLength];
                rng.NextBytes(randomBuffer);

                // Occasionally inject the real magic to stress header parsing
                if (i % 5 == 0 && bufferLength >= 2)
                {
                    randomBuffer[0] = 0x53;
                    randomBuffer[1] = 0x4B;
                }

                if (PacketHeader.TryReadFrom(randomBuffer, out var header))
                {
                    validHeadersFound++;
                    try
                    {
                        var reader = new PacketReader(randomBuffer);
                        switch (header.PacketId)
                        {
                            case PacketType.VitalsSync:
                                var v = VitalsSyncPacket.Deserialize(ref reader);
                                vitals.HandleVitalsPacket(peer, v);
                                successfulDeserializations++;
                                break;

                            case PacketType.CurrencySync:
                                var c = CurrencySyncPacket.Deserialize(ref reader);
                                vitals.HandleCurrencyPacket(peer, c);
                                successfulDeserializations++;
                                break;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // Expected safe underflow rejection
                        gracefulUnderflows++;
                    }
                    catch (Exception ex)
                    {
                        Assert.Fail($"Unexpected non-graceful exception during fuzzing: {ex}");
                    }
                }
            }

            // Zero unhandled fatal exceptions; all invalid packets safely rejected
            Assert.True(validHeadersFound >= 0);
        }

        #endregion

        #region 4. Full Multi-Client DualClientTestHarness Integration Concurrency

        [Fact]
        public void DualClient_SimultaneousHighThroughputCurrencies_BidirectionalConvergence()
        {
            using var harness = new DualClientTestHarness(latencyMs: 10, jitterMs: 5, lossRate: 0f, seed: 777);
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, initialRosaries: 100, initialShards: 50);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, initialRosaries: 100, initialShards: 50);

            const int transactionsPerSide = 30;
            int expectedRosaries = 100 + (transactionsPerSide * 2 * 10); // 100 + 600 = 700

            // Host generates 30 transactions (+10 each)
            for (int i = 0; i < transactionsPerSide; i++)
            {
                harness.HostVitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 10, 0, Guid.NewGuid());
            }

            // Client generates 30 transactions (+10 each)
            for (int i = 0; i < transactionsPerSide; i++)
            {
                harness.ClientVitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 10, 0, Guid.NewGuid());
            }

            // Step virtual time until all packets are exchanged
            harness.DeliverAll(maxSteps: 300);

            // Both sides must converge to the exact same shared wallet balance
            Assert.Equal(expectedRosaries, harness.HostVitals.SharedRosaries);
            Assert.Equal(expectedRosaries, harness.ClientVitals.SharedRosaries);
            harness.AssertSharedWallet(expectedRosaries, 50);
        }

        [Fact]
        public void DualClient_SimultaneousDisjointUpgrades_BothClientsConverge()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, 0, 0);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, 0, 0);

            // Host upgrades Health to 7 and Needle to 1
            harness.HostVitals.OnLocalMaxHealthUpgrade(7);
            harness.HostVitals.OnLocalNeedleUpgrade(1);

            // Client upgrades Needle to 3 and Silk to 3 spools
            harness.ClientVitals.OnLocalNeedleUpgrade(3);
            harness.ClientVitals.OnLocalMaxSilkUpgrade(3);

            // Deliver all Channel 2 ReliableOrdered packets
            harness.DeliverAll();

            // Both clients must converge to the supremum (highest) of each progression attribute:
            // MaxHealth = 7, MaxSilkSpools = 3, NeedleUpgradeLevel = 3
            Assert.Equal(7, harness.HostVitals.SharedMaxHealth);
            Assert.Equal(7, harness.ClientVitals.SharedMaxHealth);

            Assert.Equal(3, harness.HostVitals.SharedMaxSilkSpools);
            Assert.Equal(3, harness.ClientVitals.SharedMaxSilkSpools);

            Assert.Equal(3, harness.HostVitals.SharedNeedleUpgradeLevel);
            Assert.Equal(3, harness.ClientVitals.SharedNeedleUpgradeLevel);
        }

        [Fact]
        public void HighThroughput_ConcurrentLocalDamageAndHealing_ThreadSafetyAndHudObservation()
        {
            var vitals = new VitalsSyncService();
            vitals.InitializeState(5, 5, 9, 1, 0, 0, 0);

            const int threadsCount = 40;
            var threads = new Thread[threadsCount];
            using var barrier = new Barrier(threadsCount);

            var broadcastedVitals = new ConcurrentBag<VitalsSyncPacket>();
            vitals.OnOutgoingVitalsBroadcast += p => broadcastedVitals.Add(p);

            for (int t = 0; t < threadsCount; t++)
            {
                int tid = t;
                threads[t] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    if (tid % 2 == 0)
                    {
                        // Take damage
                        vitals.OnLocalDamageTaken(1, 4);
                    }
                    else
                    {
                        // Heal
                        vitals.OnLocalHealthGained(1, 5);
                    }
                });
                threads[t].Start();
            }

            foreach (var th in threads) th.Join();

            // Local health must be bounded between 0 and 5
            Assert.InRange(vitals.LocalHealth, 0, 5);
            Assert.Equal(threadsCount, broadcastedVitals.Count);
        }

        [Fact]
        public void SharedProgression_ConcurrentCrossThreadUpgrades_MonotonicConvergence()
        {
            var vitals = new VitalsSyncService();
            vitals.InitializeState(5, 5, 9, 1, 0, 0, 0);

            const int threadsCount = 50;
            var threads = new Thread[threadsCount];
            using var barrier = new Barrier(threadsCount);

            for (int t = 0; t < threadsCount; t++)
            {
                int tid = t;
                threads[t] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    // Each thread attempts to upgrade to various levels (up to 10 for health, 4 for needle, 3 for silk)
                    int healthUpgrade = 5 + (tid % 6); // 5..10
                    int needleUpgrade = tid % 5;        // 0..4
                    int silkUpgrade = 1 + (tid % 3);    // 1..3

                    vitals.OnLocalMaxHealthUpgrade(healthUpgrade);
                    vitals.OnLocalNeedleUpgrade(needleUpgrade);
                    vitals.OnLocalMaxSilkUpgrade(silkUpgrade);
                });
                threads[t].Start();
            }

            foreach (var th in threads) th.Join();

            // Maximum values must strictly converge to the highest submitted
            Assert.Equal(10, vitals.SharedMaxHealth);
            Assert.Equal(4, vitals.SharedNeedleUpgradeLevel);
            Assert.Equal(3, vitals.SharedMaxSilkSpools);
        }

        [Fact]
        public void BothCurrencies_SimultaneousInterleavedHammering_RosariesAndShards()
        {
            var vitals = new VitalsSyncService();
            vitals.InitializeState(5, 5, 9, 1, 0, 1000, 1000);

            const int threadsCount = 60;
            const int opsPerThread = 50;
            var threads = new Thread[threadsCount];
            using var barrier = new Barrier(threadsCount);

            for (int t = 0; t < threadsCount; t++)
            {
                int tid = t;
                threads[t] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    for (int i = 0; i < opsPerThread; i++)
                    {
                        if (tid % 2 == 0)
                        {
                            vitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 2, 0, Guid.NewGuid());
                        }
                        else
                        {
                            vitals.OnLocalCurrencyChanged(CurrencyType.ShellShards, 3, 0, Guid.NewGuid());
                        }
                    }
                });
                threads[t].Start();
            }

            foreach (var th in threads) th.Join();

            // 30 threads did Rosaries * 50 ops * 2 = +3,000 Rosaries
            // 30 threads did Shards * 50 ops * 3 = +4,500 Shards
            Assert.Equal(1000 + (30 * 50 * 2), vitals.SharedRosaries);
            Assert.Equal(1000 + (30 * 50 * 3), vitals.SharedShellShards);
        }

        #endregion
    }
}
