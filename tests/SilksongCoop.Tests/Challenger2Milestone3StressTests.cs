using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
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
    /// Empirical Adversarial Stress Suite for Milestone 3 Economy & Progression Invariants.
    /// Focuses on:
    /// 1. Commutative delta accumulation under severe latency, jitter, out-of-order arrival, and concurrent thread contention.
    /// 2. 500-item FIFO sliding window eviction boundaries, memory bounding (O(1)), duplicate spam resilience, and replay attack boundaries.
    /// 3. Negative currency spending, racing dual-client overspending, and zero floor clamping.
    /// 4. Progression upgrade monotonicity across needle, mask containers, and silk spools under stale/out-of-order flood conditions.
    /// </summary>
    public class Challenger2Milestone3StressTests
    {
        #region 1. Commutative Delta Accumulation Stress Tests

        [Fact]
        public void CommutativeDelta_1000RandomInterleavedTransactions_BothPeersConvergeIdentically()
        {
            // Seeded RNG for reproducible pseudo-random transactions
            var rng = new Random(1337);

            // Network simulation: 40ms latency, 20ms jitter
            using var harness = new DualClientTestHarness(latencyMs: 40, jitterMs: 20, seed: 1337);
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, initialRosaries: 10000, initialShards: 1000);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, initialRosaries: 10000, initialShards: 1000);

            // Generate 500 transactions from Host and 500 transactions from Client (1,000 total)
            for (int i = 0; i < 500; i++)
            {
                // Host generates a transaction
                CurrencyType hostCurrency = rng.Next(2) == 0 ? CurrencyType.Rosaries : CurrencyType.ShellShards;
                int hostDelta = rng.Next(-10, 50);
                if (hostDelta != 0)
                {
                    var txHost = Guid.NewGuid();
                    harness.HostVitals.OnLocalCurrencyChanged(hostCurrency, hostDelta, 0, txHost);
                }

                // Client generates a transaction
                CurrencyType clientCurrency = rng.Next(2) == 0 ? CurrencyType.Rosaries : CurrencyType.ShellShards;
                int clientDelta = rng.Next(-10, 50);
                if (clientDelta != 0)
                {
                    var txClient = Guid.NewGuid();
                    harness.ClientVitals.OnLocalCurrencyChanged(clientCurrency, clientDelta, 0, txClient);
                }

                // Periodic network delivery ticks
                if (i % 10 == 0)
                {
                    harness.Step(16);
                }
            }

            // Flush all in-flight network packets until complete quiescence
            harness.DeliverAll(maxSteps: 300);

            // Invariant: Both peers MUST converge to the EXACT same wallet balances
            Assert.Equal(harness.HostVitals.SharedRosaries, harness.ClientVitals.SharedRosaries);
            Assert.Equal(harness.HostVitals.SharedShellShards, harness.ClientVitals.SharedShellShards);

            // Wallets must not have drifted into negative territory
            Assert.True(harness.HostVitals.SharedRosaries >= 0);
            Assert.True(harness.HostVitals.SharedShellShards >= 0);
        }

        [Fact]
        public async Task CommutativeDelta_HighConcurrencyRacingThreads_NoRaceOrDataCorruption()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, initialRosaries: 50000, initialShards: 20000);

            const int threadCount = 10;
            const int transactionsPerThread = 100;
            var tasks = new List<Task>();

            // Concurrent local changes
            for (int t = 0; t < threadCount; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < transactionsPerThread; i++)
                    {
                        var guid = Guid.NewGuid();
                        service.OnLocalCurrencyChanged(CurrencyType.Rosaries, 1, 0, guid);
                    }
                }));
            }

            // Concurrent remote incoming packets
            for (int t = 0; t < threadCount; t++)
            {
                int peerId = t + 2;
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < transactionsPerThread; i++)
                    {
                        var guid = Guid.NewGuid();
                        service.HandleCurrencyPacket(new NetworkPeerId((uint)peerId), new CurrencySyncPacket
                        {
                            Header = new PacketHeader(PacketType.CurrencySync, 3, 0, 100),
                            Currency = CurrencyType.Rosaries,
                            DeltaAmount = 1,
                            TotalWalletAmount = 0,
                            TransactionId = guid
                        });
                    }
                }));
            }

            await Task.WhenAll(tasks);

            // 50,000 initial + (10 * 100 local) + (10 * 100 remote) = 52,000 exactly
            Assert.Equal(52000, service.SharedRosaries);
        }

        [Fact]
        public void CommutativeDelta_ReorderedTransactions_ConvergenceRegardlessOfArrivalOrder()
        {
            var serviceNormal = new VitalsSyncService();
            serviceNormal.InitializeState(5, 5, 9, 1, 0, initialRosaries: 1000, initialShards: 500);

            var serviceReversed = new VitalsSyncService();
            serviceReversed.InitializeState(5, 5, 9, 1, 0, initialRosaries: 1000, initialShards: 500);

            var packets = new List<CurrencySyncPacket>();
            var rng = new Random(42);

            for (int i = 0; i < 100; i++)
            {
                int delta = rng.Next(-5, 20); // deltas small enough not to deplete 1000 balance
                if (delta == 0) delta = 1;

                packets.Add(new CurrencySyncPacket
                {
                    Header = new PacketHeader(PacketType.CurrencySync, 3, (uint)i, (uint)(i * 10)),
                    Currency = CurrencyType.Rosaries,
                    DeltaAmount = (short)delta,
                    TotalWalletAmount = 0,
                    TransactionId = Guid.NewGuid()
                });
            }

            // Apply forward order to serviceNormal
            foreach (var p in packets)
            {
                serviceNormal.HandleCurrencyPacket(new NetworkPeerId(1), p);
            }

            // Apply reverse order to serviceReversed
            for (int i = packets.Count - 1; i >= 0; i--)
            {
                serviceReversed.HandleCurrencyPacket(new NetworkPeerId(1), packets[i]);
            }

            // Commutativity invariant: Final sum must be identical regardless of sequence order
            Assert.Equal(serviceNormal.SharedRosaries, serviceReversed.SharedRosaries);
        }

        [Fact]
        public void CommutativeDelta_ShortIntMaxBoundary_ClampingPreservesMathematicalRange()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, initialRosaries: 0, initialShards: 0);

            CurrencySyncPacket? capturedPacket = null;
            service.OnOutgoingCurrencyBroadcast += p => capturedPacket = p;

            // Maximum short delta (+32767)
            var txMax = Guid.NewGuid();
            service.OnLocalCurrencyChanged(CurrencyType.Rosaries, short.MaxValue, 0, txMax);

            Assert.Equal(32767, service.SharedRosaries);
            Assert.NotNull(capturedPacket);
            Assert.Equal((short)32767, capturedPacket.Value.DeltaAmount);

            // Minimum short delta (-32768)
            var txMin = Guid.NewGuid();
            service.OnLocalCurrencyChanged(CurrencyType.Rosaries, short.MinValue, 0, txMin);

            // 32767 - 32768 = -1 -> clamped at 0
            Assert.Equal(0, service.SharedRosaries);
            Assert.Equal((short)-32768, capturedPacket.Value.DeltaAmount);
        }

        #endregion

        #region 2. 500-Item FIFO Sliding Window Eviction & Memory Bounds

        [Fact]
        public void SlidingWindow_Exact500Capacity_RetainsAll500TransactionsAgainstReplay()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, initialRosaries: 0, initialShards: 0);

            var guids = new List<Guid>(500);

            for (int i = 0; i < 500; i++)
            {
                var guid = Guid.NewGuid();
                guids.Add(guid);
                service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
                {
                    Header = new PacketHeader(PacketType.CurrencySync, 3, (uint)i, (uint)i),
                    Currency = CurrencyType.Rosaries,
                    DeltaAmount = 2,
                    TransactionId = guid
                });
            }

            // 500 transactions of +2 = 1,000 Rosaries
            Assert.Equal(1000, service.SharedRosaries);

            // Replay every single one of the 500 transactions
            for (int i = 0; i < 500; i++)
            {
                service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
                {
                    Header = new PacketHeader(PacketType.CurrencySync, 3, 999, 999),
                    Currency = CurrencyType.Rosaries,
                    DeltaAmount = 2,
                    TransactionId = guids[i]
                });
            }

            // Zero additional Rosaries credited: all 500 replayed packets were deduplicated!
            Assert.Equal(1000, service.SharedRosaries);
        }

        [Fact]
        public void SlidingWindow_501Transactions_EvictsOldestTransactionStrictlyFIFO()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, initialRosaries: 0, initialShards: 0);

            var guids = new List<Guid>(510);
            for (int i = 0; i < 500; i++)
            {
                var guid = Guid.NewGuid();
                guids.Add(guid);
                service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
                {
                    Header = new PacketHeader(PacketType.CurrencySync, 3, (uint)i, (uint)i),
                    Currency = CurrencyType.Rosaries,
                    DeltaAmount = 1,
                    TransactionId = guid
                });
            }

            Assert.Equal(500, service.SharedRosaries);

            // Transaction 501 arrives -> evicts guids[0] from the 500-item window
            var guid501 = Guid.NewGuid();
            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 501, 501),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 1,
                TransactionId = guid501
            });

            Assert.Equal(501, service.SharedRosaries);

            // Step A: Replay guids[1] BEFORE guids[0] is replayed.
            // guids[1] is still currently the oldest in window, so it MUST be dropped!
            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 502, 502),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 1,
                TransactionId = guids[1]
            });
            Assert.Equal(501, service.SharedRosaries); // Dropped!

            // Step B: Replay guids[0] (which was evicted by guid501).
            // It is treated as new, enqueued, and in turn evicts the next oldest: guids[1]!
            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 503, 503),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 1,
                TransactionId = guids[0]
            });
            Assert.Equal(502, service.SharedRosaries); // Accepted because evicted!

            // Step C: Replaying guids[1] now MUST succeed because guids[1] was just evicted by the acceptance of guids[0]!
            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 504, 504),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 1,
                TransactionId = guids[1]
            });
            Assert.Equal(503, service.SharedRosaries); // Accepted because guids[0] evicted guids[1]!

            // Step D: Meanwhile, guids[2] is now the oldest. Replaying guids[2] after guids[1] acceptance evicts guids[2]!
            // First verify guids[3] is NOT evicted yet:
            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 505, 505),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 1,
                TransactionId = guids[3]
            });
            Assert.Equal(503, service.SharedRosaries); // Dropped!
        }

        [Fact]
        public void SlidingWindow_MemoryBounds_10000Transactions_CollectionSizesBoundedAt500()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, initialRosaries: 0, initialShards: 0);

            // Inspect internal private fields via reflection
            var procField = typeof(VitalsSyncService).GetField("_processedTransactions", BindingFlags.NonPublic | BindingFlags.Instance);
            var orderField = typeof(VitalsSyncService).GetField("_transactionOrder", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.NotNull(procField);
            Assert.NotNull(orderField);

            for (int i = 0; i < 10000; i++)
            {
                var guid = Guid.NewGuid();
                service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
                {
                    Header = new PacketHeader(PacketType.CurrencySync, 3, (uint)i, (uint)i),
                    Currency = CurrencyType.Rosaries,
                    DeltaAmount = 1,
                    TransactionId = guid
                });
            }

            var processedSet = (HashSet<Guid>?)procField.GetValue(service);
            var orderQueue = (Queue<Guid>?)orderField.GetValue(service);

            Assert.NotNull(processedSet);
            Assert.NotNull(orderQueue);

            // Strict O(1) memory bound invariant: Must NEVER exceed 500 items
            Assert.Equal(500, processedSet.Count);
            Assert.Equal(500, orderQueue.Count);
            Assert.Equal(10000, service.SharedRosaries);
        }

        [Fact]
        public void SlidingWindow_DuplicateSpam_DoesNotPolluteFIFOQueue()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, 0, 0);

            var spamGuid = Guid.NewGuid();

            // Spam the exact same transaction 1,000 times
            for (int i = 0; i < 1000; i++)
            {
                service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
                {
                    Header = new PacketHeader(PacketType.CurrencySync, 3, (uint)i, (uint)i),
                    Currency = CurrencyType.Rosaries,
                    DeltaAmount = 10,
                    TransactionId = spamGuid
                });
            }

            // Processed exactly once
            Assert.Equal(10, service.SharedRosaries);

            var orderField = typeof(VitalsSyncService).GetField("_transactionOrder", BindingFlags.NonPublic | BindingFlags.Instance);
            var orderQueue = (Queue<Guid>?)orderField?.GetValue(service);
            Assert.NotNull(orderQueue);

            // Duplicate spam must NOT add duplicate entries to the queue
            Assert.Single(orderQueue);
        }

        [Fact]
        public void SlidingWindow_TamperedPayload_IdenticalGuidDifferentDelta_Rejected()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, 100, 0);

            var tx = Guid.NewGuid();

            // Legit transaction: +50
            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 1, 10),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 50,
                TransactionId = tx
            });
            Assert.Equal(150, service.SharedRosaries);

            // Malicious or corrupted retry of same tx ID claiming +9999
            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 2, 20),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 9999,
                TransactionId = tx
            });

            // Tampered payload with duplicate GUID must be rejected outright
            Assert.Equal(150, service.SharedRosaries);
        }

        [Fact]
        public void SlidingWindow_EmptyGuid_BothLocalAndRemote_StrictlyRejected()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, 100, 50);

            bool localBroadcastFired = false;
            service.OnOutgoingCurrencyBroadcast += _ => localBroadcastFired = true;

            // Local with Guid.Empty
            service.OnLocalCurrencyChanged(CurrencyType.Rosaries, 20, 120, Guid.Empty);
            Assert.Equal(100, service.SharedRosaries);
            Assert.False(localBroadcastFired);

            // Remote with Guid.Empty
            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 1, 10),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 20,
                TransactionId = Guid.Empty
            });
            Assert.Equal(100, service.SharedRosaries);
        }

        #endregion

        #region 3. Negative Currency Spending & Zero Floor Clamping

        [Fact]
        public void ZeroFloor_RacingSimultaneousOverspend_BothClientsClampAtZero()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, initialRosaries: 50, initialShards: 0);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, initialRosaries: 50, initialShards: 0);

            // Host spends 40 Rosaries
            var txHost = Guid.NewGuid();
            harness.HostVitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, -40, 10, txHost);

            // Client spends 35 Rosaries concurrently (combined spend = 75, exceeding 50 balance)
            var txClient = Guid.NewGuid();
            harness.ClientVitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, -35, 15, txClient);

            // Before packet delivery:
            // Host local wallet: 50 - 40 = 10
            // Client local wallet: 50 - 35 = 15
            Assert.Equal(10, harness.HostVitals.SharedRosaries);
            Assert.Equal(15, harness.ClientVitals.SharedRosaries);

            // Deliver all packets
            harness.DeliverAll();

            // After delivery:
            // Host receives -35: 10 - 35 = -25 -> clamps to 0
            // Client receives -40: 15 - 40 = -25 -> clamps to 0
            Assert.Equal(0, harness.HostVitals.SharedRosaries);
            Assert.Equal(0, harness.ClientVitals.SharedRosaries);

            // Invariant: Floor strictly clamped at 0
            harness.AssertSharedWallet(expectedRosaries: 0, expectedShards: 0);
        }

        [Fact]
        public async Task ZeroFloor_HighConcurrencyDepletion_NeverDropsBelowZeroAtAnyInstant()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, initialRosaries: 100, initialShards: 50);

            var negativeObservations = new ConcurrentBag<int>();
            var tasks = new List<Task>();

            for (int t = 0; t < 20; t++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 10; i++)
                    {
                        service.OnLocalCurrencyChanged(CurrencyType.Rosaries, -15, 0, Guid.NewGuid());
                        int current = service.SharedRosaries;
                        if (current < 0)
                        {
                            negativeObservations.Add(current);
                        }
                    }
                }));
            }

            await Task.WhenAll(tasks);

            Assert.Empty(negativeObservations);
            Assert.Equal(0, service.SharedRosaries);
        }

        [Fact]
        public void ZeroFloor_AlternatingMicroDeltasAroundZero_AccurateTrajectory()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, initialRosaries: 0, initialShards: 0);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, initialRosaries: 0, initialShards: 0);

            // Sequence of operations starting from 0:
            // 1. -50 -> max(0, -50) = 0
            // 2. +20 -> 20
            // 3. -30 -> max(0, 20 - 30) = 0
            // 4. +15 -> 15
            // 5. -10 -> 5
            // 6. -20 -> max(0, 5 - 20) = 0
            // 7. +40 -> 40

            int[] deltas = { -50, 20, -30, 15, -10, -20, 40 };
            int expected = 40;

            foreach (var d in deltas)
            {
                harness.HostVitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, d, 0, Guid.NewGuid());
                harness.DeliverAll();
            }

            Assert.Equal(expected, harness.HostVitals.SharedRosaries);
            Assert.Equal(expected, harness.ClientVitals.SharedRosaries);
        }

        [Fact]
        public void ZeroFloor_IndependentShellShardsClamping_DoesNotAffectRosaries()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, initialRosaries: 100, initialShards: 10);

            // Overspend Shell Shards
            service.OnLocalCurrencyChanged(CurrencyType.ShellShards, -30, 0, Guid.NewGuid());

            // Shards clamps at 0, Rosaries remains 100
            Assert.Equal(0, service.SharedShellShards);
            Assert.Equal(100, service.SharedRosaries);
        }

        #endregion

        #region 4. Progression Monotonicity & Stale Packet Resilience

        [Fact]
        public void ProgressionMonotonicity_NeedleUpgrades_RejectsStaleDowngrades()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, needleLevel: 2, initialRosaries: 0, initialShards: 0);

            int eventFireCount = 0;
            service.OnSharedNeedleUpgradeChanged += _ => eventFireCount++;

            // Inject stale packets with level 0 and level 1
            service.HandleVitalsPacket(new NetworkPeerId(1), new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 1, 100),
                NeedleUpgradeLevel = 0,
                MaxHealth = 5,
                MaxSilkSpools = 1
            });
            service.HandleVitalsPacket(new NetworkPeerId(1), new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 2, 200),
                NeedleUpgradeLevel = 1,
                MaxHealth = 5,
                MaxSilkSpools = 1
            });

            Assert.Equal(2, service.SharedNeedleUpgradeLevel);
            Assert.Equal(0, eventFireCount); // No downgrade events fired

            // Legitimate upgrade to level 3
            service.HandleVitalsPacket(new NetworkPeerId(1), new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 3, 300),
                NeedleUpgradeLevel = 3,
                MaxHealth = 5,
                MaxSilkSpools = 1
            });

            Assert.Equal(3, service.SharedNeedleUpgradeLevel);
            Assert.Equal(1, eventFireCount);

            // Stale packet retransmitted with level 2
            service.HandleVitalsPacket(new NetworkPeerId(1), new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 4, 400),
                NeedleUpgradeLevel = 2,
                MaxHealth = 5,
                MaxSilkSpools = 1
            });

            Assert.Equal(3, service.SharedNeedleUpgradeLevel);
            Assert.Equal(1, eventFireCount);
        }

        [Fact]
        public void ProgressionMonotonicity_MaskContainers_RejectsStaleDowngrades()
        {
            var service = new VitalsSyncService();
            service.InitializeState(initialHealth: 7, initialMaxHealth: 7, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 0, initialRosaries: 0, initialShards: 0);

            int maxHealthEventCount = 0;
            service.OnSharedMaxHealthChanged += _ => maxHealthEventCount++;

            // Stale packet with MaxHealth = 5
            service.HandleVitalsPacket(new NetworkPeerId(1), new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 1, 100),
                MaxHealth = 5,
                MaxSilkSpools = 1,
                NeedleUpgradeLevel = 0
            });

            Assert.Equal(7, service.SharedMaxHealth);
            Assert.Equal(0, maxHealthEventCount);

            // Valid upgrade to MaxHealth = 8
            service.HandleVitalsPacket(new NetworkPeerId(1), new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 2, 200),
                MaxHealth = 8,
                MaxSilkSpools = 1,
                NeedleUpgradeLevel = 0
            });

            Assert.Equal(8, service.SharedMaxHealth);
            Assert.Equal(1, maxHealthEventCount);
        }

        [Fact]
        public void ProgressionMonotonicity_SilkSpools_RejectsStaleDowngrades()
        {
            var service = new VitalsSyncService();
            service.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 18, initialMaxSilkSpools: 2, needleLevel: 0, initialRosaries: 0, initialShards: 0);

            int silkEventCount = 0;
            service.OnSharedMaxSilkChanged += _ => silkEventCount++;

            // Stale packet with 1 spool
            service.HandleVitalsPacket(new NetworkPeerId(1), new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 1, 100),
                MaxSilkSpools = 1,
                MaxHealth = 5,
                NeedleUpgradeLevel = 0
            });

            Assert.Equal(2, service.SharedMaxSilkSpools);
            Assert.Equal(0, silkEventCount);

            // Valid upgrade to 3 spools
            service.HandleVitalsPacket(new NetworkPeerId(1), new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 2, 200),
                MaxSilkSpools = 3,
                MaxHealth = 5,
                NeedleUpgradeLevel = 0
            });

            Assert.Equal(3, service.SharedMaxSilkSpools);
            Assert.Equal(1, silkEventCount);
        }

        [Fact]
        public void ProgressionMonotonicity_ExtremeInterleavedProgressionFlood()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, 0, 0);

            var rng = new Random(777);
            byte highestHpObserved = 5;
            byte highestSpoolsObserved = 1;
            byte highestNeedleObserved = 0;

            for (int i = 0; i < 200; i++)
            {
                byte hp = (byte)rng.Next(5, 10);       // 5 to 9
                byte spools = (byte)rng.Next(1, 4);   // 1 to 3
                byte needle = (byte)rng.Next(0, 5);   // 0 to 4

                if (hp > highestHpObserved) highestHpObserved = hp;
                if (spools > highestSpoolsObserved) highestSpoolsObserved = spools;
                if (needle > highestNeedleObserved) highestNeedleObserved = needle;

                int prevHp = service.SharedMaxHealth;
                int prevSpools = service.SharedMaxSilkSpools;
                int prevNeedle = service.SharedNeedleUpgradeLevel;

                service.HandleVitalsPacket(new NetworkPeerId(1), new VitalsSyncPacket
                {
                    Header = new PacketHeader(PacketType.VitalsSync, 2, (uint)i, (uint)(i * 10)),
                    MaxHealth = hp,
                    MaxSilkSpools = spools,
                    NeedleUpgradeLevel = needle
                });

                // Invariant: Monotonically non-decreasing at each step
                Assert.True(service.SharedMaxHealth >= prevHp);
                Assert.True(service.SharedMaxSilkSpools >= prevSpools);
                Assert.True(service.SharedNeedleUpgradeLevel >= prevNeedle);
            }

            // Invariant: Final state equals the supremum of all values
            Assert.Equal(highestHpObserved, service.SharedMaxHealth);
            Assert.Equal(highestSpoolsObserved, service.SharedMaxSilkSpools);
            Assert.Equal(highestNeedleObserved, service.SharedNeedleUpgradeLevel);
        }

        [Fact]
        public void ProgressionMonotonicity_LocalGaugesDecoupledFromUpgrades()
        {
            var service = new VitalsSyncService();
            service.InitializeState(initialHealth: 3, initialMaxHealth: 5, initialSilk: 4, initialMaxSilkSpools: 1, needleLevel: 0, initialRosaries: 0, initialShards: 0);

            // Remote peer upgrades Max Health to 6 and Max Silk Spools to 2
            service.HandleVitalsPacket(new NetworkPeerId(1), new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 1, 100),
                MaxHealth = 6,
                MaxSilkSpools = 2,
                NeedleUpgradeLevel = 1,
                CurrentHealth = 6,
                CurrentSilk = 18
            });

            // Progression attributes upgraded
            Assert.Equal(6, service.SharedMaxHealth);
            Assert.Equal(2, service.SharedMaxSilkSpools);
            Assert.Equal(1, service.SharedNeedleUpgradeLevel);

            // Local gauges must remain untouched (local HP 3, local Silk 4)
            Assert.Equal(3, service.LocalHealth);
            Assert.Equal(4, service.LocalSilk);
        }

        #endregion
    }
}
