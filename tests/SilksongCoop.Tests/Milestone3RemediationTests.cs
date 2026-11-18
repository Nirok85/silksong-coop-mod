using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Comprehensive tests for Milestone 3 Remediation:
    /// - Defect 6: Death lifecycle and epoch synchronization for late-joining players.
    /// - Defect 9: Inventory unlock failure rollback and safe retry execution.
    /// - Defect 14: Bounded counter transaction memory growth via 500-entry FIFO eviction.
    /// </summary>
    public class Milestone3RemediationTests
    {
        #region Helpers

        private static DeathLifecyclePacket CreateDeathPacket(DeathEventType eventType, uint epoch, float x = 0f, float y = 0f)
        {
            return new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 1, NetworkClock.NowMs),
                EventType = eventType,
                CoordinateX = x,
                CoordinateY = y,
                RemainingSeconds = 0f,
                Epoch = epoch
            };
        }

        #endregion

        #region Defect 6: Death Lifecycle & Epoch Sync for Late-Joining Players

        [Fact]
        public void Defect6_LateJoiningPeer_LifecycleHandshake_AcceptsDeathAndRejectsStaleRecovery()
        {
            var host = new DeathLifecycleCoordinator();
            var late = new DeathLifecycleCoordinator();
            var latePeer = new NetworkPeerId(2);
            var hostPeer = new NetworkPeerId(1);
            host.ResetAfterBenchRespawn();
            host.ResetAfterBenchRespawn();
            host.RegisterRemotePeer(latePeer);
            late.RegisterRemotePeer(hostPeer);
            Assert.Equal(3u, host.CurrentEpoch);

            // Actual readiness snapshots establish each sender's own epoch and life state.
            host.HandleDeathPacket(latePeer, late.CreateLifecycleSnapshot());
            Assert.True(host.IsRemotePeerAlive(latePeer));
            Assert.Equal(3u, host.CurrentEpoch);
            late.HandleDeathPacket(hostPeer, host.CreateLifecycleSnapshot());
            Assert.Equal(3u, late.CurrentEpoch);

            DeathLifecyclePacket? death = null;
            late.OnOutgoingDeathPacket += packet => death = packet;
            late.OnLocalPlayerDied(4f, 5f);
            Assert.True(death.HasValue);
            host.HandleDeathPacket(latePeer, death.Value);
            Assert.False(host.IsRemotePeerAlive(latePeer));

            // A recovery from a completed older cycle must never resurrect the sender.
            host.HandleDeathPacket(latePeer,
                CreateDeathPacket(DeathEventType.BenchCheckpointUpdate, epoch: 1));
            Assert.False(host.IsRemotePeerAlive(latePeer));
            Assert.Equal(3u, host.CurrentEpoch);
        }

        [Fact]
        public void Defect6_PeerWithHigherEpoch_IsMarkedAlive_And_AdoptsHigherEpoch()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = new NetworkPeerId(2);

            Assert.Equal(1u, coordinator.CurrentEpoch);

            DeathLifecyclePacket? broadcastPacket = null;
            coordinator.OnOutgoingDeathPacket += p => broadcastPacket = p;

            // Peer finished a wipe cycle while we were disconnected and sends Epoch 5
            var higherPacket = CreateDeathPacket(DeathEventType.BenchCheckpointUpdate, epoch: 5);
            coordinator.HandleDeathPacket(peer, higherPacket);

            // Invariants:
            // 1. Peer is marked alive
            Assert.True(coordinator.IsRemotePeerAlive(peer));
            Assert.True(coordinator.IsPartnerAlive);

            // 2. Local epoch advances to the higher peer epoch
            Assert.Equal(5u, coordinator.CurrentEpoch);

            // 3. No redundant lower-epoch catch-up broadcast
            Assert.Null(broadcastPacket);
        }

        [Fact]
        public void Defect6_PeerWithEqualEpoch_IsMarkedAlive_WithoutBroadcastingSync()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = new NetworkPeerId(2);

            int broadcastCount = 0;
            coordinator.OnOutgoingDeathPacket += _ => broadcastCount++;

            // Peer sends BenchCheckpointUpdate matching our current epoch (1)
            var matchingPacket = CreateDeathPacket(DeathEventType.BenchCheckpointUpdate, epoch: 1);
            coordinator.HandleDeathPacket(peer, matchingPacket);

            Assert.True(coordinator.IsRemotePeerAlive(peer));
            Assert.True(coordinator.IsPartnerAlive);
            Assert.Equal(1u, coordinator.CurrentEpoch);
            Assert.Equal(0, broadcastCount);
        }

        [Fact]
        public void Defect6_MultiPeer_LateJoiner_DoesNotAffectOtherPeers()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peerA = new NetworkPeerId(2);
            var peerB = new NetworkPeerId(3);

            coordinator.RegisterRemotePeer(peerA);
            coordinator.RegisterRemotePeer(peerB);

            // Advance epoch to 2
            coordinator.ForceBenchRespawn(notifyPartner: false);
            coordinator.ResetAfterBenchRespawn();
            Assert.Equal(2u, coordinator.CurrentEpoch);

            // Peer A dies
            coordinator.OnRemotePartnerDied(peerA, 10f, 20f);
            Assert.False(coordinator.IsRemotePeerAlive(peerA));

            // Peer B's lower-epoch readiness snapshot must not resurrect peer A.
            var late = new DeathLifecycleCoordinator();
            coordinator.HandleDeathPacket(peerB, late.CreateLifecycleSnapshot());
            Assert.True(coordinator.IsRemotePeerAlive(peerB));
            Assert.False(coordinator.IsRemotePeerAlive(peerA));
            Assert.True(coordinator.IsPartnerAlive);
            Assert.Equal(2u, coordinator.CurrentEpoch);

            // Sender-specific epoch tracking accepts B's genuine epoch-1 death.
            coordinator.HandleDeathPacket(peerB,
                CreateDeathPacket(DeathEventType.SoloDeathOccurred, epoch: 1));
            Assert.False(coordinator.IsRemotePeerAlive(peerB));
            Assert.False(coordinator.IsRemotePeerAlive(peerA));
            Assert.False(coordinator.IsPartnerAlive);
            Assert.Equal(2u, coordinator.CurrentEpoch);
        }

        [Fact]
        public void Defect6_StaleSoloDeathFromLowerEpoch_IsStillRejected()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = new NetworkPeerId(2);

            // Advance to epoch 3
            coordinator.ForceBenchRespawn(notifyPartner: false);
            coordinator.ResetAfterBenchRespawn();
            coordinator.ForceBenchRespawn(notifyPartner: false);
            coordinator.ResetAfterBenchRespawn();
            Assert.Equal(3u, coordinator.CurrentEpoch);

            // Peer sends a stale SoloDeath from epoch 1 (e.g. delayed over high-latency network)
            var staleDeath = CreateDeathPacket(DeathEventType.SoloDeathOccurred, epoch: 1, x: 50f, y: 50f);
            coordinator.HandleDeathPacket(peer, staleDeath);

            // The stale death must be filtered out by epoch checking; partner remains alive
            Assert.True(coordinator.IsPartnerAlive);
            Assert.True(coordinator.IsRemotePeerAlive(peer));
        }

        #endregion

        #region Defect 9: Inventory Unlock Failure Rollback & Safe Retry

        [Fact]
        public void Defect9_OnItemUnlockedFailure_DoesNotCommitToUnlockedItems_AllowsSuccessfulRetry()
        {
            var service = new InventorySyncService();
            var peer = new NetworkPeerId(2);
            bool failHandler = true;
            int callbackFireCount = 0;

            service.OnItemUnlocked += (itemId, category) =>
            {
                callbackFireCount++;
                if (failHandler)
                {
                    throw new InvalidOperationException("Simulated scene loading transient failure");
                }
            };

            var txId = Guid.NewGuid();
            var packet = new ItemUnlockPacket
            {
                ItemId = "hasDash",
                Category = ItemCategory.Ability,
                TransactionId = txId
            };

            // First attempt: handler throws
            service.HandleItemUnlockPacket(peer, packet);

            // Handler fired once
            Assert.Equal(1, callbackFireCount);

            // Item was NOT committed to UnlockedItems
            Assert.False(service.IsItemUnlocked("hasDash"));
            Assert.DoesNotContain("hasDash", service.UnlockedItems);

            // Now transient issue resolves
            failHandler = false;

            // Peer retries with new transaction ID
            var retryPacket = new ItemUnlockPacket
            {
                ItemId = "hasDash",
                Category = ItemCategory.Ability,
                TransactionId = Guid.NewGuid()
            };

            service.HandleItemUnlockPacket(peer, retryPacket);

            // Callback fired second time and succeeded
            Assert.Equal(2, callbackFireCount);
            Assert.True(service.IsItemUnlocked("hasDash"));
            Assert.Contains("hasDash", service.UnlockedItems);
        }

        [Fact]
        public void Defect9_OnRemoteItemUnlockedFailure_DoesNotCommitToUnlockedItems_AllowsSuccessfulRetry()
        {
            var service = new InventorySyncService();
            var peer = new NetworkPeerId(2);
            bool failRemoteHandler = true;
            int remoteCallbacks = 0;

            service.OnRemoteItemUnlocked += (itemId, category) =>
            {
                remoteCallbacks++;
                if (failRemoteHandler)
                {
                    throw new InvalidOperationException("PlayerData instance is null during reload");
                }
            };

            var packet = new ItemUnlockPacket
            {
                ItemId = "Tool_StraightPin",
                Category = ItemCategory.Tool,
                TransactionId = Guid.NewGuid()
            };

            service.HandleItemUnlockPacket(peer, packet);

            Assert.Equal(1, remoteCallbacks);
            Assert.False(service.IsItemUnlocked("Tool_StraightPin"));

            // Recovery retry
            failRemoteHandler = false;
            var retryPacket = new ItemUnlockPacket
            {
                ItemId = "Tool_StraightPin",
                Category = ItemCategory.Tool,
                TransactionId = Guid.NewGuid()
            };

            service.HandleItemUnlockPacket(peer, retryPacket);

            Assert.Equal(2, remoteCallbacks);
            Assert.True(service.IsItemUnlocked("Tool_StraightPin"));
            Assert.Contains("Tool_StraightPin", service.UnlockedItems);
        }

        [Fact]
        public void Defect9_SameTransactionIdRetry_AfterFailure_AllowedToProcess()
        {
            var service = new InventorySyncService();
            var peer = new NetworkPeerId(2);
            bool transientFailure = true;
            bool successfullyApplied = false;

            service.OnItemUnlocked += (itemId, category) =>
            {
                if (transientFailure)
                {
                    throw new InvalidOperationException("Temporary database locked");
                }
                successfullyApplied = true;
            };

            var fixedTxId = Guid.NewGuid();
            var packet = new ItemUnlockPacket
            {
                ItemId = "Crest_Reaper",
                Category = ItemCategory.Crest,
                TransactionId = fixedTxId
            };

            // First attempt with fixedTxId fails
            service.HandleItemUnlockPacket(peer, packet);
            Assert.False(successfullyApplied);
            Assert.False(service.IsItemUnlocked("Crest_Reaper"));
            Assert.False(service.IsTransactionProcessed(fixedTxId));

            // Retrying with the EXACT SAME transaction ID (e.g. reliable transport redelivery)
            transientFailure = false;
            service.HandleItemUnlockPacket(peer, packet);

            // Successfully processed on redelivery!
            Assert.True(successfullyApplied);
            Assert.True(service.IsItemUnlocked("Crest_Reaper"));
            Assert.True(service.IsTransactionProcessed(fixedTxId));

            // Subsequent identical duplicate packet is rejected
            int duplicateCallbacks = 0;
            service.OnItemUnlocked += (_, _) => duplicateCallbacks++;
            service.HandleItemUnlockPacket(peer, packet);
            Assert.Equal(0, duplicateCallbacks);
        }

        [Fact]
        public void Defect9_SuccessfulApplication_DeduplicatesSubsequentIdenticalTransactions()
        {
            var service = new InventorySyncService();
            var peer = new NetworkPeerId(2);
            int invocations = 0;

            service.OnItemUnlocked += (_, _) => invocations++;

            var txId = Guid.NewGuid();
            var packet = new ItemUnlockPacket
            {
                ItemId = "hasDoubleJump",
                Category = ItemCategory.Ability,
                TransactionId = txId
            };

            // First successful run
            service.HandleItemUnlockPacket(peer, packet);
            Assert.Equal(1, invocations);
            Assert.True(service.IsItemUnlocked("hasDoubleJump"));

            // Duplicate with same transaction
            service.HandleItemUnlockPacket(peer, packet);
            Assert.Equal(1, invocations);

            // Duplicate with new transaction but already unlocked non-counter item
            var newTxPacket = new ItemUnlockPacket
            {
                ItemId = "hasDoubleJump",
                Category = ItemCategory.Ability,
                TransactionId = Guid.NewGuid()
            };
            service.HandleItemUnlockPacket(peer, newTxPacket);
            Assert.Equal(1, invocations);
        }

        [Fact]
        public async Task Defect9_ConcurrentRaceOnSameItem_WithTransientFailure_DoesNotLoseUnlock()
        {
            var service = new InventorySyncService();
            int calls = 0;
            service.OnItemUnlocked += (itemId, cat) =>
            {
                if (Interlocked.Increment(ref calls) <= 5)
                {
                    throw new InvalidOperationException("Transient failure");
                }
            };

            var tasks = new Task[20];
            for (int i = 0; i < 20; i++)
            {
                int idx = i;
                tasks[i] = Task.Run(() =>
                {
                    service.HandleItemUnlockPacket(new NetworkPeerId((uint)(100 + idx)), new ItemUnlockPacket
                    {
                        ItemId = "PureNail",
                        Category = ItemCategory.KeyItem,
                        TransactionId = Guid.NewGuid()
                    });
                });
            }
            await Task.WhenAll(tasks);

            Assert.True(service.IsItemUnlocked("PureNail"));
            Assert.Contains("PureNail", service.UnlockedItems);
            Assert.Equal(6, calls);
        }

        #endregion

        #region Defect 14: Bounded Counter Transaction Memory Growth

        [Fact]
        public void Defect14_CounterTransactions_UnderCapacity_AllTracked()
        {
            var service = new InventorySyncService();
            var guids = new List<Guid>();

            for (int i = 0; i < 200; i++)
            {
                var tx = Guid.NewGuid();
                guids.Add(tx);
                service.OnLocalCounterChanged("heartPieces:1", ItemCategory.KeyItem, tx);
            }

            Assert.Equal(200, service.ProcessedCounterTransactionsCount);

            foreach (var tx in guids)
            {
                Assert.True(service.IsTransactionProcessed(tx));
            }
        }

        [Fact]
        public void Defect14_CounterTransactions_Exceeding500_EvictsOldestFIFO()
        {
            var service = new InventorySyncService();
            var oldestTransactions = new List<Guid>();

            // Generate first 100 transactions
            for (int i = 0; i < 100; i++)
            {
                var tx = Guid.NewGuid();
                oldestTransactions.Add(tx);
                service.OnLocalCounterChanged("heartPieces:1", ItemCategory.KeyItem, tx);
            }

            Assert.Equal(100, service.ProcessedCounterTransactionsCount);

            // Generate 500 more transactions (total 600)
            var newestTransactions = new List<Guid>();
            for (int i = 0; i < 500; i++)
            {
                var tx = Guid.NewGuid();
                newestTransactions.Add(tx);
                service.OnLocalCounterChanged($"heartPieces:{(i % 4) + 1}", ItemCategory.KeyItem, tx);
            }

            // Invariant 1: Count is strictly bounded at 500
            Assert.Equal(500, service.ProcessedCounterTransactionsCount);

            // Invariant 2: The oldest 100 transactions are evicted
            foreach (var oldTx in oldestTransactions)
            {
                Assert.False(service.IsTransactionProcessed(oldTx));
            }

            // Invariant 3: The newest 500 transactions are retained
            foreach (var newTx in newestTransactions)
            {
                Assert.True(service.IsTransactionProcessed(newTx));
            }
        }

        [Fact]
        public void Defect14_RemoteCounterTransactions_ViaHandleItemUnlockPacket_AlsoBoundedAt500()
        {
            var service = new InventorySyncService();
            var peer = new NetworkPeerId(2);
            var oldestTx = new List<Guid>();

            for (int i = 0; i < 50; i++)
            {
                var tx = Guid.NewGuid();
                oldestTx.Add(tx);
                service.HandleItemUnlockPacket(peer, new ItemUnlockPacket
                {
                    ItemId = "silkParts:1",
                    Category = ItemCategory.KeyItem,
                    TransactionId = tx
                });
            }

            for (int i = 0; i < 500; i++)
            {
                service.HandleItemUnlockPacket(peer, new ItemUnlockPacket
                {
                    ItemId = "silkParts:2",
                    Category = ItemCategory.KeyItem,
                    TransactionId = Guid.NewGuid()
                });
            }

            Assert.Equal(500, service.ProcessedCounterTransactionsCount);

            // Oldest are evicted
            foreach (var tx in oldestTx)
            {
                Assert.False(service.IsTransactionProcessed(tx));
            }
        }

        [Fact]
        public void Defect14_HeavyStress_1500CounterTransactions_MaintainsStrictBound()
        {
            var service = new InventorySyncService();

            for (int i = 0; i < 1500; i++)
            {
                var tx = Guid.NewGuid();
                service.OnLocalCounterChanged("silkParts:1", ItemCategory.KeyItem, tx);
                Assert.True(service.ProcessedCounterTransactionsCount <= 500);
            }

            Assert.Equal(500, service.ProcessedCounterTransactionsCount);
        }

        [Fact]
        public void Defect14_Reset_ClearsCounterTransactionsAndFifoQueue()
        {
            var service = new InventorySyncService();

            for (int i = 0; i < 300; i++)
            {
                service.OnLocalCounterChanged("heartPieces:2", ItemCategory.KeyItem, Guid.NewGuid());
            }

            Assert.Equal(300, service.ProcessedCounterTransactionsCount);

            // Call Reset()
            service.Reset();

            Assert.Equal(0, service.ProcessedCounterTransactionsCount);

            // Verify queue is clean: add 1 new transaction
            var freshTx = Guid.NewGuid();
            service.OnLocalCounterChanged("heartPieces:2", ItemCategory.KeyItem, freshTx);

            Assert.Equal(1, service.ProcessedCounterTransactionsCount);
            Assert.True(service.IsTransactionProcessed(freshTx));
        }

        [Fact]
        public void Defect14_ResetSessionState_ClearsBothRegularAndCounterTransactions()
        {
            var service = new InventorySyncService();

            service.OnLocalItemDiscovered("dash", ItemCategory.Ability, Guid.NewGuid());
            service.OnLocalCounterChanged("heartPieces:1", ItemCategory.KeyItem, Guid.NewGuid());

            Assert.Equal(1, service.ProcessedTransactionsCount);
            Assert.Equal(1, service.ProcessedCounterTransactionsCount);

            service.ResetSessionState();

            Assert.Equal(0, service.ProcessedTransactionsCount);
            Assert.Equal(0, service.ProcessedCounterTransactionsCount);
            Assert.Empty(service.UnlockedItems);
        }

        #endregion
    }
}
