using System;
using System.Collections.Generic;
using System.Net;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.Revive;
using SilksongCoop.Core.State;
using SilksongCoop.Core.Visuals;
using SilksongCoop.Tests.TestHarness;
using Xunit;

namespace SilksongCoop.Tests.E2ERemediationTests
{
    /// <summary>
    /// Tier 1: Category-Partition Feature Coverage Tests.
    /// Covers all 18 defects (F01 through F18) with at least 5 isolated happy-path / equivalence-class
    /// test cases per defect (Total: 90 tests).
    /// </summary>
    public class Tier1FeatureCoverageTests
    {
        #region F01: Whitelist PlayerData bools (SilksongCoopPlugin.cs:1186, R1)

        [Fact]
        public void F01_01_CoreMovementAbilities_AreAllowed()
        {
            Assert.True(RemediationTestHarness.IsSaveBoolAllowed("hasDash", ItemCategory.Ability));
            Assert.True(RemediationTestHarness.IsSaveBoolAllowed("hasWalljump", ItemCategory.Ability));
            Assert.True(RemediationTestHarness.IsSaveBoolAllowed("hasDoubleJump", ItemCategory.Ability));
            Assert.True(RemediationTestHarness.IsSaveBoolAllowed("hasSuperJump", ItemCategory.Ability));
        }

        [Fact]
        public void F01_02_CombatAndSilkAbilities_AreAllowed()
        {
            Assert.True(RemediationTestHarness.IsSaveBoolAllowed("hasHarpoonDash", ItemCategory.Ability));
            Assert.True(RemediationTestHarness.IsSaveBoolAllowed("hasSilkSpecial", ItemCategory.Ability));
            Assert.True(RemediationTestHarness.IsSaveBoolAllowed("hasNeedleThrow", ItemCategory.Ability));
            Assert.True(RemediationTestHarness.IsSaveBoolAllowed("hasThreadSphere", ItemCategory.Ability));
            Assert.True(RemediationTestHarness.IsSaveBoolAllowed("hasParry", ItemCategory.Ability));
            Assert.True(RemediationTestHarness.IsSaveBoolAllowed("hasSilkCharge", ItemCategory.Ability));
            Assert.True(RemediationTestHarness.IsSaveBoolAllowed("hasSilkBomb", ItemCategory.Ability));
        }

        [Fact]
        public void F01_03_NeedolinAndUtilityAbilities_AreAllowed()
        {
            Assert.True(RemediationTestHarness.IsSaveBoolAllowed("hasSilkBossNeedle", ItemCategory.Ability));
            Assert.True(RemediationTestHarness.IsSaveBoolAllowed("hasNeedolin", ItemCategory.Ability));
            Assert.True(RemediationTestHarness.IsSaveBoolAllowed("hasNeedolinMemoryPowerup", ItemCategory.Ability));
            Assert.True(RemediationTestHarness.IsSaveBoolAllowed("hasBrolly", ItemCategory.Ability));
            Assert.True(RemediationTestHarness.IsSaveBoolAllowed("hasChargeSlash", ItemCategory.Ability));
            Assert.True(RemediationTestHarness.IsSaveBoolAllowed("hasQuill", ItemCategory.Ability));
        }

        [Fact]
        public void F01_04_UnauthorizedPersistentFields_AreRejected()
        {
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed("isInvincible", ItemCategory.Ability));
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed("permaDeath", ItemCategory.Ability));
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed("infiniteSilk", ItemCategory.Ability));
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed("debugConsoleEnabled", ItemCategory.Ability));
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed("unlockedAllMaps", ItemCategory.Ability));
        }

        [Fact]
        public void F01_05_KeyItemCategory_AllowsOnlyAuthorizedKeys()
        {
            Assert.True(RemediationTestHarness.IsSaveBoolAllowed("simpleKey", ItemCategory.KeyItem));
            Assert.True(RemediationTestHarness.IsSaveBoolAllowed("bellTowerKey", ItemCategory.KeyItem));
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed("hasDash", ItemCategory.KeyItem)); // Wrong category
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed("masterCheatKey", ItemCategory.KeyItem));
        }

        #endregion

        #region F02: WalletSeed retry mutation fix (SilksongCoopPlugin.cs:1630, R1)

        [Fact]
        public void F02_01_SinglePeer_RetryDecrementsCleanlyWithoutException()
        {
            var outstanding = new Dictionary<NetworkPeerId, byte>
            {
                { new NetworkPeerId(101), 3 }
            };

            bool success = RemediationTestHarness.PumpWalletSeedSafe(
                outstanding,
                peer => false, // Delivery failed, must retry
                out bool failed);

            Assert.True(success);
            Assert.False(failed);
            Assert.Single(outstanding);
            Assert.Equal(2, outstanding[new NetworkPeerId(101)]);
        }

        [Fact]
        public void F02_02_MultiplePeers_RetryDecrementsConcurrentlyWithoutModifyingCollectionDuringEnum()
        {
            var peer1 = new NetworkPeerId(101);
            var peer2 = new NetworkPeerId(102);
            var peer3 = new NetworkPeerId(103);
            var outstanding = new Dictionary<NetworkPeerId, byte>
            {
                { peer1, 3 },
                { peer2, 2 },
                { peer3, 4 }
            };

            bool success = RemediationTestHarness.PumpWalletSeedSafe(
                outstanding,
                peer => false, // None delivered
                out bool failed);

            Assert.True(success);
            Assert.False(failed);
            Assert.Equal(3, outstanding.Count);
            Assert.Equal(2, outstanding[peer1]);
            Assert.Equal(1, outstanding[peer2]);
            Assert.Equal(3, outstanding[peer3]);
        }

        [Fact]
        public void F02_03_DeliveredPeer_RemovedFromOutstandingList()
        {
            var peer1 = new NetworkPeerId(101);
            var peer2 = new NetworkPeerId(102);
            var outstanding = new Dictionary<NetworkPeerId, byte>
            {
                { peer1, 3 },
                { peer2, 3 }
            };

            // Peer 1 delivered, Peer 2 needs retry
            bool success = RemediationTestHarness.PumpWalletSeedSafe(
                outstanding,
                peer => peer == peer1,
                out bool failed);

            Assert.True(success);
            Assert.False(failed);
            Assert.Single(outstanding);
            Assert.False(outstanding.ContainsKey(peer1));
            Assert.Equal(2, outstanding[peer2]);
        }

        [Fact]
        public void F02_04_ExhaustedRetry_TriggersConnectionFailureAndClearsOutstanding()
        {
            var peer1 = new NetworkPeerId(101);
            var outstanding = new Dictionary<NetworkPeerId, byte>
            {
                { peer1, 1 } // Only 1 attempt left, fails
            };

            bool success = RemediationTestHarness.PumpWalletSeedSafe(
                outstanding,
                peer => false,
                out bool failed);

            Assert.False(success);
            Assert.True(failed);
            Assert.Empty(outstanding);
        }

        [Fact]
        public void F02_05_AllDelivered_EmptiesOutstandingDictionarySuccessfully()
        {
            var peer1 = new NetworkPeerId(101);
            var peer2 = new NetworkPeerId(102);
            var outstanding = new Dictionary<NetworkPeerId, byte>
            {
                { peer1, 2 },
                { peer2, 2 }
            };

            bool success = RemediationTestHarness.PumpWalletSeedSafe(
                outstanding,
                peer => true, // All delivered
                out bool failed);

            Assert.True(success);
            Assert.False(failed);
            Assert.Empty(outstanding);
        }

        #endregion

        #region F03: Catch-up currency readiness (VitalsSyncManager.cs:167, R1)

        [Fact]
        public void F03_01_ReadyPeer_AcceptsValidCurrencyDelta()
        {
            var service = new VitalsSyncService();
            service.SeedWallet(CurrencyType.Rosaries, 100, 0);

            var peer = new NetworkPeerId(2);
            var packet = new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 25,
                TotalWalletAmount = 125,
                TransactionId = Guid.NewGuid(),
                PickupUniqueId = 1
            };

            service.HandleCurrencyPacket(peer, packet);
            Assert.Equal(125, service.SharedRosaries);
        }

        [Fact]
        public void F03_02_MonotonicSequenceWatermark_AdvancesWithNewerSequence()
        {
            var service = new VitalsSyncService();
            service.SeedWallet(CurrencyType.Rosaries, 50, 0);

            var peer = new NetworkPeerId(2);
            var tx1 = new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 10,
                TotalWalletAmount = 60,
                TransactionId = Guid.NewGuid(),
                PickupUniqueId = 1
            };
            var tx2 = new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 20,
                TotalWalletAmount = 80,
                TransactionId = Guid.NewGuid(),
                PickupUniqueId = 2
            };

            service.HandleCurrencyPacket(peer, tx1);
            service.HandleCurrencyPacket(peer, tx2);

            Assert.Equal(80, service.SharedRosaries);
        }

        [Fact]
        public void F03_03_StaleSequence_IsRejectedWhenWatermarkAlreadyAdvanced()
        {
            var service = new VitalsSyncService();
            service.SeedWallet(CurrencyType.Rosaries, 100, 0);

            var peer = new NetworkPeerId(2);
            var txNewer = new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 50,
                TotalWalletAmount = 150,
                TransactionId = Guid.NewGuid(),
                PickupUniqueId = 5
            };
            var txOlder = new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 10,
                TotalWalletAmount = 110,
                TransactionId = Guid.NewGuid(),
                PickupUniqueId = 2 // Lower than 5
            };

            service.HandleCurrencyPacket(peer, txNewer);
            service.HandleCurrencyPacket(peer, txOlder);

            // txOlder should be rejected, balance remains 150
            Assert.Equal(150, service.SharedRosaries);
        }

        [Fact]
        public void F03_04_ZeroDeltaPickupClaim_DoesNotAlterSharedBalance()
        {
            var service = new VitalsSyncService();
            service.SeedWallet(CurrencyType.Rosaries, 200, 0);

            var peer = new NetworkPeerId(2);
            var claim = new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 0,
                TotalWalletAmount = 200,
                TransactionId = Guid.NewGuid(),
                PickupUniqueId = 1
            };

            service.HandleCurrencyPacket(peer, claim);
            Assert.Equal(200, service.SharedRosaries);
        }

        [Fact]
        public void F03_05_PostHandshakeReadiness_ProcessesValidTransactionsInOrder()
        {
            var service = new VitalsSyncService();
            service.SeedWallet(CurrencyType.ShellShards, 10, 0);

            var peer = new NetworkPeerId(3);
            for (uint i = 1; i <= 5; i++)
            {
                service.HandleCurrencyPacket(peer, new CurrencySyncPacket
                {
                    Currency = CurrencyType.ShellShards,
                    DeltaAmount = 5,
                    TotalWalletAmount = 10 + (int)i * 5,
                    TransactionId = Guid.NewGuid(),
                    PickupUniqueId = i
                });
            }

            Assert.Equal(35, service.SharedShellShards);
        }

        #endregion

        #region F04: WalletSeed 3rd client delta (VitalsSyncService.cs:604, R1)

        [Fact]
        public void F04_01_PreSeedRemotePositiveDelta_PreservedWhenHostSeedArrives()
        {
            var service = new VitalsSyncService();
            // Client 2 sends +75 rosaries before host seed arrives
            RemediationTestHarness.SeedWalletWithRemoteDelta(
                service,
                CurrencyType.Rosaries,
                total: 200,
                pendingLocalDelta: 10,
                preSeedRemoteDelta: 75);

            Assert.Equal(285, service.SharedRosaries);
        }

        [Fact]
        public void F04_02_PreSeedRemoteNegativeDelta_DeductedFromSeededBalance()
        {
            var service = new VitalsSyncService();
            // Client 2 spent -40 rosaries before host seed arrives
            RemediationTestHarness.SeedWalletWithRemoteDelta(
                service,
                CurrencyType.Rosaries,
                total: 150,
                pendingLocalDelta: 0,
                preSeedRemoteDelta: -40);

            Assert.Equal(110, service.SharedRosaries);
        }

        [Fact]
        public void F04_03_ShellShards_PreSeedDelta_PreservedCorrectly()
        {
            var service = new VitalsSyncService();
            RemediationTestHarness.SeedWalletWithRemoteDelta(
                service,
                CurrencyType.ShellShards,
                total: 50,
                pendingLocalDelta: 5,
                preSeedRemoteDelta: 20);

            Assert.Equal(75, service.SharedShellShards);
        }

        [Fact]
        public void F04_04_ConcurrentLocalAndRemotePreSeedDeltas_BothPreserved()
        {
            var service = new VitalsSyncService();
            // Local collected 30, Remote collected 45, Host base is 500
            RemediationTestHarness.SeedWalletWithRemoteDelta(
                service,
                CurrencyType.Rosaries,
                total: 500,
                pendingLocalDelta: 30,
                preSeedRemoteDelta: 45);

            Assert.Equal(575, service.SharedRosaries);
        }

        [Fact]
        public void F04_05_SubsequentTransactionsAfterSeed_AccumulateOnTopPreservedDeltas()
        {
            var service = new VitalsSyncService();
            RemediationTestHarness.SeedWalletWithRemoteDelta(
                service,
                CurrencyType.Rosaries,
                total: 100,
                pendingLocalDelta: 0,
                preSeedRemoteDelta: 50);

            Assert.Equal(150, service.SharedRosaries);

            var peer = new NetworkPeerId(2);
            service.HandleCurrencyPacket(peer, new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 25,
                TotalWalletAmount = 175,
                TransactionId = Guid.NewGuid(),
                PickupUniqueId = 10
            });

            Assert.Equal(175, service.SharedRosaries);
        }

        #endregion

        #region F05: Stat upgrade channel idempotency (SilksongCoopPlugin.cs:1085, R1)

        [Fact]
        public void F05_01_FragmentsArriveBeforeMaxHealth_UpgradesExactlyOnce()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();
            Assert.Equal(5, controller.CurrentMaxHealth);

            // 4 fragments complete mask
            controller.ApplyHeartPieceFragments(fragmentTotal: 4, sharedMaxHealth: 5);
            Assert.Equal(6, controller.CurrentMaxHealth);
            Assert.Equal(1, controller.AddToMaxHealthCallCount);

            // Delayed VitalsSyncPacket arrives with new max (6)
            controller.OnSharedMaxHealthReceived(6);
            Assert.Equal(6, controller.CurrentMaxHealth);
            Assert.Equal(1, controller.AddToMaxHealthCallCount); // No duplicate invocation
        }

        [Fact]
        public void F05_02_MaxHealthArrivesBeforeFragments_DoesNotDuplicateUpgrade()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();
            Assert.Equal(5, controller.CurrentMaxHealth);

            // VitalsSyncPacket arrives first (updates base to 6)
            controller.OnSharedMaxHealthReceived(6);
            Assert.Equal(6, controller.CurrentMaxHealth);

            // Then fragment packet arrives (4 fragments)
            controller.ApplyHeartPieceFragments(fragmentTotal: 4, sharedMaxHealth: 6);
            Assert.Equal(6, controller.CurrentMaxHealth);
            Assert.Equal(1, controller.AddToMaxHealthCallCount); // Upgraded once from VitalsSyncPacket, prevented duplicate call
        }

        [Fact]
        public void F05_03_SilkPartsBeforeMaxSilk_UpgradesExactlyOnce()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();
            Assert.Equal(1, controller.CurrentMaxSilkSpools);

            // 3 fragments complete 1 spool
            controller.ApplySilkPartFragments(fragmentTotal: 3, sharedMaxSilkSpools: 1);
            Assert.Equal(2, controller.CurrentMaxSilkSpools);
            Assert.Equal(1, controller.AddToMaxSilkCallCount);

            // VitalsSyncPacket arrives with max spool 2
            controller.OnSharedMaxSilkReceived(2);
            Assert.Equal(2, controller.CurrentMaxSilkSpools);
            Assert.Equal(1, controller.AddToMaxSilkCallCount);
        }

        [Fact]
        public void F05_04_MaxSilkArrivesBeforeSilkParts_DoesNotDuplicateUpgrade()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();
            controller.OnSharedMaxSilkReceived(2);
            Assert.Equal(2, controller.CurrentMaxSilkSpools);

            controller.ApplySilkPartFragments(fragmentTotal: 3, sharedMaxSilkSpools: 2);
            Assert.Equal(2, controller.CurrentMaxSilkSpools);
            Assert.Equal(1, controller.AddToMaxSilkCallCount); // Upgraded once from VitalsSyncPacket, prevented duplicate call
        }

        [Fact]
        public void F05_05_PartialFragments_DoNotTriggerMaxUpgrade()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();
            // 2 heart pieces (not a multiple of 4)
            controller.ApplyHeartPieceFragments(fragmentTotal: 2, sharedMaxHealth: 5);
            Assert.Equal(5, controller.CurrentMaxHealth);
            Assert.Equal(0, controller.AddToMaxHealthCallCount);

            // 1 silk part (not a multiple of 3)
            controller.ApplySilkPartFragments(fragmentTotal: 1, sharedMaxSilkSpools: 1);
            Assert.Equal(1, controller.CurrentMaxSilkSpools);
            Assert.Equal(0, controller.AddToMaxSilkCallCount);
        }

        #endregion

        #region F06: Death epoch synchronization (DeathLifecycleCoordinator.cs:905, R2)

        [Fact]
        public void F06_01_InboundHigherEpoch_AdoptsHigherEpochImmediately()
        {
            var coordinator = new DeathLifecycleCoordinator();
            Assert.Equal(1u, coordinator.CurrentEpoch);

            var peer = new NetworkPeerId(2);
            var packet = new DeathLifecyclePacket
            {
                EventType = DeathEventType.BenchCheckpointUpdate,
                Epoch = 3
            };

            coordinator.HandleDeathPacket(peer, packet);
            Assert.Equal(3u, coordinator.CurrentEpoch);
        }

        [Fact]
        public void F06_02_MatchingEpochDeathPacket_ProcessesNormally()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.IsPartnerAlive = true;

            var peer = new NetworkPeerId(2);
            var packet = new DeathLifecyclePacket
            {
                EventType = DeathEventType.SoloDeathOccurred,
                CoordinateX = 15f,
                CoordinateY = 25f,
                Epoch = 1
            };

            coordinator.HandleDeathPacket(peer, packet);
            Assert.False(coordinator.IsPartnerAlive);
        }

        [Fact]
        public void F06_03_LowerEpochPacket_DiscardedAsStale()
        {
            var coordinator = new DeathLifecycleCoordinator();
            // Advance to epoch 5
            coordinator.HandleDeathPacket(new NetworkPeerId(2), new DeathLifecyclePacket
            {
                EventType = DeathEventType.BenchCheckpointUpdate,
                Epoch = 5
            });
            Assert.Equal(5u, coordinator.CurrentEpoch);

            // Stale death packet with epoch 2 arrives
            coordinator.IsPartnerAlive = true;
            coordinator.HandleDeathPacket(new NetworkPeerId(2), new DeathLifecyclePacket
            {
                EventType = DeathEventType.SoloDeathOccurred,
                CoordinateX = 10f,
                CoordinateY = 10f,
                Epoch = 2
            });

            // Partner should still be alive because epoch 2 was stale
            Assert.True(coordinator.IsPartnerAlive);
        }

        [Fact]
        public void F06_04_SoloBenchRespawn_SurvivesEpochMismatchForRecovery()
        {
            var coordinator = new DeathLifecycleCoordinator();
            // Partner was dead
            coordinator.IsPartnerAlive = false;

            // Partner recovered at a bench with epoch 1 while our coordinator was at epoch 3
            coordinator.HandleDeathPacket(new NetworkPeerId(2), new DeathLifecyclePacket
            {
                EventType = DeathEventType.SoloBenchRespawn,
                Epoch = 1
            });

            Assert.True(coordinator.IsPartnerAlive);
        }

        [Fact]
        public void F06_05_TeamWipeEvent_InMatchingEpoch_TransitionsToTeamWipeState()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = new NetworkPeerId(2);

            coordinator.HandleDeathPacket(peer, new DeathLifecyclePacket
            {
                EventType = DeathEventType.TeamWipeOccurred,
                Epoch = 1
            });

            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
        }

        #endregion

        #region F07: Enemy host authority renewal (EnemySyncService.cs:583, R2)

        [Fact]
        public void F07_01_LowerHpSnapshot_AcceptedAndUpdatesHostAuthority()
        {
            var service = new EnemySyncService();
            bool accepted = service.TryAcceptSnapshot(1, 100, dead: false, timestamp: 100);
            Assert.True(accepted);
            Assert.True(service.HasFreshHostAuthority(1));
        }

        [Fact]
        public void F07_02_MatchingHpConfirmingSnapshot_RenewsHostAuthority()
        {
            var service = new EnemySyncService();
            service.TryAcceptSnapshot(1, 100, dead: false, timestamp: 100);
            service.NoteLocalEnemyState(1, 80, dead: false);

            // Confirming snapshot with matching HP (80)
            bool accepted = service.TryAcceptSnapshot(1, 80, dead: false, timestamp: 105);
            Assert.True(accepted);
            Assert.True(service.HasFreshHostAuthority(1));
        }

        [Fact]
        public void F07_03_HostAuthorityRemainsFreshWithinTtl()
        {
            var service = new EnemySyncService();
            service.TryAcceptSnapshot(1, 50, dead: false, timestamp: 200);

            // Immediately fresh
            Assert.True(service.HasFreshHostAuthority(1));
        }

        [Fact]
        public void F07_04_DeadSnapshot_ConfirmsKillAndExtendsAuthority()
        {
            var service = new EnemySyncService();
            service.TryAcceptSnapshot(1, 50, dead: false, timestamp: 100);
            service.NoteLocalEnemyState(1, 40, dead: false);

            // Dead snapshot arrives
            bool accepted = service.TryAcceptSnapshot(1, 0, dead: true, timestamp: 110);
            Assert.True(accepted);
            Assert.True(service.HasFreshHostAuthority(1));
        }

        [Fact]
        public void F07_05_HigherHpSnapshotAfterLocalHit_RejectedToPreventHealing()
        {
            var service = new EnemySyncService();
            service.TryAcceptSnapshot(1, 100, dead: false, timestamp: 100);
            service.NoteLocalEnemyState(1, 80, dead: false);

            // Stale snapshot with HP 100 arriving after local hit to 80
            bool accepted = service.TryAcceptSnapshot(1, 100, dead: false, timestamp: 101);
            Assert.False(accepted);
        }

        #endregion

        #region F08: Unloaded enemy damage accumulation (EnemySyncController.cs:2059, R2)

        [Fact]
        public void F08_01_MultiHitClientAttacks_AccumulateToKillUnloadedEnemy()
        {
            var accumulator = new RemediationTestHarness.UnloadedEnemyDamageAccumulator();
            ulong enemyKey = 999;

            accumulator.RecordHit(enemyKey, 30, isDead: false);
            accumulator.RecordHit(enemyKey, 30, isDead: false);
            accumulator.RecordHit(enemyKey, 40, isDead: true); // 30 + 30 + 40 = 100

            Assert.Equal(100, accumulator.GetAccumulatedDamage(enemyKey));

            // Host loads room with 100 HP enemy
            bool killResolved = accumulator.TryResolvePendingKill(enemyKey, loadedHp: 100, out int totalDamage);
            Assert.True(killResolved);
            Assert.Equal(100, totalDamage);
        }

        [Fact]
        public void F08_02_SingleLethalHit_ResolvesKill()
        {
            var accumulator = new RemediationTestHarness.UnloadedEnemyDamageAccumulator();
            ulong enemyKey = 888;

            accumulator.RecordHit(enemyKey, 150, isDead: true);

            bool killResolved = accumulator.TryResolvePendingKill(enemyKey, loadedHp: 120, out int totalDamage);
            Assert.True(killResolved);
            Assert.Equal(150, totalDamage);
        }

        [Fact]
        public void F08_03_InsufficientAccumulatedDamage_RejectsKill()
        {
            var accumulator = new RemediationTestHarness.UnloadedEnemyDamageAccumulator();
            ulong enemyKey = 777;

            // Only 60 damage recorded, but enemy has 100 HP
            accumulator.RecordHit(enemyKey, 30, isDead: false);
            accumulator.RecordHit(enemyKey, 30, isDead: true);

            bool killResolved = accumulator.TryResolvePendingKill(enemyKey, loadedHp: 100, out int totalDamage);
            Assert.False(killResolved);
            Assert.Equal(0, totalDamage);
        }

        [Fact]
        public void F08_04_ResolvedKill_ClearsAccumulatorCache()
        {
            var accumulator = new RemediationTestHarness.UnloadedEnemyDamageAccumulator();
            ulong enemyKey = 666;

            accumulator.RecordHit(enemyKey, 50, isDead: true);
            accumulator.TryResolvePendingKill(enemyKey, loadedHp: 50, out _);

            Assert.Equal(0, accumulator.GetAccumulatedDamage(enemyKey));
            Assert.False(accumulator.TryResolvePendingKill(enemyKey, loadedHp: 50, out _));
        }

        [Fact]
        public void F08_05_ZeroOrNegativeDamage_IgnoredByAccumulator()
        {
            var accumulator = new RemediationTestHarness.UnloadedEnemyDamageAccumulator();
            ulong enemyKey = 555;

            accumulator.RecordHit(enemyKey, 0, isDead: false);
            accumulator.RecordHit(enemyKey, -10, isDead: false);

            Assert.Equal(0, accumulator.GetAccumulatedDamage(enemyKey));
        }

        #endregion

        #region F09: Inventory unlock retry rollback (InventorySyncService.cs:261, R2)

        [Fact]
        public void F09_01_SuccessfulUnlock_RegistersInUnlockedItems()
        {
            var service = new InventorySyncService();
            var peer = new NetworkPeerId(2);
            var packet = new ItemUnlockPacket
            {
                ItemId = "hasDash",
                Category = ItemCategory.Ability,
                TransactionId = Guid.NewGuid()
            };

            service.HandleItemUnlockPacket(peer, packet);
            Assert.Contains("hasDash", service.UnlockedItems);
        }

        [Fact]
        public void F09_02_DuplicateUnlock_DoesNotRaiseDuplicateEvents()
        {
            var service = new InventorySyncService();
            var peer = new NetworkPeerId(2);
            var txId = Guid.NewGuid();
            int unlockEvents = 0;

            service.OnItemUnlocked += (item, cat) => unlockEvents++;

            var packet1 = new ItemUnlockPacket
            {
                ItemId = "hasWalljump",
                Category = ItemCategory.Ability,
                TransactionId = txId
            };
            var packet2 = new ItemUnlockPacket
            {
                ItemId = "hasWalljump",
                Category = ItemCategory.Ability,
                TransactionId = txId
            };

            service.HandleItemUnlockPacket(peer, packet1);
            service.HandleItemUnlockPacket(peer, packet2);

            Assert.Equal(1, unlockEvents);
        }

        [Fact]
        public void F09_03_RollbackOnFailure_AllowsSuccessfulRetry()
        {
            var service = new InventorySyncService();
            var peer = new NetworkPeerId(2);
            var txId = Guid.NewGuid();
            bool simulateFailure = true;
            bool retrySucceeded = false;

            service.OnItemUnlocked += (item, cat) =>
            {
                if (simulateFailure)
                {
                    throw new InvalidOperationException("Simulated transient handler error");
                }
                retrySucceeded = true;
            };

            var packet = new ItemUnlockPacket
            {
                ItemId = "hasDoubleJump",
                Category = ItemCategory.Ability,
                TransactionId = txId
            };

            try
            {
                service.HandleItemUnlockPacket(peer, packet);
            }
            catch (InvalidOperationException)
            {
                // Handled in pipeline
            }

            // Retry attempt with simulateFailure = false
            simulateFailure = false;
            var retryPacket = new ItemUnlockPacket
            {
                ItemId = "hasDoubleJump",
                Category = ItemCategory.Ability,
                TransactionId = Guid.NewGuid() // New retry transaction
            };

            service.HandleItemUnlockPacket(peer, retryPacket);
            Assert.True(retrySucceeded);
            Assert.Contains("hasDoubleJump", service.UnlockedItems);
        }

        [Fact]
        public void F09_04_MultipleDistinctItems_AllUnlockSuccessfully()
        {
            var service = new InventorySyncService();
            var peer = new NetworkPeerId(2);

            service.HandleItemUnlockPacket(peer, new ItemUnlockPacket
            {
                ItemId = "itemA",
                Category = ItemCategory.Tool,
                TransactionId = Guid.NewGuid()
            });
            service.HandleItemUnlockPacket(peer, new ItemUnlockPacket
            {
                ItemId = "itemB",
                Category = ItemCategory.Tool,
                TransactionId = Guid.NewGuid()
            });

            Assert.Contains("itemA", service.UnlockedItems);
            Assert.Contains("itemB", service.UnlockedItems);
            Assert.Equal(2, service.UnlockedItems.Count);
        }

        [Fact]
        public void F09_05_ResetInventory_ClearsUnlockedItemsCollection()
        {
            var service = new InventorySyncService();
            var peer = new NetworkPeerId(2);

            service.HandleItemUnlockPacket(peer, new ItemUnlockPacket
            {
                ItemId = "hasDash",
                Category = ItemCategory.Ability,
                TransactionId = Guid.NewGuid()
            });
            Assert.Single(service.UnlockedItems);

            service.ResetSessionState();
            Assert.Empty(service.UnlockedItems);
        }

        #endregion

        #region F10: Revive wall check & safe fallback (UnityGroundProber.cs:181, R2)

        [Fact]
        public void F10_01_ClearGround_SelectedAtOffsetZero()
        {
            var prober = new RemediationTestHarness.SolidGeometryGroundProber();
            prober.Platforms.Add(new RemediationTestHarness.SolidGeometryGroundProber.Platform
            {
                MinX = -5f,
                MaxX = 5f,
                SurfaceY = 10f,
                NormalY = 1.0f
            });

            bool found = prober.TryFindSafeGround(0f, 10f, out float safeX, out float safeY);
            Assert.True(found);
            Assert.Equal(0f, safeX);
            Assert.Equal(10.81f, safeY, 2);
        }

        [Fact]
        public void F10_02_WallAtOffsetZero_SelectsAdjacentSafeOffset()
        {
            var prober = new RemediationTestHarness.SolidGeometryGroundProber();
            prober.Platforms.Add(new RemediationTestHarness.SolidGeometryGroundProber.Platform
            {
                MinX = -5f,
                MaxX = 5f,
                SurfaceY = 10f,
                NormalY = 1.0f
            });

            // Solid wall obstructing offset 0.0
            prober.Walls.Add(new RemediationTestHarness.SolidGeometryGroundProber.WallObstacle
            {
                MinX = -0.5f,
                MaxX = 0.5f,
                MinY = 9.5f,
                MaxY = 12.0f
            });

            bool found = prober.TryFindSafeGround(0f, 10f, out float safeX, out float safeY);
            Assert.True(found);
            Assert.Equal(1.2f, safeX, 1); // Checked next candidate offset +1.2
            Assert.Equal(10.81f, safeY, 2);
        }

        [Fact]
        public void F10_03_SteepSlope_RejectedAsUnsafe()
        {
            var prober = new RemediationTestHarness.SolidGeometryGroundProber();
            // Slope > 45 degrees directly below
            prober.Platforms.Add(new RemediationTestHarness.SolidGeometryGroundProber.Platform
            {
                MinX = -1f,
                MaxX = 1f,
                SurfaceY = 10f,
                NormalY = 0.5f // Below 0.707
            });
            // Safe flat platform at +1.2
            prober.Platforms.Add(new RemediationTestHarness.SolidGeometryGroundProber.Platform
            {
                MinX = 1.0f,
                MaxX = 3.0f,
                SurfaceY = 10f,
                NormalY = 1.0f
            });

            bool found = prober.TryFindSafeGround(0f, 10f, out float safeX, out float safeY);
            Assert.True(found);
            Assert.Equal(1.2f, safeX, 1);
        }

        [Fact]
        public void F10_04_SurroundedByWalls_CascadesToHazardCheckpointFallback()
        {
            var prober = new RemediationTestHarness.SolidGeometryGroundProber();
            // Solid walls across all lateral candidate offsets (-3.6 to +3.6)
            prober.Walls.Add(new RemediationTestHarness.SolidGeometryGroundProber.WallObstacle
            {
                MinX = -5f,
                MaxX = 5f,
                MinY = 9f,
                MaxY = 15f
            });
            prober.FallbackHazardLocation = (45.0f, 20.0f);

            bool found = prober.TryFindSafeGround(0f, 10f, out float safeX, out float safeY);
            Assert.True(found);
            Assert.Equal(45.0f, safeX);
            Assert.Equal(20.0f, safeY);
        }

        [Fact]
        public void F10_05_NoGroundAndNoFallback_ReportsFailureWithoutClipping()
        {
            var prober = new RemediationTestHarness.SolidGeometryGroundProber();
            prober.FallbackHazardLocation = null;

            bool found = prober.TryFindSafeGround(12f, 34f, out float safeX, out float safeY);
            Assert.False(found);
            Assert.Equal(12f, safeX);
            Assert.Equal(34.5f, safeY);
        }

        #endregion

        #region F11: Revive prober dynamic buffer (UnityGroundProber.cs:186, R3)

        [Fact]
        public void F11_01_StandardQueryUnder16Colliders_SucceedsWithoutExpanding()
        {
            var prober = new RemediationTestHarness.SolidGeometryGroundProber();
            prober.Platforms.Add(new RemediationTestHarness.SolidGeometryGroundProber.Platform
            {
                MinX = -5f,
                MaxX = 5f,
                SurfaceY = 5f,
                NormalY = 1f
            });

            bool found = prober.TryFindSafeGround(0f, 5f, out _, out _);
            Assert.True(found);
            Assert.Equal(16, prober.BufferCapacity); // Default initial capacity
        }

        [Fact]
        public void F11_02_DenseAreaWith20Colliders_ExpandsBufferTo32()
        {
            var prober = new RemediationTestHarness.SolidGeometryGroundProber();
            prober.Platforms.Add(new RemediationTestHarness.SolidGeometryGroundProber.Platform
            {
                MinX = -5f,
                MaxX = 5f,
                SurfaceY = 5f,
                NormalY = 1f
            });

            // Add 18 non-hazard walls in lateral offset 0
            for (int i = 0; i < 18; i++)
            {
                prober.Walls.Add(new RemediationTestHarness.SolidGeometryGroundProber.WallObstacle
                {
                    MinX = -0.3f,
                    MaxX = 0.3f,
                    MinY = 4.5f,
                    MaxY = 6.0f
                });
            }

            prober.TryFindSafeGround(0f, 5f, out _, out _);
            Assert.True(prober.BufferCapacity >= 32);
        }

        [Fact]
        public void F11_03_HazardBeyondIndex16_DetectedByExpandedBuffer()
        {
            var prober = new RemediationTestHarness.SolidGeometryGroundProber();
            // Candidate offset 0 has 16 harmless walls and 1 hazard platform
            for (int i = 0; i < 16; i++)
            {
                prober.Walls.Add(new RemediationTestHarness.SolidGeometryGroundProber.WallObstacle
                {
                    MinX = -0.3f,
                    MaxX = 0.3f,
                    MinY = 5.0f,
                    MaxY = 5.5f
                });
            }

            prober.Platforms.Add(new RemediationTestHarness.SolidGeometryGroundProber.Platform
            {
                MinX = -0.5f,
                MaxX = 0.5f,
                SurfaceY = 4.0f,
                IsHazard = true // Lethal hazard beyond 16 harmless colliders
            });

            // Safe platform at lateral +1.2
            prober.Platforms.Add(new RemediationTestHarness.SolidGeometryGroundProber.Platform
            {
                MinX = 1.0f,
                MaxX = 3.0f,
                SurfaceY = 4.0f,
                IsHazard = false
            });

            bool found = prober.TryFindSafeGround(0f, 4f, out float safeX, out _);
            Assert.True(found);
            Assert.Equal(1.2f, safeX, 1); // Detected hazard at offset 0 and moved to +1.2
        }

        [Fact]
        public void F11_04_SaturatedBuffer_SafelyReportsUnsafe()
        {
            var prober = new RemediationTestHarness.SolidGeometryGroundProber
            {
                MaxBufferCapacity = 32 // Set low limit for testing saturation
            };

            // Ground platform under probe
            prober.Platforms.Add(new RemediationTestHarness.SolidGeometryGroundProber.Platform
            {
                MinX = -10f,
                MaxX = 10f,
                SurfaceY = 0f,
                NormalY = 1.0f
            });

            // Add 40 colliders exceeding max capacity
            for (int i = 0; i < 40; i++)
            {
                prober.Walls.Add(new RemediationTestHarness.SolidGeometryGroundProber.WallObstacle
                {
                    MinX = -5f,
                    MaxX = 5f,
                    MinY = 0f,
                    MaxY = 20f
                });
            }

            bool found = prober.TryFindSafeGround(0f, 5f, out _, out _);
            Assert.False(found);
            Assert.True(prober.LastQueryBufferSaturated);
        }

        [Fact]
        public void F11_05_MultipleQueries_PreserveExpandedBufferCapacity()
        {
            var prober = new RemediationTestHarness.SolidGeometryGroundProber();
            prober.Platforms.Add(new RemediationTestHarness.SolidGeometryGroundProber.Platform
            {
                MinX = -10f,
                MaxX = 10f,
                SurfaceY = 5f
            });

            for (int i = 0; i < 20; i++)
            {
                prober.Walls.Add(new RemediationTestHarness.SolidGeometryGroundProber.WallObstacle
                {
                    MinX = -0.2f,
                    MaxX = 0.2f,
                    MinY = 4f,
                    MaxY = 6f
                });
            }

            prober.TryFindSafeGround(0f, 5f, out _, out _);
            int capacityAfterFirst = prober.BufferCapacity;
            Assert.True(capacityAfterFirst >= 32);

            // Second query reuses capacity without shrinking
            prober.TryFindSafeGround(0f, 5f, out _, out _);
            Assert.Equal(capacityAfterFirst, prober.BufferCapacity);
        }

        #endregion

        #region F12: ReliableUnordered sequence wrap-around (LiteNetLibTransport.cs:1658, R3)

        [Fact]
        public void F12_01_InOrderSequences_Accepted()
        {
            var window = new RemediationTestHarness.SimulatedUnorderedReplayWindow();
            for (uint i = 0; i < 10; i++)
            {
                var status = window.Process(i);
                Assert.Equal(RemediationTestHarness.ReplayStatus.Accepted, status);
            }
            Assert.Equal(9u, window.Latest);
        }

        [Fact]
        public void F12_02_OutOfOrderSequencesWithinWindow_Accepted()
        {
            var window = new RemediationTestHarness.SimulatedUnorderedReplayWindow();
            Assert.Equal(RemediationTestHarness.ReplayStatus.Accepted, window.Process(10));
            Assert.Equal(RemediationTestHarness.ReplayStatus.Accepted, window.Process(5));
            Assert.Equal(RemediationTestHarness.ReplayStatus.Accepted, window.Process(8));
            Assert.Equal(RemediationTestHarness.ReplayStatus.Accepted, window.Process(12));
            Assert.Equal(12u, window.Latest);
        }

        [Fact]
        public void F12_03_DuplicateSequence_MarkedDuplicateForAck()
        {
            var window = new RemediationTestHarness.SimulatedUnorderedReplayWindow();
            Assert.Equal(RemediationTestHarness.ReplayStatus.Accepted, window.Process(100));
            Assert.Equal(RemediationTestHarness.ReplayStatus.Duplicate, window.Process(100));
        }

        [Fact]
        public void F12_04_LaggedSequencePast4096_MarkedStaleAndNotAcked()
        {
            var window = new RemediationTestHarness.SimulatedUnorderedReplayWindow();
            // Sequence advances to 4096
            for (uint i = 1; i <= 4096; i++)
            {
                window.Process(i);
            }
            Assert.Equal(4096u, window.Latest);

            // Delayed sequence 0 arrives (lag = 4096 >= Capacity)
            var status = window.Process(0);
            Assert.Equal(RemediationTestHarness.ReplayStatus.Stale, status);
        }

        [Fact]
        public void F12_05_SequenceWrapNearUintMax_HandlesTransitionCorrectly()
        {
            var window = new RemediationTestHarness.SimulatedUnorderedReplayWindow();
            window.Process(uint.MaxValue - 2);
            window.Process(uint.MaxValue - 1);
            window.Process(uint.MaxValue);

            // Wrap to 0
            var status = window.Process(0);
            Assert.Equal(RemediationTestHarness.ReplayStatus.Accepted, status);
            Assert.Equal(0u, window.Latest);
        }

        #endregion

        #region F13: All unlocked crests catch-up (SilksongCoopPlugin.cs:994, R3)

        [Fact]
        public void F13_01_SingleCrestCatchUp_RegistersSuccessfully()
        {
            var inventory = new InventorySyncService();
            inventory.OnLocalItemDiscovered("HunterCrest", ItemCategory.Crest, force: true);

            Assert.Contains("HunterCrest", inventory.UnlockedItems);
        }

        [Fact]
        public void F13_02_MultipleUnlockedCrests_AllRegisteredInInventory()
        {
            var inventory = new InventorySyncService();
            string[] crests = new[] { "HunterCrest", "ReaperCrest", "ArchitectCrest", "WandererCrest" };

            foreach (var crest in crests)
            {
                inventory.OnLocalItemDiscovered(crest, ItemCategory.Crest, force: true);
            }

            Assert.Equal(4, inventory.UnlockedItems.Count);
            foreach (var crest in crests)
            {
                Assert.Contains(crest, inventory.UnlockedItems);
            }
        }

        [Fact]
        public void F13_03_EquippedCrest_PreservedSeparatelyFromUnlockedCollection()
        {
            var inventory = new InventorySyncService();
            inventory.SetLocalEquippedCrest("ReaperCrest");
            inventory.OnLocalItemDiscovered("HunterCrest", ItemCategory.Crest, force: true);
            inventory.OnLocalItemDiscovered("ArchitectCrest", ItemCategory.Crest, force: true);

            Assert.Equal("ReaperCrest", inventory.LocalEquippedCrest);
            Assert.Contains("HunterCrest", inventory.UnlockedItems);
            Assert.Contains("ArchitectCrest", inventory.UnlockedItems);
        }

        [Fact]
        public void F13_04_DuplicateCrestBroadcast_DeduplicatedCleanly()
        {
            var inventory = new InventorySyncService();
            inventory.OnLocalItemDiscovered("HunterCrest", ItemCategory.Crest, force: true);
            inventory.OnLocalItemDiscovered("HunterCrest", ItemCategory.Crest, force: true);

            Assert.Single(inventory.UnlockedItems);
        }

        [Fact]
        public void F13_05_RemotePeerReceivesAllUnlockedCrestsViaHarness()
        {
            using var harness = new DualClientTestHarness();
            harness.HostInventory.OnLocalItemDiscovered("Crest1", ItemCategory.Crest, force: true);
            harness.HostInventory.OnLocalItemDiscovered("Crest2", ItemCategory.Crest, force: true);
            harness.DeliverAll();

            Assert.Contains("Crest1", harness.ClientInventory.UnlockedItems);
            Assert.Contains("Crest2", harness.ClientInventory.UnlockedItems);
        }

        #endregion

        #region F14: Bounded counter transaction memory (InventorySyncService.cs:25, R3)

        [Fact]
        public void F14_01_UnderCapacity_TracksAllTransactions()
        {
            var tracker = new RemediationTestHarness.BoundedCounterTransactionTracker();
            for (int i = 0; i < 100; i++)
            {
                bool accepted = tracker.TryProcessTransaction(Guid.NewGuid());
                Assert.True(accepted);
            }
            Assert.Equal(100, tracker.Count);
        }

        [Fact]
        public void F14_02_DuplicateTransactionWithinWindow_Rejected()
        {
            var tracker = new RemediationTestHarness.BoundedCounterTransactionTracker();
            var tx = Guid.NewGuid();

            Assert.True(tracker.TryProcessTransaction(tx));
            Assert.False(tracker.TryProcessTransaction(tx)); // Duplicate
            Assert.Equal(1, tracker.Count);
        }

        [Fact]
        public void F14_03_Exceeding500Capacity_EvictsOldestTransactions()
        {
            var tracker = new RemediationTestHarness.BoundedCounterTransactionTracker();
            var firstTx = Guid.NewGuid();
            tracker.TryProcessTransaction(firstTx);

            for (int i = 0; i < 500; i++)
            {
                tracker.TryProcessTransaction(Guid.NewGuid());
            }

            Assert.Equal(500, tracker.Count);
            // Oldest transaction should have been evicted
            Assert.False(tracker.Contains(firstTx));
        }

        [Fact]
        public void F14_04_MostRecent500Transactions_RetainedInTracker()
        {
            var tracker = new RemediationTestHarness.BoundedCounterTransactionTracker();
            var recentGuids = new List<Guid>();

            // Push 100 dummy GUIDs
            for (int i = 0; i < 100; i++) tracker.TryProcessTransaction(Guid.NewGuid());

            // Push 500 recent GUIDs
            for (int i = 0; i < 500; i++)
            {
                var g = Guid.NewGuid();
                recentGuids.Add(g);
                tracker.TryProcessTransaction(g);
            }

            Assert.Equal(500, tracker.Count);
            foreach (var g in recentGuids)
            {
                Assert.True(tracker.Contains(g));
            }
        }

        [Fact]
        public void F14_05_Reset_ClearsAllTrackedCounterTransactions()
        {
            var tracker = new RemediationTestHarness.BoundedCounterTransactionTracker();
            for (int i = 0; i < 50; i++) tracker.TryProcessTransaction(Guid.NewGuid());
            Assert.Equal(50, tracker.Count);

            tracker.Reset();
            Assert.Equal(0, tracker.Count);
        }

        #endregion

        #region F15: Steam heartbeat queue drain (SteamP2PTransport.cs:1060, R3)

        [Fact]
        public void F15_01_DrainFirst_QueuedPongRefreshesLivenessAndPreventsTimeout()
        {
            var transport = new RemediationTestHarness.SimulatedHeartbeatTransport();
            long now = 20000;
            transport.LastPongReceivedMs = 2000; // 18000 ms elapsed (> 15000 ms timeout)

            // Valid PONG is in the incoming queue
            transport.IncomingQueue.Enqueue("PONG");

            // Poll with drainFirst = true
            transport.PollEvents(now, drainFirst: true);

            Assert.True(transport.IsConnected);
            Assert.Equal(now, transport.LastPongReceivedMs);
        }

        [Fact]
        public void F15_02_TimeoutFirst_PrematurelyDisconnectsWithoutReadingQueuedPong()
        {
            var transport = new RemediationTestHarness.SimulatedHeartbeatTransport();
            long now = 20000;
            transport.LastPongReceivedMs = 2000;

            transport.IncomingQueue.Enqueue("PONG");

            // Old buggy behavior: timeout check first
            transport.PollEvents(now, drainFirst: false);

            Assert.False(transport.IsConnected); // Disconnected prematurely
        }

        [Fact]
        public void F15_03_NormalHeartbeatTimeout_OccursWhenQueueIsEmptyAndTimeoutElapsed()
        {
            var transport = new RemediationTestHarness.SimulatedHeartbeatTransport();
            long now = 20000;
            transport.LastPongReceivedMs = 2000;

            // Empty queue
            transport.PollEvents(now, drainFirst: true);

            Assert.False(transport.IsConnected);
        }

        [Fact]
        public void F15_04_ActiveConnectionWithinTimeout_RemainsConnected()
        {
            var transport = new RemediationTestHarness.SimulatedHeartbeatTransport();
            long now = 10000;
            transport.LastPongReceivedMs = 8000; // Only 2000 ms elapsed

            transport.PollEvents(now, drainFirst: true);
            Assert.True(transport.IsConnected);
        }

        [Fact]
        public void F15_05_MultipleQueuedPackets_ProcessedInOrderDuringDrain()
        {
            var transport = new RemediationTestHarness.SimulatedHeartbeatTransport();
            transport.IncomingQueue.Enqueue("DATA_1");
            transport.IncomingQueue.Enqueue("DATA_2");
            transport.IncomingQueue.Enqueue("PONG");

            transport.PollEvents(10000, drainFirst: true);

            Assert.Empty(transport.IncomingQueue);
            Assert.Equal(10000, transport.LastPongReceivedMs);
            Assert.True(transport.IsConnected);
        }

        #endregion

        #region F16: Comprehensive automated test coverage (SilksongCoop.Tests.csproj, R3)

        [Fact]
        public void F16_01_PacketHeaderSerialization_RoundTripIntegrity()
        {
            var header = new PacketHeader(PacketType.VitalsSync, 2, 42, 123456789);
            Span<byte> buffer = stackalloc byte[PacketHeader.HeaderSize];
            header.WriteTo(buffer);

            bool success = PacketHeader.TryReadFrom(buffer, out var deserialized);
            Assert.True(success);
            Assert.Equal(PacketType.VitalsSync, deserialized.PacketId);
            Assert.Equal(123456789u, deserialized.Timestamp);
            Assert.Equal(42u, deserialized.TickSequence);
        }

        [Fact]
        public void F16_02_Base32RoomCodeCodec_MatchesIpAndPort()
        {
            var ip = IPAddress.Parse("10.0.0.1");
            ushort port = 7777;
            string code = RoomCodeCodec.Encode(ip, port);

            bool decoded = RoomCodeCodec.TryDecode(code, out var decIp, out int decPort);
            Assert.True(decoded);
            Assert.Equal(ip, decIp);
            Assert.Equal(port, decPort);
        }

        [Fact]
        public void F16_03_NetworkPeerId_EqualityAndHashCodeInvariants()
        {
            var peer1 = new NetworkPeerId(999);
            var peer2 = new NetworkPeerId(999);
            var peer3 = new NetworkPeerId(1000);

            Assert.Equal(peer1, peer2);
            Assert.NotEqual(peer1, peer3);
            Assert.Equal(peer1.GetHashCode(), peer2.GetHashCode());
        }

        [Fact]
        public void F16_04_SafeGroundSolver_FormulaCorrectness()
        {
            Assert.Equal(1, SafeGroundSolver.CalculateReviveHealth(0));
            Assert.Equal(1, SafeGroundSolver.CalculateReviveHealth(2));
            Assert.Equal(3, SafeGroundSolver.CalculateReviveHealth(6));
            Assert.Equal(4, SafeGroundSolver.CalculateReviveHealth(8));
        }

        [Fact]
        public void F16_05_DualClientHarness_TransmitsAndDeliversPacketsSuccessfully()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.OnLocalHealthGained(1, 4);
            harness.DeliverAll();

            Assert.Single(harness.DeliveredVitalsToClient);
            Assert.Equal(4, harness.DeliveredVitalsToClient[0].CurrentHealth);
            Assert.Equal(5, harness.DeliveredVitalsToClient[0].MaxHealth);
        }

        #endregion

        #region F17: Dynamic tick rate active update (NetworkOrchestrator.cs:62, R3)

        [Fact]
        public void F17_01_SetDefaultTickHz_UpdatesTargetAndIntervalImmediately()
        {
            var rate = new AdaptiveTickRate(30f);
            Assert.Equal(30f, rate.DefaultTickHz);

            RemediationTestHarness.SetDefaultTickHzSafe(rate, 60f);
            Assert.Equal(60f, rate.DefaultTickHz);
            rate.Reset();
            Assert.Equal(60f, rate.CurrentTickHz);
            Assert.Equal(1f / 60f, rate.CurrentInterval, 4);
        }

        [Fact]
        public void F17_02_SetDefaultTickHz_ClampsToMin15Hz()
        {
            var rate = new AdaptiveTickRate(30f);
            RemediationTestHarness.SetDefaultTickHzSafe(rate, 5f);
            Assert.Equal(15f, rate.DefaultTickHz);
            rate.Reset();
            Assert.Equal(15f, rate.CurrentTickHz);
            Assert.Equal(1f / 15f, rate.CurrentInterval, 4);
        }

        [Fact]
        public void F17_03_SetDefaultTickHz_ClampsToMax60Hz()
        {
            var rate = new AdaptiveTickRate(30f);
            RemediationTestHarness.SetDefaultTickHzSafe(rate, 120f);
            Assert.Equal(60f, rate.DefaultTickHz);
            rate.Reset();
            Assert.Equal(60f, rate.CurrentTickHz);
            Assert.Equal(1f / 60f, rate.CurrentInterval, 4);
        }

        [Fact]
        public void F17_04_SetDefaultTickHz_InvalidInputsFallbackToDefault()
        {
            var rate = new AdaptiveTickRate(30f);

            RemediationTestHarness.SetDefaultTickHzSafe(rate, float.NaN);
            Assert.True(rate.DefaultTickHz >= 15f && rate.DefaultTickHz <= 60f);

            RemediationTestHarness.SetDefaultTickHzSafe(rate, -10f);
            Assert.True(rate.DefaultTickHz >= 15f && rate.DefaultTickHz <= 60f);
        }

        [Fact]
        public void F17_05_AdaptiveTickRate_AdjustsUnderHighRtt()
        {
            var rate = new AdaptiveTickRate(60f);
            RemediationTestHarness.SetDefaultTickHzSafe(rate, 60f);
            rate.Reset();

            // Update with high RTT (300ms) across multiple ticks to adapt smoothly down
            for (float t = 1.0f; t <= 15.0f; t += 0.6f)
            {
                rate.Update(currentTime: t, rttMs: 300f, lossPercent: 0f);
            }
            Assert.True(rate.CurrentTickHz <= 20f);
        }

        #endregion

        #region F18: Dash trail light fade shutoff (DashTrailController.cs:117, R3)

        [Fact]
        public void F18_01_DecayFromFullIntensity_ReachesZeroAndDeactivates()
        {
            var result = RemediationTestHarness.SimulateRimLightDecay(
                initialIntensity: 1.0f,
                deltaTime: 0.05f,
                useFixedLogic: true);

            Assert.Equal(0f, result.FinalIntensity);
            Assert.False(result.IsActive);
        }

        [Fact]
        public void F18_02_DecayFromTrapZoneValue_ReachesZeroUnderFixedLogic()
        {
            // Initial intensity 0.08f is in trap zone (0.05f, 0.10f]
            var resultFixed = RemediationTestHarness.SimulateRimLightDecay(
                initialIntensity: 0.08f,
                deltaTime: 0.05f,
                useFixedLogic: true);

            Assert.Equal(0f, resultFixed.FinalIntensity);
            Assert.False(resultFixed.IsActive);
        }

        [Fact]
        public void F18_03_BuggyLogicTrapsAt008Intensity()
        {
            // Verify bug reproduction: old logic skips decay when <= 0.10f
            var resultBuggy = RemediationTestHarness.SimulateRimLightDecay(
                initialIntensity: 0.08f,
                deltaTime: 0.05f,
                useFixedLogic: false);

            Assert.Equal(0.08f, resultBuggy.FinalIntensity);
            Assert.True(resultBuggy.IsActive); // Trapped! Never turns off!
        }

        [Fact]
        public void F18_04_ImmediateLowIntensity_ClampsDirectlyToZero()
        {
            var result = RemediationTestHarness.SimulateRimLightDecay(
                initialIntensity: 0.04f, // Already below 0.05 threshold
                deltaTime: 0.016f,
                useFixedLogic: true);

            Assert.Equal(0f, result.FinalIntensity);
            Assert.False(result.IsActive);
        }

        [Fact]
        public void F18_05_DashTrailLifecycle_PhaseTransitionsToIdle()
        {
            var lifecycle = new DashTrailLifecycle();
            lifecycle.TriggerDash(isAirDash: false, isPlayer2: false);
            Assert.Equal(DashTrailPhase.Active, lifecycle.CurrentPhase);

            lifecycle.Update(0.26f, isGrounded: true, isWallSliding: false);
            Assert.Equal(DashTrailPhase.Cooldown, lifecycle.CurrentPhase);

            lifecycle.Update(0.01f, isGrounded: true, isWallSliding: false);
            Assert.Equal(DashTrailPhase.Idle, lifecycle.CurrentPhase);
            Assert.True(lifecycle.CanTriggerDash);
        }

        #endregion
    }
}
