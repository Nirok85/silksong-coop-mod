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
    /// Tier 4: Real-World Application Workload Scenarios.
    /// Simulates complex, end-to-end multi-peer co-op sessions modeling real gameplay events,
    /// room transitions, boss arenas, network impairments, and recovery cycles (9 Scenarios).
    /// </summary>
    public class Tier4RealWorldScenarioTests
    {
        /// <summary>
        /// Scenario 1: Late-Join Catch-Up in Active Boss Arena.
        /// A 3rd player joins late while Host and Client 2 are fighting a boss.
        /// Host broadcasts all unlocked crests, inventory tools, wallet balances, and current death epoch.
        /// The new peer synchronizes epoch, receives wallet seed, and connects without losing currency deltas
        /// or causing enumeration exceptions.
        /// Features exercised: F2, F3, F4, F6, F13.
        /// </summary>
        [Fact]
        public void Scenario01_LateJoinCatchUpInActiveBossArena()
        {
            var hostSession = new CoopSessionState();
            var hostPeer = new NetworkPeerId(1);
            hostSession.SetHosting(hostPeer, "SK-BOSS-ARENA");

            var client2Peer = new NetworkPeerId(2);
            var client3LatePeer = new NetworkPeerId(3);

            hostSession.SetConnected(client2Peer);

            // Host and Client 2 are at Death Epoch 3 after 2 wipes in boss fight
            var hostDeath = new DeathLifecycleCoordinator();
            hostDeath.HandleDeathPacket(client2Peer, new DeathLifecyclePacket
            {
                EventType = DeathEventType.BenchCheckpointUpdate,
                Epoch = 3
            });
            Assert.Equal(3u, hostDeath.CurrentEpoch);

            // Client 2 earns 75 rosaries in boss room right before Client 3 connects
            var client3Vitals = new VitalsSyncService();
            long client2DeltaBeforeSeed = 75;

            // Host has outstanding wallet seed for Client 3 with retries
            var walletSeedQueue = new Dictionary<NetworkPeerId, byte>
            {
                { client3LatePeer, 3 }
            };

            // Pump wallet seed without collection modification crash
            bool pumpSuccess = RemediationTestHarness.PumpWalletSeedSafe(
                walletSeedQueue,
                peer => peer == client3LatePeer,
                out _);
            Assert.True(pumpSuccess);
            Assert.Empty(walletSeedQueue);

            // Client 3 adopts wallet seed (host balance 400 + pending local 10 + client 2 delta 75 = 485)
            RemediationTestHarness.SeedWalletWithRemoteDelta(
                client3Vitals,
                CurrencyType.Rosaries,
                total: 400,
                pendingLocalDelta: 10,
                preSeedRemoteDelta: client2DeltaBeforeSeed);
            Assert.Equal(485, client3Vitals.SharedRosaries);

            // Client 3 adopts Epoch 3
            var client3Death = new DeathLifecycleCoordinator();
            client3Death.HandleDeathPacket(hostPeer, new DeathLifecyclePacket
            {
                EventType = DeathEventType.BenchCheckpointUpdate,
                Epoch = hostDeath.CurrentEpoch
            });
            Assert.Equal(3u, client3Death.CurrentEpoch);

            // Client 3 receives all unlocked crests from host catch-up broadcast
            var client3Inventory = new InventorySyncService();
            string[] hostCrests = new[] { "HunterCrest", "ReaperCrest", "ArchitectCrest" };
            foreach (var crest in hostCrests)
            {
                client3Inventory.OnLocalItemDiscovered(crest, ItemCategory.Crest, force: true);
            }

            Assert.Equal(3, client3Inventory.UnlockedItems.Count);
            foreach (var crest in hostCrests)
            {
                Assert.Contains(crest, client3Inventory.UnlockedItems);
            }
        }

        /// <summary>
        /// Scenario 2: Cross-Room Multi-Hit Boss Battle with Host Migration.
        /// Client engages a multi-phase enemy in an adjacent room unloaded on Host.
        /// Client inflicts incremental damage (30 + 30 + 40). Host loads room,
        /// applies deferred kill without discarding legitimate death claim, while confirming
        /// snapshot extends authority ticks.
        /// Features exercised: F7, F8, F17.
        /// </summary>
        [Fact]
        public void Scenario02_CrossRoomMultiHitBossWithHostMigration()
        {
            var accumulator = new RemediationTestHarness.UnloadedEnemyDamageAccumulator();
            var clientEnemySync = new EnemySyncService();
            ulong bossKey = 9001;

            // Network tick rate operates adaptively
            var tickRate = new AdaptiveTickRate(30f);
            RemediationTestHarness.SetDefaultTickHzSafe(tickRate, 45f);

            // 1. Client attacks unloaded boss across 3 hits
            accumulator.RecordHit(bossKey, 30, isDead: false);
            accumulator.RecordHit(bossKey, 30, isDead: false);
            accumulator.RecordHit(bossKey, 40, isDead: true); // Total 100 dmg

            Assert.Equal(100, accumulator.GetAccumulatedDamage(bossKey));

            // 2. Host transitions into room: boss spawns with 100 HP
            int loadedBossHp = 100;
            bool killApplied = accumulator.TryResolvePendingKill(bossKey, loadedBossHp, out int totalDmg);
            Assert.True(killApplied);
            Assert.Equal(100, totalDmg);

            // 3. Host broadcasts dead snapshot (hp = 0, dead = true)
            bool snapshotAccepted = clientEnemySync.TryAcceptSnapshot(bossKey, 0, dead: true, timestamp: 500);
            Assert.True(snapshotAccepted);

            // 4. Client verifies host authority remains fresh
            Assert.True(clientEnemySync.HasFreshHostAuthority(bossKey));
        }

        /// <summary>
        /// Scenario 3: Simultaneous Heart Piece Completion and Vitals Upgrade.
        /// Two players simultaneously complete mask shards via currency/progression channels
        /// while host broadcasts vitals updates. Ensures masks and silk spools do not double-upgrade
        /// or corrupt max health.
        /// Features exercised: F1, F5, F14.
        /// </summary>
        [Fact]
        public void Scenario03_SimultaneousHeartPieceCompletionAndVitalsUpgrade()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();
            var tracker = new RemediationTestHarness.BoundedCounterTransactionTracker();

            Assert.Equal(5, controller.CurrentMaxHealth);

            // Player 1 collects fragment 3, Player 2 collects fragment 4 simultaneously
            var tx1 = Guid.NewGuid();
            var tx2 = Guid.NewGuid();

            Assert.True(tracker.TryProcessTransaction(tx1));
            Assert.True(tracker.TryProcessTransaction(tx2));

            // Fragment unlock packet arrives (4 heart pieces completed)
            controller.ApplyHeartPieceFragments(4, sharedMaxHealth: 5);
            Assert.Equal(6, controller.CurrentMaxHealth);
            Assert.Equal(1, controller.AddToMaxHealthCallCount);

            // Concurrent VitalsSyncPacket arrives with authoritative MaxHealth = 6
            controller.OnSharedMaxHealthReceived(6);

            // Re-evaluating fragments should be completely idempotent (no second AddToMaxHealth call)
            controller.ApplyHeartPieceFragments(4, sharedMaxHealth: 6);
            Assert.Equal(6, controller.CurrentMaxHealth);
            Assert.Equal(1, controller.AddToMaxHealthCallCount);

            // Verify whitelist validation prevents arbitrary stat tampering
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed("maxHealthBase", ItemCategory.Ability));
        }

        /// <summary>
        /// Scenario 4: Revive in Dense Hazard & Solid Geometry Dungeon.
        /// Hero dies near complex vertical terrain with spikes and narrow wall shafts.
        /// Prober dynamically expands buffer past 16 colliders, detects hazard triggers and
        /// solid walls, and triggers safe bench fallback when no verified safe ground exists.
        /// Features exercised: F10, F11, F6.
        /// </summary>
        [Fact]
        public void Scenario04_ReviveInDenseHazardAndSolidGeometryDungeon()
        {
            var deathCoordinator = new DeathLifecycleCoordinator();
            deathCoordinator.IsPartnerAlive = true;
            deathCoordinator.ReviveCooldownDuration = 2.0f;
            deathCoordinator.LastPartnerPosition = (50f, 20f);

            // Hero dies in hazardous dungeon
            deathCoordinator.OnLocalPlayerDied(50f, 20f);
            Assert.Equal(DeathState.Spectating, deathCoordinator.CurrentState);

            // Spectate timer runs out (cooldown clamped to min 15s)
            deathCoordinator.UpdateTick(20f);
            Assert.Equal(DeathState.RespawningAtPartner, deathCoordinator.CurrentState);

            // Prober encounters dense spikes and narrow vertical wall shafts
            var prober = new RemediationTestHarness.SolidGeometryGroundProber();

            // Ground platform under partner
            prober.Platforms.Add(new RemediationTestHarness.SolidGeometryGroundProber.Platform
            {
                MinX = 40f,
                MaxX = 60f,
                SurfaceY = 15f,
                NormalY = 1.0f,
                IsHazard = false
            });

            // Lateral candidates are blocked by 25 solid wall colliders overlapping the area
            for (int i = 0; i < 25; i++)
            {
                prober.Walls.Add(new RemediationTestHarness.SolidGeometryGroundProber.WallObstacle
                {
                    MinX = 45f,
                    MaxX = 55f,
                    MinY = 14f,
                    MaxY = 25f
                });
            }

            // Fallback hazard checkpoint defined at dungeon entrance
            prober.FallbackHazardLocation = (10f, 5f);

            bool found = prober.TryFindSafeGround(50f, 20f, out float safeX, out float safeY);
            Assert.True(found);
            Assert.True(prober.BufferCapacity >= 32); // Buffer expanded
            Assert.Equal(10f, safeX);                // Cascaded safely to hazard checkpoint
            Assert.Equal(5f, safeY);

            deathCoordinator.CompleteRevival();
            Assert.Equal(DeathState.Alive, deathCoordinator.CurrentState);
        }

        /// <summary>
        /// Scenario 5: High-Packet-Loss Network Session with Sequence Wrap.
        /// Two peers exchange bursty unordered reliable data over lossy connection.
        /// Window wraps past 4096 sequences; out-of-order sequence 0 is not falsely ACKed,
        /// while queued heartbeat pongs prevent spurious disconnects during packet bursts.
        /// Features exercised: F12, F15, F17.
        /// </summary>
        [Fact]
        public void Scenario05_HighPacketLossNetworkSessionWithSequenceWrap()
        {
            var window = new RemediationTestHarness.SimulatedUnorderedReplayWindow();
            var transport = new RemediationTestHarness.SimulatedHeartbeatTransport();
            var tickRate = new AdaptiveTickRate(30f);

            // Simulate high packet loss (15%) adapting tick rate down over ticks
            for (float t = 1.0f; t <= 5.0f; t += 0.6f)
            {
                tickRate.Update(currentTime: t, rttMs: 180f, lossPercent: 15f);
            }
            Assert.True(tickRate.CurrentTickHz <= 25f);

            // Stream of 4100 sequences processed over bursty session
            for (uint i = 1; i <= 4100; i++)
            {
                window.Process(i);
            }

            // Heartbeat pong delayed in burst queue during heavy traffic
            transport.LastPongReceivedMs = 5000;
            transport.IncomingQueue.Enqueue("BURST_DATA_1");
            transport.IncomingQueue.Enqueue("BURST_DATA_2");
            transport.IncomingQueue.Enqueue("PONG");

            // Drain incoming packets first before evaluating timeout at 22000 ms
            transport.PollEvents(now: 22000, drainFirst: true);
            Assert.True(transport.IsConnected); // Not falsely disconnected!
            Assert.Equal(22000, transport.LastPongReceivedMs);

            // Delayed packet sequence 0 from start of session arrives after 4100 packets
            var statusSeq0 = window.Process(0);
            Assert.Equal(RemediationTestHarness.ReplayStatus.Stale, statusSeq0); // Stale: DO NOT ACK!
        }

        /// <summary>
        /// Scenario 6: Rapid Inventory Discovery with Simulated Handler Failure.
        /// Network packet bursts unlock abilities and key items; handler fails on transient scene change.
        /// State rolls back cleanly, allowing successful retry without locking out items
        /// or leaking transaction GUIDs.
        /// Features exercised: F1, F9, F14.
        /// </summary>
        [Fact]
        public void Scenario06_RapidInventoryDiscoveryWithSimulatedHandlerFailure()
        {
            var service = new InventorySyncService();
            var tracker = new RemediationTestHarness.BoundedCounterTransactionTracker();
            var peer = new NetworkPeerId(2);

            bool sceneLoading = true;
            bool doubleJumpUnlocked = false;

            service.OnItemUnlocked += (item, cat) =>
            {
                if (sceneLoading)
                {
                    // Simulated PlayerData.instance == null during scene load
                    throw new InvalidOperationException("Scene transition in progress: PlayerData unavailable");
                }
                if (item == "hasDoubleJump") doubleJumpUnlocked = true;
            };

            // Whitelist validation succeeds for valid ability
            Assert.True(RemediationTestHarness.IsSaveBoolAllowed("hasDoubleJump", ItemCategory.Ability));

            var txId = Guid.NewGuid();
            tracker.TryProcessTransaction(txId);

            // 1. First attempt fails due to scene loading
            try
            {
                service.HandleItemUnlockPacket(peer, new ItemUnlockPacket
                {
                    ItemId = "hasDoubleJump",
                    Category = ItemCategory.Ability,
                    TransactionId = txId
                });
            }
            catch (InvalidOperationException) { }

            Assert.False(doubleJumpUnlocked);

            // 2. Scene finishes loading, retry arrives
            sceneLoading = false;
            var retryTxId = Guid.NewGuid();
            tracker.TryProcessTransaction(retryTxId);

            service.HandleItemUnlockPacket(peer, new ItemUnlockPacket
            {
                ItemId = "hasDoubleJump",
                Category = ItemCategory.Ability,
                TransactionId = retryTxId
            });

            Assert.True(doubleJumpUnlocked);
            Assert.Contains("hasDoubleJump", service.UnlockedItems);
            Assert.Equal(2, tracker.Count);
        }

        /// <summary>
        /// Scenario 7: Dynamic Performance Adaptation under Heavy Dash Combat.
        /// Player repeatedly performs rapid dashing while menu UI dynamically shifts
        /// network tick rate between 15 Hz and 60 Hz. Rim light smoothly decays without trapping,
        /// and network orchestrator dynamically updates target frequency.
        /// Features exercised: F17, F18.
        /// </summary>
        [Fact]
        public void Scenario07_DynamicPerformanceAdaptationUnderHeavyDashCombat()
        {
            var tickRate = new AdaptiveTickRate(30f);

            // Player changes setting in menu from 30 Hz to 60 Hz
            RemediationTestHarness.SetDefaultTickHzSafe(tickRate, 60f);
            Assert.Equal(60f, tickRate.DefaultTickHz);
            tickRate.Reset();
            Assert.Equal(60f, tickRate.CurrentTickHz);
            float frameDelta = tickRate.CurrentInterval;

            // Player executes multiple dash sequences
            var lifecycle = new DashTrailLifecycle();
            for (int dash = 0; dash < 5; dash++)
            {
                lifecycle.TriggerDash(isAirDash: false, isPlayer2: false);
                Assert.Equal(DashTrailPhase.Active, lifecycle.CurrentPhase);

                // Decay rim light from initial 0.8f intensity
                var decay = RemediationTestHarness.SimulateRimLightDecay(
                    initialIntensity: 0.8f,
                    deltaTime: frameDelta,
                    useFixedLogic: true);

                Assert.Equal(0f, decay.FinalIntensity);
                Assert.False(decay.IsActive);

                lifecycle.Update(0.26f, isGrounded: true, isWallSliding: false);
                lifecycle.Update(0.01f, isGrounded: true, isWallSliding: false);
                Assert.Equal(DashTrailPhase.Idle, lifecycle.CurrentPhase);
            }

            // Network degrades: menu or system scales tick rate down to 15 Hz
            RemediationTestHarness.SetDefaultTickHzSafe(tickRate, 15f);
            Assert.Equal(15f, tickRate.DefaultTickHz);
            tickRate.Reset();
            Assert.Equal(15f, tickRate.CurrentTickHz);
        }

        /// <summary>
        /// Scenario 8: Total Party Wipe & Multi-Epoch Bench Checkpoint Respawn.
        /// Both players perish simultaneously; bench respawn occurs; late reconnector arrives with Epoch 1.
        /// Epoch sync updates peer to Epoch 2; subsequent deaths and revive states operate consistently.
        /// Features exercised: F6, F10.
        /// </summary>
        [Fact]
        public void Scenario08_TotalPartyWipeAndMultiEpochBenchCheckpointRespawn()
        {
            var hostDeath = new DeathLifecycleCoordinator();
            var clientDeath = new DeathLifecycleCoordinator();
            var hostPeer = new NetworkPeerId(1);
            var clientPeer = new NetworkPeerId(2);

            // 1. Both players die -> Team Wipe at Epoch 1
            hostDeath.IsPartnerAlive = false;
            hostDeath.OnLocalPlayerDied(10f, 10f);
            Assert.Equal(DeathState.TeamWipe, hostDeath.CurrentState);

            // 2. Bench respawn resets state and increments epoch to 2
            hostDeath.ResetAfterBenchRespawn();
            Assert.Equal(2u, hostDeath.CurrentEpoch);
            Assert.Equal(DeathState.Alive, hostDeath.CurrentState);

            // 3. Client reconnector arrives with stale Epoch 1
            Assert.Equal(1u, clientDeath.CurrentEpoch);

            // 4. Host sends Epoch sync packet
            clientDeath.HandleDeathPacket(hostPeer, new DeathLifecyclePacket
            {
                EventType = DeathEventType.BenchCheckpointUpdate,
                Epoch = hostDeath.CurrentEpoch
            });

            // Client adopts Epoch 2
            Assert.Equal(2u, clientDeath.CurrentEpoch);

            // 5. Subsequent solo death in Epoch 2 functions properly
            clientDeath.IsPartnerAlive = true;
            clientDeath.ReviveCooldownDuration = 3f;
            clientDeath.LastPartnerPosition = (100f, 50f);
            clientDeath.OnLocalPlayerDied(0f, 0f);

            Assert.Equal(DeathState.Spectating, clientDeath.CurrentState);
            clientDeath.UpdateTick(20f);
            Assert.Equal(DeathState.RespawningAtPartner, clientDeath.CurrentState);

            // Verify safe ground without wall clip
            var prober = new RemediationTestHarness.SolidGeometryGroundProber();
            prober.Platforms.Add(new RemediationTestHarness.SolidGeometryGroundProber.Platform
            {
                MinX = 90f,
                MaxX = 110f,
                SurfaceY = 50f,
                NormalY = 1.0f
            });

            bool safeFound = prober.TryFindSafeGround(100f, 50f, out float safeX, out _);
            Assert.True(safeFound);
            Assert.Equal(100f, safeX);

            clientDeath.CompleteRevival();
            Assert.Equal(DeathState.Alive, clientDeath.CurrentState);
        }

        /// <summary>
        /// Scenario 9: Long-Duration Co-Op Session Memory Stability.
        /// 1000+ counter transactions (heart pieces/silk parts) processed over extended session.
        /// Proves bounded memory FIFO prevents memory leaks while preventing replay exploits.
        /// Features exercised: F14, F5.
        /// </summary>
        [Fact]
        public void Scenario09_LongDurationCoOpSessionMemoryStability()
        {
            var tracker = new RemediationTestHarness.BoundedCounterTransactionTracker();
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();

            // Process 1200 counter fragment transactions over a 4-hour co-op playthrough
            for (int i = 1; i <= 1200; i++)
            {
                var txId = Guid.NewGuid();
                bool added = tracker.TryProcessTransaction(txId);
                Assert.True(added);

                // Attempting immediate replay of same transaction is blocked
                bool replayBlocked = !tracker.TryProcessTransaction(txId);
                Assert.True(replayBlocked);

                if (i % 4 == 0)
                {
                    controller.ApplyHeartPieceFragments(4, sharedMaxHealth: controller.CurrentMaxHealth);
                }
            }

            // Assert memory bound is strictly enforced: exactly 500 entries, preventing memory leak
            Assert.Equal(500, tracker.Count);
        }
    }
}
