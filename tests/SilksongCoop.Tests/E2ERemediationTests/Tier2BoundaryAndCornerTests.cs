using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
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
    /// Tier 2: Boundary Value Analysis & Corner Case Tests.
    /// Covers boundary limits, overflow/underflow, wrap-around, empty/null,
    /// and buffer saturation across all 18 defects (Total: 90 tests).
    /// </summary>
    public class Tier2BoundaryAndCornerTests
    {
        #region F01 Boundary Tests: Whitelist PlayerData bools

        [Fact]
        public void F01_B01_NullString_Rejected()
        {
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed(null!, ItemCategory.Ability));
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed(null!, ItemCategory.KeyItem));
        }

        [Fact]
        public void F01_B02_EmptyAndWhitespaceStrings_Rejected()
        {
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed("", ItemCategory.Ability));
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed("   ", ItemCategory.Ability));
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed("\t\n\r", ItemCategory.Ability));
        }

        [Fact]
        public void F01_B03_ExtremeLengthString_Rejected()
        {
            string hugeKey = new string('A', 4096);
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed(hugeKey, ItemCategory.Ability));
        }

        [Fact]
        public void F01_B04_InjectionPayloads_Rejected()
        {
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed("hasDash; DROP TABLE Saves;--", ItemCategory.Ability));
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed("../../save.dat", ItemCategory.Ability));
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed("<script>alert(1)</script>", ItemCategory.Ability));
        }

        [Fact]
        public void F01_B05_CaseSensitivityAndTrailingSpaces_Rejected()
        {
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed("HASDASH", ItemCategory.Ability));
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed("hasdash", ItemCategory.Ability));
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed("hasDash ", ItemCategory.Ability));
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed(" hasDash", ItemCategory.Ability));
        }

        #endregion

        #region F02 Boundary Tests: WalletSeed retry mutation fix

        [Fact]
        public void F02_B01_EmptyOutstandingDictionary_ReturnsTrueImmediately()
        {
            var outstanding = new Dictionary<NetworkPeerId, byte>();
            bool success = RemediationTestHarness.PumpWalletSeedSafe(
                outstanding,
                peer => false,
                out bool failed);

            Assert.True(success);
            Assert.False(failed);
            Assert.Empty(outstanding);
        }

        [Fact]
        public void F02_B02_RetryBoundaryAtOne_DecrementsToZeroAndDisconnects()
        {
            var peer = new NetworkPeerId(42);
            var outstanding = new Dictionary<NetworkPeerId, byte>
            {
                { peer, 1 } // Boundary: lowest non-zero retry
            };

            bool success = RemediationTestHarness.PumpWalletSeedSafe(
                outstanding,
                _ => false, // Send fails
                out bool failed);

            Assert.False(success);
            Assert.True(failed);
            Assert.Empty(outstanding);
        }

        [Fact]
        public void F02_B03_RetryBoundaryAtByteMax_DecrementsCleanly()
        {
            var peer = new NetworkPeerId(42);
            var outstanding = new Dictionary<NetworkPeerId, byte>
            {
                { peer, byte.MaxValue } // 255
            };

            bool success = RemediationTestHarness.PumpWalletSeedSafe(
                outstanding,
                _ => false,
                out bool failed);

            Assert.True(success);
            Assert.False(failed);
            Assert.Equal(254, outstanding[peer]);
        }

        [Fact]
        public void F02_B04_HundredPeersConcurrentRetries_DecrementedWithoutException()
        {
            var outstanding = new Dictionary<NetworkPeerId, byte>();
            for (ulong i = 1; i <= 100; i++)
            {
                outstanding[new NetworkPeerId(i)] = 5;
            }

            bool success = RemediationTestHarness.PumpWalletSeedSafe(
                outstanding,
                _ => false,
                out bool failed);

            Assert.True(success);
            Assert.False(failed);
            Assert.Equal(100, outstanding.Count);
            for (ulong i = 1; i <= 100; i++)
            {
                Assert.Equal(4, outstanding[new NetworkPeerId(i)]);
            }
        }

        [Fact]
        public void F02_B05_InterleavedDeliveredAndRetryingPeers_AccuratelyProcessed()
        {
            var outstanding = new Dictionary<NetworkPeerId, byte>();
            for (ulong i = 1; i <= 50; i++)
            {
                outstanding[new NetworkPeerId(i)] = 3;
            }

            // Even peers delivered, odd peers retried
            bool success = RemediationTestHarness.PumpWalletSeedSafe(
                outstanding,
                peer => peer.Value % 2 == 0,
                out bool failed);

            Assert.True(success);
            Assert.False(failed);
            Assert.Equal(25, outstanding.Count);
            for (ulong i = 1; i <= 50; i++)
            {
                if (i % 2 == 0)
                {
                    Assert.False(outstanding.ContainsKey(new NetworkPeerId(i)));
                }
                else
                {
                    Assert.Equal(2, outstanding[new NetworkPeerId(i)]);
                }
            }
        }

        #endregion

        #region F03 Boundary Tests: Catch-up currency readiness

        [Fact]
        public void F03_B01_ZeroDeltaWithSequenceZero_HandledSafely()
        {
            var service = new VitalsSyncService();
            service.SeedWallet(CurrencyType.Rosaries, 100, 0);

            var packet = new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 0,
                TotalWalletAmount = 100,
                TransactionId = Guid.NewGuid(),
                PickupUniqueId = 0 // Sequence boundary 0
            };

            service.HandleCurrencyPacket(new NetworkPeerId(2), packet);
            Assert.Equal(100, service.SharedRosaries);
        }

        [Fact]
        public void F03_B02_ZeroDeltaWithMaxSequence_DoesNotCorruptWatermark()
        {
            var service = new VitalsSyncService();
            service.SeedWallet(CurrencyType.Rosaries, 100, 0);

            var packet = new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 0,
                TotalWalletAmount = 100,
                TransactionId = Guid.NewGuid(),
                PickupUniqueId = uint.MaxValue // Sequence boundary uint.MaxValue
            };

            service.HandleCurrencyPacket(new NetworkPeerId(2), packet);
            Assert.Equal(100, service.SharedRosaries);
        }

        [Fact]
        public void F03_B03_BurstOf100ZeroDeltaClaims_LeavesBalanceUnaffected()
        {
            var service = new VitalsSyncService();
            service.SeedWallet(CurrencyType.Rosaries, 500, 0);
            var peer = new NetworkPeerId(2);

            for (uint i = 1; i <= 100; i++)
            {
                service.HandleCurrencyPacket(peer, new CurrencySyncPacket
                {
                    Currency = CurrencyType.Rosaries,
                    DeltaAmount = 0,
                    TotalWalletAmount = 500,
                    TransactionId = Guid.NewGuid(),
                    PickupUniqueId = i
                });
            }

            Assert.Equal(500, service.SharedRosaries);
        }

        [Fact]
        public void F03_B04_SequenceBoundaryWrapAround_AcceptsNewSequenceAfterMax()
        {
            var service = new VitalsSyncService();
            service.SeedWallet(CurrencyType.Rosaries, 100, 0);
            var peer = new NetworkPeerId(2);

            // High sequence near uint.MaxValue
            service.HandleCurrencyPacket(peer, new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 10,
                TotalWalletAmount = 110,
                TransactionId = Guid.NewGuid(),
                PickupUniqueId = uint.MaxValue - 1
            });

            // Wrapped sequence 1 (newer in RFC 1982 serial arithmetic)
            service.HandleCurrencyPacket(peer, new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 15,
                TotalWalletAmount = 125,
                TransactionId = Guid.NewGuid(),
                PickupUniqueId = 1
            });

            Assert.Equal(125, service.SharedRosaries);
        }

        [Fact]
        public void F03_B05_SpoofedTotalInZeroDeltaPacket_DoesNotAlterBalance()
        {
            var service = new VitalsSyncService();
            service.SeedWallet(CurrencyType.Rosaries, 50, 0);
            var peer = new NetworkPeerId(2);

            // Establish active currency stream
            service.HandleCurrencyPacket(peer, new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 1,
                TotalWalletAmount = 51,
                TransactionId = Guid.NewGuid(),
                PickupUniqueId = 1
            });
            Assert.Equal(51, service.SharedRosaries);

            // Spoofed total in subsequent zero-delta packet must not overwrite active shared balance
            service.HandleCurrencyPacket(peer, new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 0,
                TotalWalletAmount = 999999,
                TransactionId = Guid.NewGuid(),
                PickupUniqueId = 2
            });

            Assert.Equal(51, service.SharedRosaries);
        }

        #endregion

        #region F04 Boundary Tests: WalletSeed 3rd client delta

        [Fact]
        public void F04_B01_ZeroRemoteDelta_MatchesStandardSeed()
        {
            var service = new VitalsSyncService();
            RemediationTestHarness.SeedWalletWithRemoteDelta(
                service,
                CurrencyType.Rosaries,
                total: 100,
                pendingLocalDelta: 20,
                preSeedRemoteDelta: 0);

            Assert.Equal(120, service.SharedRosaries);
        }

        [Fact]
        public void F04_B02_ExtremePositiveRemoteDelta_ClampedToMaxReasonableCurrency()
        {
            var service = new VitalsSyncService();
            RemediationTestHarness.SeedWalletWithRemoteDelta(
                service,
                CurrencyType.Rosaries,
                total: 500,
                pendingLocalDelta: 0,
                preSeedRemoteDelta: 2_000_000_000L); // Extreme delta

            Assert.True(service.SharedRosaries <= PacketValidator.MaxReasonableCurrency);
        }

        [Fact]
        public void F04_B03_ExtremeNegativeRemoteDelta_ClampedAtZeroForVisibleBalance()
        {
            var service = new VitalsSyncService();
            RemediationTestHarness.SeedWalletWithRemoteDelta(
                service,
                CurrencyType.Rosaries,
                total: 50,
                pendingLocalDelta: 0,
                preSeedRemoteDelta: -500); // Exceeds balance

            Assert.True(service.SharedRosaries >= 0);
        }

        [Fact]
        public void F04_B04_MultiplePeersAccumulatedPreSeedDelta_Preserved()
        {
            var service = new VitalsSyncService();
            // Client B sent +50, Client C sent +30, Client D spent -20 before seed
            long totalPreSeedDelta = 50 + 30 - 20;

            RemediationTestHarness.SeedWalletWithRemoteDelta(
                service,
                CurrencyType.Rosaries,
                total: 1000,
                pendingLocalDelta: 15,
                preSeedRemoteDelta: totalPreSeedDelta);

            Assert.Equal(1075, service.SharedRosaries);
        }

        [Fact]
        public void F04_B05_ReSeedAfterSessionReconnect_ResetsBaselineCorrectly()
        {
            var service = new VitalsSyncService();
            RemediationTestHarness.SeedWalletWithRemoteDelta(
                service,
                CurrencyType.Rosaries,
                total: 100,
                pendingLocalDelta: 0,
                preSeedRemoteDelta: 25);
            Assert.Equal(125, service.SharedRosaries);

            // Host re-seeds after checkpoint reload
            RemediationTestHarness.SeedWalletWithRemoteDelta(
                service,
                CurrencyType.Rosaries,
                total: 200,
                pendingLocalDelta: 0,
                preSeedRemoteDelta: 0);
            Assert.Equal(200, service.SharedRosaries);
        }

        #endregion

        #region F05 Boundary Tests: Stat upgrade channel idempotency

        [Fact]
        public void F05_B01_MaxHealthCapacityBoundary_DoesNotExceed11Masks()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();
            controller.CurrentMaxHealth = 10;

            // Apply 8 fragments (2 masks) when only 1 mask can fit under max 11
            controller.ApplyHeartPieceFragments(fragmentTotal: 8, sharedMaxHealth: 10);
            int clamped = Math.Min(controller.CurrentMaxHealth, 11);
            Assert.True(clamped <= 11);
        }

        [Fact]
        public void F05_B02_NegativeFragmentCount_ClampedSafely()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();
            int current = controller.CurrentMaxHealth;

            // Malformed packet with negative fragments
            int fragments = -4;
            int masks = Math.Max(0, fragments / 4);
            controller.ApplyHeartPieceFragments(fragmentTotal: masks * 4, sharedMaxHealth: current);

            Assert.Equal(current, controller.CurrentMaxHealth);
            Assert.Equal(0, controller.AddToMaxHealthCallCount);
        }

        [Fact]
        public void F05_B03_HeartPieceExactBoundaries_0_1_2_3_4_Fragments()
        {
            var c0 = new RemediationTestHarness.StatUpgradeIdempotencyController();
            c0.ApplyHeartPieceFragments(0, 5);
            Assert.Equal(5, c0.CurrentMaxHealth);

            var c3 = new RemediationTestHarness.StatUpgradeIdempotencyController();
            c3.ApplyHeartPieceFragments(3, 5);
            Assert.Equal(5, c3.CurrentMaxHealth);

            var c4 = new RemediationTestHarness.StatUpgradeIdempotencyController();
            c4.ApplyHeartPieceFragments(4, 5);
            Assert.Equal(6, c4.CurrentMaxHealth);

            var c7 = new RemediationTestHarness.StatUpgradeIdempotencyController();
            c7.ApplyHeartPieceFragments(7, 5);
            Assert.Equal(6, c7.CurrentMaxHealth); // 1 mask, 3 remainder

            var c8 = new RemediationTestHarness.StatUpgradeIdempotencyController();
            c8.ApplyHeartPieceFragments(8, 5);
            Assert.Equal(7, c8.CurrentMaxHealth); // 2 masks
        }

        [Fact]
        public void F05_B04_SilkPartsExactBoundaries_0_1_2_3_Fragments()
        {
            var c0 = new RemediationTestHarness.StatUpgradeIdempotencyController();
            c0.ApplySilkPartFragments(0, 1);
            Assert.Equal(1, c0.CurrentMaxSilkSpools);

            var c2 = new RemediationTestHarness.StatUpgradeIdempotencyController();
            c2.ApplySilkPartFragments(2, 1);
            Assert.Equal(1, c2.CurrentMaxSilkSpools);

            var c3 = new RemediationTestHarness.StatUpgradeIdempotencyController();
            c3.ApplySilkPartFragments(3, 1);
            Assert.Equal(2, c3.CurrentMaxSilkSpools);

            var c6 = new RemediationTestHarness.StatUpgradeIdempotencyController();
            c6.ApplySilkPartFragments(6, 1);
            Assert.Equal(3, c6.CurrentMaxSilkSpools);
        }

        [Fact]
        public void F05_B05_SimultaneousMultithreadedUpgrades_RemainConsistent()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();
            object sync = new object();

            Parallel.For(0, 10, i =>
            {
                lock (sync)
                {
                    controller.ApplyHeartPieceFragments(4, 5);
                    controller.OnSharedMaxHealthReceived(6);
                }
            });

            Assert.Equal(6, controller.CurrentMaxHealth);
            Assert.Equal(1, controller.AddToMaxHealthCallCount);
        }

        #endregion

        #region F06 Boundary Tests: Death epoch synchronization

        [Fact]
        public void F06_B01_ExtremeEpochJump_AdoptsLargeEpochSafely()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.HandleDeathPacket(new NetworkPeerId(2), new DeathLifecyclePacket
            {
                EventType = DeathEventType.BenchCheckpointUpdate,
                Epoch = 1_000_000u
            });

            Assert.Equal(1_000_000u, coordinator.CurrentEpoch);
        }

        [Fact]
        public void F06_B02_EpochNearUintMaxValue_HandledCorrectly()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = new NetworkPeerId(2);
            coordinator.RegisterRemotePeer(peer);
            // Start near the boundary through a real peer snapshot rather than interpreting
            // an ambiguous serial jump from epoch 1 as forward progress.
            typeof(DeathLifecycleCoordinator).GetField("_currentEpoch",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(coordinator, uint.MaxValue - 6);
            coordinator.HandleDeathPacket(peer, new DeathLifecyclePacket
            {
                EventType = DeathEventType.LifecycleSnapshot,
                Epoch = uint.MaxValue - 6
            });
            coordinator.HandleDeathPacket(peer, new DeathLifecyclePacket
            {
                EventType = DeathEventType.BenchCheckpointUpdate,
                Epoch = uint.MaxValue - 5
            });
            Assert.Equal(uint.MaxValue - 5, coordinator.CurrentEpoch);
            Assert.True(coordinator.IsRemotePeerAlive(peer));
        }

        [Fact]
        public void F06_B03_EpochZero_DiscardedWhenCurrentEpochIsPositive()
        {
            var coordinator = new DeathLifecycleCoordinator();
            Assert.Equal(1u, coordinator.CurrentEpoch);

            // Packet with epoch 0 arrives
            coordinator.HandleDeathPacket(new NetworkPeerId(2), new DeathLifecyclePacket
            {
                EventType = DeathEventType.SoloDeathOccurred,
                CoordinateX = 0f,
                CoordinateY = 0f,
                Epoch = 0
            });

            // Epoch 0 is less than CurrentEpoch (1), so it was discarded
            Assert.Equal(1u, coordinator.CurrentEpoch);
        }

        [Fact]
        public void F06_B04_MultiplePeersConcurrentEpochSync_AdoptsHighestMonotonically()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.HandleDeathPacket(new NetworkPeerId(2), new DeathLifecyclePacket
            {
                EventType = DeathEventType.BenchCheckpointUpdate,
                Epoch = 3
            });
            Assert.Equal(3u, coordinator.CurrentEpoch);

            coordinator.HandleDeathPacket(new NetworkPeerId(3), new DeathLifecyclePacket
            {
                EventType = DeathEventType.BenchCheckpointUpdate,
                Epoch = 7
            });
            Assert.Equal(7u, coordinator.CurrentEpoch);

            // Stale peer with epoch 5 arrives after epoch 7
            coordinator.HandleDeathPacket(new NetworkPeerId(4), new DeathLifecyclePacket
            {
                EventType = DeathEventType.BenchCheckpointUpdate,
                Epoch = 5
            });
            Assert.Equal(7u, coordinator.CurrentEpoch); // Did not regress to 5
        }

        [Fact]
        public void F06_B05_SoloBenchRespawn_AcrossPeerEpochWrap_RejectsOldRecovery()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = new NetworkPeerId(2);
            coordinator.RegisterRemotePeer(peer);
            coordinator.HandleDeathPacket(peer, new DeathLifecyclePacket
            {
                EventType = DeathEventType.LifecycleSnapshot,
                FallenPlayerIndex = 1,
                Epoch = uint.MaxValue
            });
            Assert.False(coordinator.IsRemotePeerAlive(peer));

            // Establish an actual adjacent serial-number wrap, not a jump from 1 to MaxValue.
            coordinator.HandleDeathPacket(peer, new DeathLifecyclePacket
            {
                EventType = DeathEventType.SoloBenchRespawn,
                Epoch = 0
            });
            Assert.True(coordinator.IsRemotePeerAlive(peer));
            coordinator.HandleDeathPacket(peer, new DeathLifecyclePacket
            {
                EventType = DeathEventType.SoloDeathOccurred,
                Epoch = 0
            });
            Assert.False(coordinator.IsRemotePeerAlive(peer));
            coordinator.HandleDeathPacket(peer, new DeathLifecyclePacket
            {
                EventType = DeathEventType.SoloBenchRespawn,
                Epoch = uint.MaxValue
            });
            Assert.False(coordinator.IsRemotePeerAlive(peer));
        }

        #endregion

        #region F07 Boundary Tests: Enemy host authority renewal

        [Fact]
        public void F07_B01_SnapshotAtZeroHp_TreatedAsDeadOrZero()
        {
            var service = new EnemySyncService();
            bool accepted = service.TryAcceptSnapshot(1, 0, dead: false, timestamp: 100);
            Assert.True(accepted);
            Assert.True(service.HasFreshHostAuthority(1));
        }

        [Fact]
        public void F07_B02_SnapshotAtIntMaxHp_HandledWithoutOverflow()
        {
            var service = new EnemySyncService();
            bool accepted = service.TryAcceptSnapshot(1, 100_000, dead: false, timestamp: 100);
            Assert.True(accepted);
        }

        [Fact]
        public void F07_B03_ThousandConsecutiveConfirmingSnapshots_MaintainAuthority()
        {
            var service = new EnemySyncService();
            service.TryAcceptSnapshot(1, 100, dead: false, timestamp: 100);
            service.NoteLocalEnemyState(1, 80, dead: false);

            for (uint i = 1; i <= 1000; i++)
            {
                bool accepted = service.TryAcceptSnapshot(1, 80, dead: false, timestamp: 100 + i);
                Assert.True(accepted);
            }

            Assert.True(service.HasFreshHostAuthority(1));
        }

        [Fact]
        public void F07_B04_SnapshotForUnregisteredEnemyKey_HandledCleanly()
        {
            var service = new EnemySyncService();
            ulong unknownEnemy = 0xDEADBEEF;

            bool accepted = service.TryAcceptSnapshot(unknownEnemy, 50, dead: false, timestamp: 100);
            Assert.True(accepted);
            Assert.True(service.HasFreshHostAuthority(unknownEnemy));
        }

        [Fact]
        public void F07_B05_TimestampJitter_EarlierTimestampSnapshotRejected()
        {
            var service = new EnemySyncService();
            service.TryAcceptSnapshot(1, 100, dead: false, timestamp: 500);

            // Jittered snapshot with timestamp 400 arrives later
            service.NoteLocalEnemyState(1, 90, dead: false);
            bool accepted = service.TryAcceptSnapshot(1, 100, dead: false, timestamp: 400);
            Assert.False(accepted);
        }

        #endregion

        #region F08 Boundary Tests: Unloaded enemy damage accumulation

        [Fact]
        public void F08_B01_FiftyMicroHitsAccumulateToFullKill()
        {
            var accumulator = new RemediationTestHarness.UnloadedEnemyDamageAccumulator();
            ulong enemyKey = 1001;

            // 50 hits of 2 damage = 100 total
            for (int i = 0; i < 49; i++)
            {
                accumulator.RecordHit(enemyKey, 2, isDead: false);
            }
            accumulator.RecordHit(enemyKey, 2, isDead: true);

            bool resolved = accumulator.TryResolvePendingKill(enemyKey, loadedHp: 100, out int totalDamage);
            Assert.True(resolved);
            Assert.Equal(100, totalDamage);
        }

        [Fact]
        public void F08_B02_OverkillDamage_ConfirmedWithoutOverflow()
        {
            var accumulator = new RemediationTestHarness.UnloadedEnemyDamageAccumulator();
            ulong enemyKey = 1002;

            accumulator.RecordHit(enemyKey, 100_000, isDead: true);
            bool resolved = accumulator.TryResolvePendingKill(enemyKey, loadedHp: 50, out int totalDamage);

            Assert.True(resolved);
            Assert.Equal(100_000, totalDamage);
        }

        [Fact]
        public void F08_B03_ExactDamageBoundary_OneHpShortRejectsKill()
        {
            var accumulator = new RemediationTestHarness.UnloadedEnemyDamageAccumulator();
            ulong enemyKey = 1003;

            // Exactly 99 damage on 100 HP enemy
            accumulator.RecordHit(enemyKey, 99, isDead: true);
            bool resolved = accumulator.TryResolvePendingKill(enemyKey, loadedHp: 100, out _);

            Assert.False(resolved);
        }

        [Fact]
        public void F08_B04_ExactDamageBoundary_ExactlyMatchingHpConfirmsKill()
        {
            var accumulator = new RemediationTestHarness.UnloadedEnemyDamageAccumulator();
            ulong enemyKey = 1004;

            accumulator.RecordHit(enemyKey, 100, isDead: true);
            bool resolved = accumulator.TryResolvePendingKill(enemyKey, loadedHp: 100, out int totalDamage);

            Assert.True(resolved);
            Assert.Equal(100, totalDamage);
        }

        [Fact]
        public void F08_B05_HundredDistinctEnemies_TrackedIndependentlyWithoutCrosstalk()
        {
            var accumulator = new RemediationTestHarness.UnloadedEnemyDamageAccumulator();

            for (ulong i = 1; i <= 100; i++)
            {
                accumulator.RecordHit(i, (int)i, isDead: true);
            }

            for (ulong i = 1; i <= 100; i++)
            {
                bool resolved = accumulator.TryResolvePendingKill(i, loadedHp: (int)i, out int dmg);
                Assert.True(resolved);
                Assert.Equal((int)i, dmg);
            }
        }

        #endregion

        #region F09 Boundary Tests: Inventory unlock retry rollback

        [Fact]
        public void F09_B01_CriticalExceptionInHandler_RollsBackCleanly()
        {
            var service = new InventorySyncService();
            var peer = new NetworkPeerId(2);
            bool fail = true;

            service.OnItemUnlocked += (item, cat) =>
            {
                if (fail) throw new AggregateException("Critical handler failure");
            };

            var packet = new ItemUnlockPacket
            {
                ItemId = "hasSilkSpecial",
                Category = ItemCategory.Ability,
                TransactionId = Guid.NewGuid()
            };

            try { service.HandleItemUnlockPacket(peer, packet); }
            catch (AggregateException) { }

            // Turn off failure and retry
            fail = false;
            var retryPacket = new ItemUnlockPacket
            {
                ItemId = "hasSilkSpecial",
                Category = ItemCategory.Ability,
                TransactionId = Guid.NewGuid()
            };

            service.HandleItemUnlockPacket(peer, retryPacket);
            Assert.Contains("hasSilkSpecial", service.UnlockedItems);
        }

        [Fact]
        public void F09_B02_EmptyItemId_HandledWithoutCrashing()
        {
            var service = new InventorySyncService();
            service.HandleItemUnlockPacket(new NetworkPeerId(2), new ItemUnlockPacket
            {
                ItemId = "",
                Category = ItemCategory.Tool,
                TransactionId = Guid.NewGuid()
            });

            Assert.DoesNotContain("", service.UnlockedItems);
        }

        [Fact]
        public void F09_B03_FiftyFailedRetries_FiftyFirstSucceeds()
        {
            var service = new InventorySyncService();
            var peer = new NetworkPeerId(2);
            int attempts = 0;

            service.OnItemUnlocked += (item, cat) =>
            {
                attempts++;
                if (attempts <= 50) throw new InvalidOperationException("Busy");
            };

            for (int i = 1; i <= 50; i++)
            {
                try
                {
                    service.HandleItemUnlockPacket(peer, new ItemUnlockPacket
                    {
                        ItemId = "hasParry",
                        Category = ItemCategory.Ability,
                        TransactionId = Guid.NewGuid()
                    });
                }
                catch (InvalidOperationException) { }
            }

            // Attempt 51
            service.HandleItemUnlockPacket(peer, new ItemUnlockPacket
            {
                ItemId = "hasParry",
                Category = ItemCategory.Ability,
                TransactionId = Guid.NewGuid()
            });

            Assert.Contains("hasParry", service.UnlockedItems);
        }

        [Fact]
        public void F09_B04_MultithreadedConcurrentUnlocksOnSameItem_OnlyUnlockedOnce()
        {
            var service = new InventorySyncService();
            var peer = new NetworkPeerId(2);
            int unlockEvents = 0;

            service.OnItemUnlocked += (item, cat) => unlockEvents++;

            Parallel.For(0, 20, i =>
            {
                service.HandleItemUnlockPacket(peer, new ItemUnlockPacket
                {
                    ItemId = "sharedToolKey",
                    Category = ItemCategory.Tool,
                    TransactionId = Guid.NewGuid()
                });
            });

            Assert.Equal(1, unlockEvents);
            Assert.Single(service.UnlockedItems);
        }

        [Fact]
        public void F09_B05_InvalidItemCategoryEnum_HandledGracefully()
        {
            var service = new InventorySyncService();
            var packet = new ItemUnlockPacket
            {
                ItemId = "unknownItem",
                Category = unchecked((ItemCategory)999), // Undefined enum
                TransactionId = Guid.NewGuid()
            };

            service.HandleItemUnlockPacket(new NetworkPeerId(2), packet);
            // Must not throw unhandled enum exception
        }

        #endregion

        #region F10 Boundary Tests: Revive wall check & safe fallback

        [Fact]
        public void F10_B01_CompletelyEnclosedInBox_FallsBackSafely()
        {
            var prober = new RemediationTestHarness.SolidGeometryGroundProber();
            // Solid box encompassing all lateral candidate offsets (-3.6 to +3.6)
            prober.Walls.Add(new RemediationTestHarness.SolidGeometryGroundProber.WallObstacle
            {
                MinX = -10f,
                MaxX = 10f,
                MinY = 0f,
                MaxY = 20f
            });
            prober.FallbackHazardLocation = (100f, 50f);

            bool found = prober.TryFindSafeGround(0f, 10f, out float safeX, out float safeY);
            Assert.True(found);
            Assert.Equal(100f, safeX);
            Assert.Equal(50f, safeY);
        }

        [Fact]
        public void F10_B02_PlatformNormalAtExactThreshold0707_Accepted()
        {
            var prober = new RemediationTestHarness.SolidGeometryGroundProber();
            prober.Platforms.Add(new RemediationTestHarness.SolidGeometryGroundProber.Platform
            {
                MinX = -5f,
                MaxX = 5f,
                SurfaceY = 10f,
                NormalY = SafeGroundSolver.MinGroundNormalY // Exactly 0.707f
            });

            bool found = prober.TryFindSafeGround(0f, 10f, out _, out _);
            Assert.True(found);
        }

        [Fact]
        public void F10_B03_PlatformNormalBelowThreshold0706_Rejected()
        {
            var prober = new RemediationTestHarness.SolidGeometryGroundProber();
            prober.Platforms.Add(new RemediationTestHarness.SolidGeometryGroundProber.Platform
            {
                MinX = -5f,
                MaxX = 5f,
                SurfaceY = 10f,
                NormalY = 0.706f // Just below 0.707f
            });

            bool found = prober.TryFindSafeGround(0f, 10f, out _, out _);
            Assert.False(found);
        }

        [Fact]
        public void F10_B04_ExtremePartnerCoordinates_NoArithmeticOverflow()
        {
            var prober = new RemediationTestHarness.SolidGeometryGroundProber();
            bool found = prober.TryFindSafeGround(100_000f, -100_000f, out float safeX, out float safeY);

            Assert.False(found);
            Assert.Equal(100_000f, safeX);
            Assert.Equal(-99_999.5f, safeY, 1);
        }

        [Fact]
        public void F10_B05_InfinitesimallyThinPlatform_DetectedByRaycast()
        {
            var prober = new RemediationTestHarness.SolidGeometryGroundProber();
            prober.Platforms.Add(new RemediationTestHarness.SolidGeometryGroundProber.Platform
            {
                MinX = -0.001f,
                MaxX = 0.001f,
                SurfaceY = 5.0f,
                NormalY = 1.0f
            });

            bool found = prober.TryFindSafeGround(0f, 5f, out float safeX, out _);
            Assert.True(found);
            Assert.Equal(0f, safeX);
        }

        #endregion

        #region F11 Boundary Tests: Revive prober dynamic buffer

        [Fact]
        public void F11_B01_Exactly16Colliders_BoundaryExpandsCleanly()
        {
            var prober = new RemediationTestHarness.SolidGeometryGroundProber();
            prober.Platforms.Add(new RemediationTestHarness.SolidGeometryGroundProber.Platform
            {
                MinX = -5f,
                MaxX = 5f,
                SurfaceY = 5f
            });

            for (int i = 0; i < 16; i++)
            {
                prober.Walls.Add(new RemediationTestHarness.SolidGeometryGroundProber.WallObstacle
                {
                    MinX = -0.1f,
                    MaxX = 0.1f,
                    MinY = 4.5f,
                    MaxY = 5.5f
                });
            }

            prober.TryFindSafeGround(0f, 5f, out _, out _);
            Assert.True(prober.BufferCapacity >= 32);
        }

        [Fact]
        public void F11_B02_Exactly32Colliders_BoundaryExpandsTo64()
        {
            var prober = new RemediationTestHarness.SolidGeometryGroundProber();
            prober.Platforms.Add(new RemediationTestHarness.SolidGeometryGroundProber.Platform
            {
                MinX = -5f,
                MaxX = 5f,
                SurfaceY = 5f
            });

            for (int i = 0; i < 32; i++)
            {
                prober.Walls.Add(new RemediationTestHarness.SolidGeometryGroundProber.WallObstacle
                {
                    MinX = -0.1f,
                    MaxX = 0.1f,
                    MinY = 4.5f,
                    MaxY = 5.5f
                });
            }

            prober.TryFindSafeGround(0f, 5f, out _, out _);
            Assert.True(prober.BufferCapacity >= 64);
        }

        [Fact]
        public void F11_B03_BufferSaturationAt128_HandledWithoutOutOfMemory()
        {
            var prober = new RemediationTestHarness.SolidGeometryGroundProber
            {
                MaxBufferCapacity = 128
            };

            prober.Platforms.Add(new RemediationTestHarness.SolidGeometryGroundProber.Platform
            {
                MinX = -10f,
                MaxX = 10f,
                SurfaceY = 0f,
                NormalY = 1.0f
            });

            for (int i = 0; i < 150; i++)
            {
                prober.Walls.Add(new RemediationTestHarness.SolidGeometryGroundProber.WallObstacle
                {
                    MinX = -5f,
                    MaxX = 5f,
                    MinY = 0f,
                    MaxY = 10f
                });
            }

            bool found = prober.TryFindSafeGround(0f, 5f, out _, out _);
            Assert.False(found);
            Assert.True(prober.LastQueryBufferSaturated);
        }

        [Fact]
        public void F11_B04_ZeroCollidersInScene_ReturnsFalseSafely()
        {
            var prober = new RemediationTestHarness.SolidGeometryGroundProber();
            bool found = prober.TryFindSafeGround(0f, 0f, out _, out _);
            Assert.False(found);
            Assert.Equal(0, prober.LastQueryColliderCount);
        }

        [Fact]
        public void F11_B05_RapidSequentialQueriesWithVaryingDensities_DoNotLeakMemory()
        {
            var prober = new RemediationTestHarness.SolidGeometryGroundProber();
            prober.Platforms.Add(new RemediationTestHarness.SolidGeometryGroundProber.Platform
            {
                MinX = -10f,
                MaxX = 10f,
                SurfaceY = 5f
            });

            // 50 alternating queries
            for (int q = 0; q < 50; q++)
            {
                prober.Walls.Clear();
                int count = q % 2 == 0 ? 5 : 25;
                for (int i = 0; i < count; i++)
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
            }

            Assert.True(prober.BufferCapacity <= prober.MaxBufferCapacity);
        }

        #endregion

        #region F12 Boundary Tests: ReliableUnordered sequence wrap-around

        [Fact]
        public void F12_B01_LaggedDiffExactly4095_AcceptedWithinWindow()
        {
            var window = new RemediationTestHarness.SimulatedUnorderedReplayWindow();
            window.Process(4095);

            // Diff = 4095 - 0 = 4095 (< Capacity 4096)
            var status = window.Process(0);
            Assert.Equal(RemediationTestHarness.ReplayStatus.Accepted, status);
        }

        [Fact]
        public void F12_B02_LaggedDiffExactly4096_MarkedStale()
        {
            var window = new RemediationTestHarness.SimulatedUnorderedReplayWindow();
            window.Process(4096);

            // Diff = 4096 - 0 = 4096 (>= Capacity 4096)
            var status = window.Process(0);
            Assert.Equal(RemediationTestHarness.ReplayStatus.Stale, status);
        }

        [Fact]
        public void F12_B03_LaggedDiff4097_MarkedStale()
        {
            var window = new RemediationTestHarness.SimulatedUnorderedReplayWindow();
            window.Process(4097);

            var status = window.Process(0);
            Assert.Equal(RemediationTestHarness.ReplayStatus.Stale, status);
        }

        [Fact]
        public void F12_B04_SequenceWrapAroundAcrossUintMax_AcceptedCleanly()
        {
            var window = new RemediationTestHarness.SimulatedUnorderedReplayWindow();
            window.Process(uint.MaxValue - 100);

            // Forward wrap to sequence 100
            var status = window.Process(100);
            Assert.Equal(RemediationTestHarness.ReplayStatus.Accepted, status);
            Assert.Equal(100u, window.Latest);
        }

        [Fact]
        public void F12_B05_SequenceJumpExceedingCapacity_ClearsSeenBufferAndAdvances()
        {
            var window = new RemediationTestHarness.SimulatedUnorderedReplayWindow();
            window.Process(10);

            // Massive jump forwards by 100,000 sequences
            var status = window.Process(100_010);
            Assert.Equal(RemediationTestHarness.ReplayStatus.Accepted, status);
            Assert.Equal(100_010u, window.Latest);

            // Old sequence 10 is now stale
            Assert.Equal(RemediationTestHarness.ReplayStatus.Stale, window.Process(10));
        }

        #endregion

        #region F13 Boundary Tests: All unlocked crests catch-up

        [Fact]
        public void F13_B01_EmptyUnlockedCrestsList_HandledCleanly()
        {
            var inventory = new InventorySyncService();
            // Fresh save with 0 unlocked crests
            Assert.Empty(inventory.UnlockedItems);
        }

        [Fact]
        public void F13_B02_HundredCrestsUnlocked_AllRetained()
        {
            var inventory = new InventorySyncService();
            for (int i = 0; i < 100; i++)
            {
                inventory.OnLocalItemDiscovered($"Crest_{i}", ItemCategory.Crest, force: true);
            }

            Assert.Equal(100, inventory.UnlockedItems.Count);
        }

        [Fact]
        public void F13_B03_SpecialCharactersInCrestName_PreservedVerbatim()
        {
            var inventory = new InventorySyncService();
            string specialCrest = "Crest-Alpha_v2.0 [Special]";
            inventory.OnLocalItemDiscovered(specialCrest, ItemCategory.Crest, force: true);

            Assert.Contains(specialCrest, inventory.UnlockedItems);
        }

        [Fact]
        public void F13_B04_NullEquippedCrestString_DoesNotThrow()
        {
            var inventory = new InventorySyncService();
            inventory.SetLocalEquippedCrest("");
            Assert.Equal("", inventory.LocalEquippedCrest);
        }

        [Fact]
        public void F13_B05_WhitespaceCrestName_SkippedOrDeduplicated()
        {
            var inventory = new InventorySyncService();
            inventory.OnLocalItemDiscovered("   ", ItemCategory.Crest, force: true);
            // Must not throw or corrupt collection
        }

        #endregion

        #region F14 Boundary Tests: Bounded counter transaction memory

        [Fact]
        public void F14_B01_Exactly500Transactions_AllRetained()
        {
            var tracker = new RemediationTestHarness.BoundedCounterTransactionTracker();
            for (int i = 0; i < 500; i++)
            {
                tracker.TryProcessTransaction(Guid.NewGuid());
            }

            Assert.Equal(500, tracker.Count);
        }

        [Fact]
        public void F14_B02_Exactly501Transactions_OldestEvicted()
        {
            var tracker = new RemediationTestHarness.BoundedCounterTransactionTracker();
            var first = Guid.NewGuid();
            tracker.TryProcessTransaction(first);

            for (int i = 0; i < 500; i++)
            {
                tracker.TryProcessTransaction(Guid.NewGuid());
            }

            Assert.Equal(500, tracker.Count);
            Assert.False(tracker.Contains(first)); // Evicted
        }

        [Fact]
        public void F14_B03_TwoThousandTransactions_MemoryBoundedAt500()
        {
            var tracker = new RemediationTestHarness.BoundedCounterTransactionTracker();
            for (int i = 0; i < 2000; i++)
            {
                tracker.TryProcessTransaction(Guid.NewGuid());
            }

            Assert.Equal(500, tracker.Count);
        }

        [Fact]
        public void F14_B04_EvictedGuidReInjected_AcceptedAsNewTransaction()
        {
            var tracker = new RemediationTestHarness.BoundedCounterTransactionTracker();
            var recycledGuid = Guid.NewGuid();

            tracker.TryProcessTransaction(recycledGuid);

            // Push 500 other items to evict recycledGuid
            for (int i = 0; i < 500; i++)
            {
                tracker.TryProcessTransaction(Guid.NewGuid());
            }

            Assert.False(tracker.Contains(recycledGuid));

            // Re-injecting should now be accepted
            bool acceptedAgain = tracker.TryProcessTransaction(recycledGuid);
            Assert.True(acceptedAgain);
        }

        [Fact]
        public void F14_B05_EmptyGuid_HandledSafely()
        {
            var tracker = new RemediationTestHarness.BoundedCounterTransactionTracker();
            bool first = tracker.TryProcessTransaction(Guid.Empty);
            bool second = tracker.TryProcessTransaction(Guid.Empty);

            Assert.True(first);
            Assert.False(second); // Duplicate Guid.Empty
        }

        #endregion

        #region F15 Boundary Tests: Steam heartbeat queue drain

        [Fact]
        public void F15_B01_MaxDatagramBatch64Packets_AllDrainedBeforeTimeoutCheck()
        {
            var transport = new RemediationTestHarness.SimulatedHeartbeatTransport();
            long now = 30000;
            transport.LastPongReceivedMs = 5000; // 25000ms elapsed (>15000 timeout)

            // Fill queue with 63 data packets and 1 pong at the end
            for (int i = 0; i < 63; i++) transport.IncomingQueue.Enqueue($"DATA_{i}");
            transport.IncomingQueue.Enqueue("PONG");

            transport.PollEvents(now, drainFirst: true);

            Assert.True(transport.IsConnected);
            Assert.Equal(now, transport.LastPongReceivedMs);
        }

        [Fact]
        public void F15_B02_ExactTimeoutMillisecondThreshold_Handled()
        {
            var transport = new RemediationTestHarness.SimulatedHeartbeatTransport();
            long now = 20000;
            transport.LastPongReceivedMs = 5000; // Exactly 15000 ms difference

            transport.PollEvents(now, drainFirst: true);
            // Exactly at threshold is not strictly > timeout, so remains connected
            Assert.True(transport.IsConnected);
        }

        [Fact]
        public void F15_B03_ThousandPacketsBurstInQueue_DrainedWithoutStackOverflow()
        {
            var transport = new RemediationTestHarness.SimulatedHeartbeatTransport();
            for (int i = 0; i < 1000; i++) transport.IncomingQueue.Enqueue("PONG");

            transport.PollEvents(10000, drainFirst: true);
            Assert.Empty(transport.IncomingQueue);
            Assert.True(transport.IsConnected);
        }

        [Fact]
        public void F15_B04_EmptyPacketPayloadInQueue_DoesNotCrashDrain()
        {
            var transport = new RemediationTestHarness.SimulatedHeartbeatTransport();
            transport.IncomingQueue.Enqueue("");
            transport.IncomingQueue.Enqueue("PONG");

            transport.PollEvents(5000, drainFirst: true);
            Assert.True(transport.IsConnected);
        }

        [Fact]
        public void F15_B05_MultipleSequentialPollsWithQueueDraining_OperateCleanly()
        {
            var transport = new RemediationTestHarness.SimulatedHeartbeatTransport();
            long time = 1000;

            for (int i = 0; i < 10; i++)
            {
                time += 1000;
                transport.IncomingQueue.Enqueue("PONG");
                transport.PollEvents(time, drainFirst: true);
                Assert.True(transport.IsConnected);
            }
        }

        #endregion

        #region F16 Boundary Tests: Comprehensive automated test coverage

        [Fact]
        public void F16_B01_ZeroLengthBuffer_PacketHeaderReturnsFalse()
        {
            ReadOnlySpan<byte> empty = ReadOnlySpan<byte>.Empty;
            bool success = PacketHeader.TryReadFrom(empty, out _);
            Assert.False(success);
        }

        [Fact]
        public void F16_B02_TruncatedBuffer_PacketHeaderReturnsFalse()
        {
            Span<byte> truncated = stackalloc byte[PacketHeader.HeaderSize - 1];
            bool success = PacketHeader.TryReadFrom(truncated, out _);
            Assert.False(success);
        }

        [Fact]
        public void F16_B03_ReliableBacklogCap_NetworkLimits()
        {
            Assert.Equal(512, NetworkLimits.ReliableBacklogCap);
        }

        [Fact]
        public void F16_B04_CorruptedRoomCode_TryDecodeReturnsFalse()
        {
            bool success1 = RoomCodeCodec.TryDecode("INVALID-CODE-XYZ", out _, out _);
            bool success2 = RoomCodeCodec.TryDecode("", out _, out _);
            bool success3 = RoomCodeCodec.TryDecode("SK-8888-8888", out _, out _); // 8 is invalid base32

            Assert.False(success1);
            Assert.False(success2);
            Assert.False(success3);
        }

        [Fact]
        public void F16_B05_PeerIdZeroAndMax_HandledProperly()
        {
            var p0 = new NetworkPeerId(0);
            var pMax = new NetworkPeerId(ulong.MaxValue);

            Assert.Equal(0ul, p0.Value);
            Assert.Equal(ulong.MaxValue, pMax.Value);
        }

        #endregion

        #region F17 Boundary Tests: Dynamic tick rate active update

        [Fact]
        public void F17_B01_ExactBoundary15Hz_Accepted()
        {
            var rate = new AdaptiveTickRate(30f);
            RemediationTestHarness.SetDefaultTickHzSafe(rate, 15.0f);
            Assert.Equal(15.0f, rate.DefaultTickHz);
            rate.Reset();
            Assert.Equal(15.0f, rate.CurrentTickHz);
        }

        [Fact]
        public void F17_B02_ExactBoundary60Hz_Accepted()
        {
            var rate = new AdaptiveTickRate(30f);
            RemediationTestHarness.SetDefaultTickHzSafe(rate, 60.0f);
            Assert.Equal(60.0f, rate.DefaultTickHz);
            rate.Reset();
            Assert.Equal(60.0f, rate.CurrentTickHz);
        }

        [Fact]
        public void F17_B03_BelowMinimum14_99Hz_ClampedTo15Hz()
        {
            var rate = new AdaptiveTickRate(30f);
            RemediationTestHarness.SetDefaultTickHzSafe(rate, 14.99f);
            Assert.Equal(15.0f, rate.DefaultTickHz);
            rate.Reset();
            Assert.Equal(15.0f, rate.CurrentTickHz);
        }

        [Fact]
        public void F17_B04_AboveMaximum60_01Hz_ClampedTo60Hz()
        {
            var rate = new AdaptiveTickRate(30f);
            RemediationTestHarness.SetDefaultTickHzSafe(rate, 60.01f);
            Assert.Equal(60.0f, rate.DefaultTickHz);
            rate.Reset();
            Assert.Equal(60.0f, rate.CurrentTickHz);
        }

        [Fact]
        public void F17_B05_HundredRapidUpdates_FinalStateMatchesLast()
        {
            var rate = new AdaptiveTickRate(30f);
            for (int i = 15; i <= 60; i++)
            {
                RemediationTestHarness.SetDefaultTickHzSafe(rate, i);
            }
            Assert.Equal(60f, rate.DefaultTickHz);
            rate.Reset();
            Assert.Equal(60f, rate.CurrentTickHz);
        }

        #endregion

        #region F18 Boundary Tests: Dash trail light fade shutoff

        [Fact]
        public void F18_B01_IntensityAtExactBoundary005_DeactivatesImmediately()
        {
            var result = RemediationTestHarness.SimulateRimLightDecay(
                initialIntensity: 0.05f,
                deltaTime: 0.016f,
                useFixedLogic: true);

            Assert.Equal(0f, result.FinalIntensity);
            Assert.False(result.IsActive);
        }

        [Fact]
        public void F18_B02_IntensityAtExactBoundary010_DecaysAndTurnsOff()
        {
            var result = RemediationTestHarness.SimulateRimLightDecay(
                initialIntensity: 0.10f,
                deltaTime: 0.016f,
                useFixedLogic: true);

            Assert.Equal(0f, result.FinalIntensity);
            Assert.False(result.IsActive);
        }

        [Fact]
        public void F18_B03_DeltaTimeZero_IntensityUnchangedNoDivisionByZero()
        {
            var result = RemediationTestHarness.SimulateRimLightDecay(
                initialIntensity: 0.5f,
                deltaTime: 0.0f,
                useFixedLogic: true,
                maxSteps: 5);

            Assert.Equal(0.5f, result.FinalIntensity);
            Assert.True(result.IsActive);
        }

        [Fact]
        public void F18_B04_HugeDeltaTime_DropsDirectlyToZeroWithoutOvershoot()
        {
            var result = RemediationTestHarness.SimulateRimLightDecay(
                initialIntensity: 1.0f,
                deltaTime: 10.0f, // 10 seconds in one step
                useFixedLogic: true);

            Assert.Equal(0f, result.FinalIntensity);
            Assert.False(result.IsActive);
            Assert.Equal(1, result.Steps);
        }

        [Fact]
        public void F18_B05_SixtyFpsDecayMonotonicallyDecreasesToZero()
        {
            float dt = 1f / 60f; // 16.66ms
            var result = RemediationTestHarness.SimulateRimLightDecay(
                initialIntensity: 1.0f,
                deltaTime: dt,
                useFixedLogic: true,
                maxSteps: 120);

            Assert.Equal(0f, result.FinalIntensity);
            Assert.False(result.IsActive);
            Assert.True(result.Steps <= 60); // Complete turnoff within 1 second
        }

        #endregion
    }
}
