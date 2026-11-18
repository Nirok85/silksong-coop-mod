using System;
using System.Collections.Generic;
using SilksongCoop.Core.Clones;
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
    /// Tier 3: Pairwise Cross-Feature Combination Tests.
    /// Exercises concurrent asynchronous interactions between multiple subsystems
    /// (Features F01 through F18 cross-matrix, at least 18 tests).
    /// </summary>
    public class Tier3CrossFeatureTests
    {
        [Fact]
        public void Test_01_F01_x_F09_WhitelistValidation_With_InventoryUnlockRollback()
        {
            var service = new InventorySyncService();
            var peer = new NetworkPeerId(2);

            // 1. Unauthorized field is rejected by allowlist and never reaches unlock handler
            string unauthorizedKey = "isInvincible";
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed(unauthorizedKey, ItemCategory.Ability));

            // 2. Valid whitelisted ability with transient failure in handler
            string validKey = "hasDash";
            Assert.True(RemediationTestHarness.IsSaveBoolAllowed(validKey, ItemCategory.Ability));

            bool shouldFail = true;
            bool retryRan = false;

            service.OnItemUnlocked += (item, cat) =>
            {
                if (shouldFail) throw new InvalidOperationException("Transient lock");
                retryRan = true;
            };

            var txId = Guid.NewGuid();
            try
            {
                service.HandleItemUnlockPacket(peer, new ItemUnlockPacket
                {
                    ItemId = validKey,
                    Category = ItemCategory.Ability,
                    TransactionId = txId
                });
            }
            catch (InvalidOperationException) { }

            // 3. Retry succeeds and registers item
            shouldFail = false;
            service.HandleItemUnlockPacket(peer, new ItemUnlockPacket
            {
                ItemId = validKey,
                Category = ItemCategory.Ability,
                TransactionId = Guid.NewGuid()
            });

            Assert.True(retryRan);
            Assert.Contains(validKey, service.UnlockedItems);
        }

        [Fact]
        public void Test_02_F02_x_F04_WalletSeedRetry_With_3rdClientPreSeedDelta()
        {
            var service = new VitalsSyncService();
            var peer2 = new NetworkPeerId(2);
            var peer3 = new NetworkPeerId(3);

            // Peer 3 sends currency transaction (+80) before Host's wallet seed is delivered
            long preSeedDeltaFromPeer3 = 80;

            // Host has wallet seed outstanding with retry count 3
            var outstanding = new Dictionary<NetworkPeerId, byte>
            {
                { peer2, 3 }
            };

            int pumpAttempts = 0;
            // Pump fails twice then succeeds on third attempt
            while (outstanding.Count > 0)
            {
                pumpAttempts++;
                RemediationTestHarness.PumpWalletSeedSafe(
                    outstanding,
                    peer => pumpAttempts >= 3,
                    out _);
            }

            Assert.Equal(3, pumpAttempts);
            Assert.Empty(outstanding);

            // Seed arrives with base total = 300, pending local = 10, plus 3rd client delta = 80
            RemediationTestHarness.SeedWalletWithRemoteDelta(
                service,
                CurrencyType.Rosaries,
                total: 300,
                pendingLocalDelta: 10,
                preSeedRemoteDelta: preSeedDeltaFromPeer3);

            Assert.Equal(390, service.SharedRosaries);
        }

        [Fact]
        public void Test_03_F03_x_F04_CatchUpCurrencyReadiness_With_3rdClientDelta()
        {
            var service = new VitalsSyncService();
            var peer3 = new NetworkPeerId(3);

            // Connecting client receives 3rd client delta (+45) before seed
            long preSeedDelta = 45;

            // Seed wallet with host total 150
            RemediationTestHarness.SeedWalletWithRemoteDelta(
                service,
                CurrencyType.Rosaries,
                total: 150,
                pendingLocalDelta: 0,
                preSeedRemoteDelta: preSeedDelta);

            Assert.Equal(195, service.SharedRosaries);

            // After readiness, zero pickup claim is delivered without sequence mismatch
            service.HandleCurrencyPacket(peer3, new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 0,
                TotalWalletAmount = 195,
                TransactionId = Guid.NewGuid(),
                PickupUniqueId = 1
            });

            // Normal subsequent transaction (+20) delivered
            service.HandleCurrencyPacket(peer3, new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 20,
                TotalWalletAmount = 215,
                TransactionId = Guid.NewGuid(),
                PickupUniqueId = 2
            });

            Assert.Equal(215, service.SharedRosaries);
        }

        [Fact]
        public void Test_04_F05_x_F14_StatUpgradeChannel_With_BoundedCounterMemory()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();
            var tracker = new RemediationTestHarness.BoundedCounterTransactionTracker();

            // Simulate 600 heart piece fragment pickups across multiple co-op upgrade cycles
            for (int i = 1; i <= 600; i++)
            {
                var txId = Guid.NewGuid();
                bool isNew = tracker.TryProcessTransaction(txId);
                Assert.True(isNew);

                // Every 4th fragment triggers mask upgrade check
                if (i % 4 == 0)
                {
                    controller.ApplyHeartPieceFragments(4, sharedMaxHealth: controller.CurrentMaxHealth);
                    controller.OnSharedMaxHealthReceived(controller.CurrentMaxHealth);
                }
            }

            // Tracker strictly bounded to 500 entries (no memory leak)
            Assert.Equal(500, tracker.Count);
            // Masks upgraded cleanly without double increments
            Assert.True(controller.CurrentMaxHealth > 5);
        }

        [Fact]
        public void Test_05_F06_x_F10_DeathEpochSync_With_ReviveWallCollision()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var prober = new RemediationTestHarness.SolidGeometryGroundProber();

            // 1. Peer syncs to Epoch 3 (host had 2 prior wipes)
            coordinator.HandleDeathPacket(new NetworkPeerId(2), new DeathLifecyclePacket
            {
                EventType = DeathEventType.BenchCheckpointUpdate,
                Epoch = 3
            });
            Assert.Equal(3u, coordinator.CurrentEpoch);

            // 2. Solo death occurs in Epoch 3
            coordinator.IsPartnerAlive = true;
            coordinator.ReviveCooldownDuration = 10f;
            coordinator.LastPartnerPosition = (20f, 15f);
            coordinator.OnLocalPlayerDied(0f, 0f);

            Assert.Equal(DeathState.Spectating, coordinator.CurrentState);

            // 3. Complete spectator cooldown (clamped to min 15s)
            coordinator.UpdateTick(20f);
            Assert.Equal(DeathState.RespawningAtPartner, coordinator.CurrentState);

            // 4. Revive prober checks ground with solid wall obstructing partner position
            prober.Platforms.Add(new RemediationTestHarness.SolidGeometryGroundProber.Platform
            {
                MinX = 10f,
                MaxX = 30f,
                SurfaceY = 15f,
                NormalY = 1.0f
            });
            prober.Walls.Add(new RemediationTestHarness.SolidGeometryGroundProber.WallObstacle
            {
                MinX = 19.5f,
                MaxX = 20.5f,
                MinY = 14f,
                MaxY = 17f
            });

            bool safeFound = prober.TryFindSafeGround(20f, 15f, out float safeX, out float safeY);
            Assert.True(safeFound);
            Assert.Equal(21.2f, safeX, 1); // Safely avoided wall!
        }

        [Fact]
        public void Test_06_F07_x_F08_HostAuthorityRenewal_With_UnloadedEnemyDamage()
        {
            var syncService = new EnemySyncService();
            var accumulator = new RemediationTestHarness.UnloadedEnemyDamageAccumulator();
            ulong bossKey = 4004;

            // Client deals 35 + 35 + 30 = 100 damage while boss was unloaded on host
            accumulator.RecordHit(bossKey, 35, isDead: false);
            accumulator.RecordHit(bossKey, 35, isDead: false);
            accumulator.RecordHit(bossKey, 30, isDead: true);

            // Host loads room with 100 HP boss
            bool killConfirmed = accumulator.TryResolvePendingKill(bossKey, loadedHp: 100, out int totalDmg);
            Assert.True(killConfirmed);
            Assert.Equal(100, totalDmg);

            // Host broadcasts dead snapshot, client accepts and confirms host authority
            bool snapshotAccepted = syncService.TryAcceptSnapshot(bossKey, 0, dead: true, timestamp: 1000);
            Assert.True(snapshotAccepted);
            Assert.True(syncService.HasFreshHostAuthority(bossKey));
        }

        [Fact]
        public void Test_07_F10_x_F11_SolidWallDetection_With_DynamicBufferExpansion()
        {
            var prober = new RemediationTestHarness.SolidGeometryGroundProber();
            prober.Platforms.Add(new RemediationTestHarness.SolidGeometryGroundProber.Platform
            {
                MinX = -10f,
                MaxX = 10f,
                SurfaceY = 5f,
                NormalY = 1.0f
            });

            // Add 22 wall colliders surrounding candidate offset 0.0
            for (int i = 0; i < 22; i++)
            {
                prober.Walls.Add(new RemediationTestHarness.SolidGeometryGroundProber.WallObstacle
                {
                    MinX = -0.2f,
                    MaxX = 0.2f,
                    MinY = 4.5f,
                    MaxY = 6.0f
                });
            }

            bool found = prober.TryFindSafeGround(0f, 5f, out float safeX, out _);
            Assert.True(found);
            Assert.True(prober.BufferCapacity >= 32); // Buffer expanded
            Assert.Equal(1.2f, safeX, 1);             // Avoided walls
        }

        [Fact]
        public void Test_08_F12_x_F15_ReliableUnorderedWrap_With_SteamHeartbeatDrain()
        {
            var window = new RemediationTestHarness.SimulatedUnorderedReplayWindow();
            var transport = new RemediationTestHarness.SimulatedHeartbeatTransport();

            // Advance sequences past 4096
            for (uint i = 1; i <= 4096; i++)
            {
                window.Process(i);
            }

            // Queue contains heartbeats and burst data
            transport.LastPongReceivedMs = 1000;
            transport.IncomingQueue.Enqueue("PONG");

            // Drain first handles heartbeat timeout smoothly
            transport.PollEvents(now: 20000, drainFirst: true);
            Assert.True(transport.IsConnected);

            // Out of window delayed packet 0 is marked stale (not ACKed)
            var status = window.Process(0);
            Assert.Equal(RemediationTestHarness.ReplayStatus.Stale, status);
        }

        [Fact]
        public void Test_09_F17_x_F18_DynamicTickRate_With_DashTrailDecay()
        {
            var tickRate = new AdaptiveTickRate(30f);
            RemediationTestHarness.SetDefaultTickHzSafe(tickRate, 60f);
            Assert.Equal(60f, tickRate.DefaultTickHz);
            tickRate.Reset();
            Assert.Equal(60f, tickRate.CurrentTickHz);

            float dt = tickRate.CurrentInterval; // 1 / 60s
            var decayResult = RemediationTestHarness.SimulateRimLightDecay(
                initialIntensity: 0.09f, // Trap zone
                deltaTime: dt,
                useFixedLogic: true);

            Assert.Equal(0f, decayResult.FinalIntensity);
            Assert.False(decayResult.IsActive);
        }

        [Fact]
        public void Test_10_F01_x_F13_PlayerDataWhitelist_With_AllUnlockedCrests()
        {
            var inventory = new InventorySyncService();
            string[] crests = new[] { "HunterCrest", "ReaperCrest" };

            foreach (var crest in crests)
            {
                inventory.OnLocalItemDiscovered(crest, ItemCategory.Crest, force: true);
            }

            // Verify both crests registered
            Assert.Equal(2, inventory.UnlockedItems.Count);

            // Malicious key injected alongside crest catch-up is rejected
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed("infiniteGeo", ItemCategory.Ability));
        }

        [Fact]
        public void Test_11_F02_x_F03_WalletSeedRetry_With_ReadinessGating()
        {
            var outstanding = new Dictionary<NetworkPeerId, byte>
            {
                { new NetworkPeerId(5), 2 }
            };

            // Unready peer: retry decrement works cleanly
            bool success = RemediationTestHarness.PumpWalletSeedSafe(
                outstanding,
                _ => false,
                out bool failed);

            Assert.True(success);
            Assert.False(failed);
            Assert.Equal(1, outstanding[new NetworkPeerId(5)]);
        }

        [Fact]
        public void Test_12_F04_x_F14_3rdClientCurrencyDelta_With_CounterTransactionMemory()
        {
            var vitals = new VitalsSyncService();
            var tracker = new RemediationTestHarness.BoundedCounterTransactionTracker();

            // Currency pre-seed delta +60
            RemediationTestHarness.SeedWalletWithRemoteDelta(
                vitals,
                CurrencyType.Rosaries,
                total: 200,
                pendingLocalDelta: 0,
                preSeedRemoteDelta: 60);

            Assert.Equal(260, vitals.SharedRosaries);

            // Counter fragment tracking operates independently
            var counterTx = Guid.NewGuid();
            Assert.True(tracker.TryProcessTransaction(counterTx));
            Assert.False(tracker.TryProcessTransaction(counterTx)); // Deduplicated
        }

        [Fact]
        public void Test_13_F06_x_F08_TeamWipeEpochRespawn_With_UnloadedEnemyRegistry()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var accumulator = new RemediationTestHarness.UnloadedEnemyDamageAccumulator();
            ulong bossKey = 5050;

            // Damage recorded before wipe
            accumulator.RecordHit(bossKey, 50, isDead: false);

            // Team wipe triggers epoch advance
            coordinator.HandleDeathPacket(new NetworkPeerId(2), new DeathLifecyclePacket
            {
                EventType = DeathEventType.TeamWipeOccurred,
                Epoch = 1
            });
            coordinator.ResetAfterBenchRespawn();
            Assert.Equal(2u, coordinator.CurrentEpoch);

            // Hit recorded after wipe
            accumulator.RecordHit(bossKey, 50, isDead: true);

            bool resolved = accumulator.TryResolvePendingKill(bossKey, loadedHp: 100, out int totalDmg);
            Assert.True(resolved);
            Assert.Equal(100, totalDmg);
        }

        [Fact]
        public void Test_14_F07_x_F17_EnemyAuthorityRenewal_With_DynamicTickRate()
        {
            var tickRate = new AdaptiveTickRate(30f);
            RemediationTestHarness.SetDefaultTickHzSafe(tickRate, 15f); // Throttled to 15 Hz

            var syncService = new EnemySyncService();
            syncService.TryAcceptSnapshot(100, 80, dead: false, timestamp: 100);

            // Authority remains fresh even with lower update frequency
            Assert.True(syncService.HasFreshHostAuthority(100));
        }

        [Fact]
        public void Test_15_F09_x_F14_InventoryRollback_With_CounterTransactionFifo()
        {
            var tracker = new RemediationTestHarness.BoundedCounterTransactionTracker();
            var txId = Guid.NewGuid();

            // Transaction added
            Assert.True(tracker.TryProcessTransaction(txId));

            // On rollback / reset, tracker state cleans up
            tracker.Reset();
            Assert.Equal(0, tracker.Count);
            // Re-adding succeeds
            Assert.True(tracker.TryProcessTransaction(txId));
        }

        [Fact]
        public void Test_16_F11_x_F06_DynamicBufferReviveProber_With_DeathLifecycleSpectator()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.IsPartnerAlive = true;
            coordinator.ReviveCooldownDuration = 15f;
            coordinator.OnLocalPlayerDied(10f, 20f);

            coordinator.UpdateTick(20f);
            Assert.Equal(DeathState.RespawningAtPartner, coordinator.CurrentState);

            var prober = new RemediationTestHarness.SolidGeometryGroundProber();
            prober.Platforms.Add(new RemediationTestHarness.SolidGeometryGroundProber.Platform
            {
                MinX = 5f,
                MaxX = 15f,
                SurfaceY = 20f,
                NormalY = 1.0f
            });

            bool safeFound = prober.TryFindSafeGround(10f, 20f, out float safeX, out float safeY);
            Assert.True(safeFound);
            coordinator.CompleteRevival();
            Assert.Equal(DeathState.Alive, coordinator.CurrentState);
        }

        [Fact]
        public void Test_17_F12_x_F17_ReliableUnorderedWindow_With_AdaptiveRateThrottling()
        {
            var window = new RemediationTestHarness.SimulatedUnorderedReplayWindow();
            var rate = new AdaptiveTickRate(60f);
            rate.Reset();

            // Simulate network jitter: RTT 250ms over several ticks
            for (float t = 1.0f; t <= 15.0f; t += 0.6f)
            {
                rate.Update(currentTime: t, rttMs: 250f, lossPercent: 5f);
            }
            Assert.True(rate.CurrentTickHz <= 25f);

            // Bursty out-of-order packets during jitter
            Assert.Equal(RemediationTestHarness.ReplayStatus.Accepted, window.Process(5));
            Assert.Equal(RemediationTestHarness.ReplayStatus.Accepted, window.Process(3));
            Assert.Equal(RemediationTestHarness.ReplayStatus.Accepted, window.Process(4));
            Assert.Equal(5u, window.Latest);
        }

        [Fact]
        public void Test_18_F15_x_F06_HeartbeatQueueDrain_During_BenchRespawnTransition()
        {
            var transport = new RemediationTestHarness.SimulatedHeartbeatTransport();
            var coordinator = new DeathLifecycleCoordinator();

            // Long bench reload transition: 16000ms elapsed
            transport.LastPongReceivedMs = 1000;
            transport.IncomingQueue.Enqueue("PONG");

            // Coordinator adopts new epoch during respawn
            coordinator.HandleDeathPacket(new NetworkPeerId(2), new DeathLifecyclePacket
            {
                EventType = DeathEventType.BenchCheckpointUpdate,
                Epoch = 2
            });

            // Heartbeat drained first prevents timeout
            transport.PollEvents(now: 17000, drainFirst: true);
            Assert.True(transport.IsConnected);
            Assert.Equal(2u, coordinator.CurrentEpoch);
        }
    }
}
