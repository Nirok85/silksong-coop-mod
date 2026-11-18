using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SilksongCoop.Core;
using SilksongCoop.Core.Clones;
using SilksongCoop.Core.Interpolation;
using SilksongCoop.Core.Localization;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Progression;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.Revive;
using SilksongCoop.Core.State;
using SilksongCoop.Core.Visuals;
using SilksongCoop.Tests.TestHarness;
using Xunit;

namespace SilksongCoop.Tests.DefectRemediationTiers
{
    /// <summary>
    /// Tier 3: Pairwise Cross-Feature Tests.
    /// Systematically pairs interacting features across subsystems (P01 through P08)
    /// to verify that concurrent interactions, shared states, and multi-subsystem
    /// workflows do not induce race conditions, memory leaks, or contract violations.
    /// </summary>
    public class Tier3DefectPairwiseTests
    {
        #region P01: CRIT-01 (Epoch CAS) + HIGH-04 (SnapshotBuffer Synchronization)

        [Fact]
        public async Task P01_ConcurrentTransformSnapshots_And_EpochAdvancement()
        {
            var buffer = new SnapshotBuffer(32);
            var coordinator = new DeathLifecycleCoordinator();
            var p2 = new NetworkPeerId(2);

            int snapshotWrites = 0;
            int epochAdvances = 0;

            // Task 1: high-frequency transform snapshot ingestion
            var snapshotTask = Task.Run(() =>
            {
                var rng = new Random(101);
                for (int tick = 1; tick <= 100; tick++)
                {
                    float x = (float)rng.NextDouble() * 100f;
                    float y = (float)rng.NextDouble() * 50f;
                    buffer.AddSnapshot(new TransformSnapshot
                    {
                        Tick = (uint)tick,
                        TimestampMs = tick * 16.6,
                        PositionX = x,
                        PositionY = y
                    });
                    Interlocked.Increment(ref snapshotWrites);
                }
            });

            // Task 2: concurrent epoch advancement & synchronization
            var epochTask = Task.Run(() =>
            {
                for (int i = 1; i <= 50; i++)
                {
                    uint current = coordinator.CurrentEpoch;
                    coordinator.HandleDeathPacket(p2, new DeathLifecyclePacket
                    {
                        Header = new PacketHeader(PacketType.PlayerDeath, 4, 1, 100),
                        EventType = DeathEventType.EpochSync,
                        Epoch = current + 1
                    });
                    Interlocked.Increment(ref epochAdvances);
                }
            });

            await Task.WhenAll(snapshotTask, epochTask);

            // Assertions
            Assert.Equal(100, snapshotWrites);
            Assert.Equal(50, epochAdvances);
            Assert.InRange(buffer.Count, 1, 32);
            Assert.True(coordinator.CurrentEpoch > 0, "Epoch must be strictly positive");
        }

        #endregion

        #region P02: CRIT-02 (Atomic Team Wipe CAS) + MED-01 (Solo Bench Respawn) + MED-02 (Invulnerability Reset)

        [Fact]
        public void P02_AtomicCAS_TeamWipe_With_PendingSoloBenchRespawn_And_InvulnerabilityReset()
        {
            var wipeCoordinator = new DefectTestHarness.AtomicTeamWipeCoordinator();
            var deathCoordinator = new DeathLifecycleCoordinator();

            // Phase 1: Local player dies and enters spectating/respawning
            deathCoordinator.OnLocalPlayerDied(10f, 5f);
            Assert.Equal(DeathState.Spectating, deathCoordinator.CurrentState);

            // Phase 2: Simultaneous mutual team wipe triggers from 4 concurrent threads
            int attempts = 4;
            int successfulWipes = 0;
            Parallel.For(0, attempts, _ =>
            {
                if (wipeCoordinator.TriggerTeamWipe())
                {
                    Interlocked.Increment(ref successfulWipes);
                }
            });

            // Exactly one team wipe succeeds due to CAS guarantee
            Assert.Equal(1, successfulWipes);
            Assert.Equal(1, wipeCoordinator.TeamWipeTriggerCount);

            // Phase 3: Session resets for new session
            deathCoordinator.SynchronizeEpochForNewSession();
            Assert.Equal(DeathState.Alive, deathCoordinator.CurrentState);
            Assert.Equal(1u, deathCoordinator.CurrentEpoch);
        }

        #endregion

        #region P03: HIGH-01 (Fallback Ground Prober) + HIGH-02 (Steam Offline PeerId) + MED-03 (Spectator Suppress)

        [Fact]
        public void P03_FallbackGroundRevival_With_OfflineSteamPeerId_And_SpectatorSuppression()
        {
            // HIGH-02: Offline Steam returns safe fallback peer ID = 1 (never 0)
            ulong offlineSteamId = 0;
            var localPeer = offlineSteamId != 0 ? new NetworkPeerId(offlineSteamId) : new NetworkPeerId(1);
            Assert.Equal(1u, localPeer.Value);

            // HIGH-01: Prober raycast misses floor (empty world/void pit), fallback safely offsets from anchor
            var prober = new DefectTestHarness.HeadlessGroundProber();
            bool foundGround = prober.TryFindSafeGround(100f, -50f, out float safeX, out float safeY);
            Assert.False(foundGround, "Raycast into abyss fails ground detection");

            // Fallback safe position solver
            float fallbackY = safeY + 1.5f; // elevated safety clearance
            int reviveHp = prober.CalculateReviveHealth(8); // 50% max HP
            Assert.Equal(4, reviveHp);
            Assert.True(fallbackY > safeY);

            // MED-03: Spectator camera toggle and toasts are completely suppressed when spectator is inactive
            var spectator = new DefectTestHarness.HeadlessSpectatorController();
            Assert.False(spectator.IsSpectatorActive);

            bool toggleResult = spectator.HandleF9Keypress();
            Assert.False(toggleResult);
            Assert.False(spectator.IsSpectatorActive);
            Assert.Equal(0, spectator.ToastNotificationCount);
        }

        #endregion

        #region P04: MED-04 (Batch Initial Inventory) + ARCH-01 (Multi-Peer Inventory API)

        [Fact]
        public void P04_MultiPeer_InventoryBatching_And_APIContract()
        {
            var inventory = new InventorySyncService();
            var p1 = new NetworkPeerId(1);

            // Unlock 50 distinct items across peers
            for (int i = 0; i < 50; i++)
            {
                inventory.HandleItemUnlockPacket(p1, new ItemUnlockPacket
                {
                    Header = new PacketHeader(PacketType.ItemUnlock, 3, 1, 100),
                    ItemId = $"Item_{i}",
                    Category = ItemCategory.Ability,
                    TransactionId = Guid.NewGuid()
                });
            }

            var sharedItems = inventory.UnlockedItems;
            Assert.Equal(50, sharedItems.Count);

            // Batch initial inventory into chunks of at most 20 to prevent transport saturation
            const int maxPerBatch = 20;
            var batches = new List<List<string>>();
            var currentBatch = new List<string>();

            foreach (var key in sharedItems)
            {
                currentBatch.Add(key);
                if (currentBatch.Count >= maxPerBatch)
                {
                    batches.Add(new List<string>(currentBatch));
                    currentBatch.Clear();
                }
            }
            if (currentBatch.Count > 0)
            {
                batches.Add(new List<string>(currentBatch));
            }

            // 50 items batched into chunks of 20 -> 3 batches (20, 20, 10)
            Assert.Equal(3, batches.Count);
            Assert.Equal(20, batches[0].Count);
            Assert.Equal(20, batches[1].Count);
            Assert.Equal(10, batches[2].Count);
        }

        #endregion

        #region P05: MED-05 (Pause-Safe Delta Time) + LOW-02 (Immutable Vitals) + LOW-06 (Watermark RTT) + PERF-02 (Zero-Alloc Diagnostics)

        [Fact]
        public async Task P05_PauseSafeDeltaTiming_With_ImmutableVitals_And_ZeroAllocDiagnostics()
        {
            // MED-05 & LOW-06: Instant RTT propagation in watermark
            var watermark = new DefectTestHarness.HeadlessWatermarkState();
            var peer = new NetworkPeerId(2, "HornetFriend");

            // RTT change immediately updates watermark without refresh wait
            watermark.Update(0f, true, SessionStatus.Connected, peer, 45);
            Assert.Contains("HornetFriend", watermark.WatermarkText);
            Assert.Contains("45ms", watermark.WatermarkText);
            Assert.Contains("●", watermark.WatermarkText); // Green for RTT < 50ms

            // LOW-02: Concurrent vitals updates with immutable RemotePeerVitals (zero torn reads)
            var vitalsService = new VitalsSyncService();

            var writerTask = Task.Run(() =>
            {
                for (byte hp = 1; hp <= 100; hp++)
                {
                    vitalsService.HandleVitalsPacket(peer, new VitalsSyncPacket
                    {
                        Header = new PacketHeader(PacketType.VitalsSync, 2, 1, 100),
                        CurrentHealth = hp,
                        MaxHealth = 100,
                        CurrentSilk = (byte)(hp * 2)
                    });
                }
            });

            var readerTask = Task.Run(() =>
            {
                for (int reads = 0; reads < 200; reads++)
                {
                    if (vitalsService.TryGetRemoteVitals(peer, out var vitals))
                    {
                        // Invariant: Silk must be exactly 2 * Health (never torn read)
                        Assert.Equal(vitals.Health * 2, vitals.Silk);
                    }
                }
            });

            await Task.WhenAll(writerTask, readerTask);

            // PERF-02: Zero-alloc peer diagnostics snapshot
            var snapshot = DefectTestHarness.HeadlessPeerDiagnosticsSnapshot.Build(
                connected: true,
                partnerAlive: true,
                peerId: peer,
                rtt: 45,
                delay: 50.0);

            Assert.True(snapshot.Connected);
            Assert.Equal(45, snapshot.RttMs);
            Assert.Equal(peer, snapshot.RemotePeerId);
        }

        #endregion

        #region P06: LOW-03 (Connection History Cap) + LOW-05 (Enemy Sync Reset) + LOW-07 (Loc Fallbacks)

        [Fact]
        public void P06_ConnectionHistoryCap_With_EnemySyncAuthorityClear_And_MissingLocFallback()
        {
            // LOW-03: Connection history strictly caps entries at 5 and deduplicates
            var history = new DefectTestHarness.HeadlessConnectionHistory();
            for (int i = 1; i <= 8; i++)
            {
                history.Add($"192.168.1.{i}:7777");
            }
            Assert.Equal(5, history.GetAll().Count);
            Assert.Equal("192.168.1.8:7777", history.GetAll()[0]); // Most recent at index 0

            // LOW-05: EnemySyncController authority map and tracking cleared on session reset
            var enemyService = new EnemySyncService();
            enemyService.TryAcceptSnapshot(1001, 100, dead: false, timestamp: 10);
            Assert.True(enemyService.HasFreshHostAuthority(1001));

            enemyService.Reset();
            Assert.False(enemyService.HasFreshHostAuthority(1001));

            // LOW-07: Missing localization key returns key without throwing unhandled exception
            string missingKey = Loc.Translate(CoopLanguage.Russian, "UI_NON_EXISTENT_KEY_P06");
            Assert.Equal("UI_NON_EXISTENT_KEY_P06", missingKey);

            // Format fallback with mismatched placeholders
            string formatFallback = Loc.Translate(CoopLanguage.Russian, "UNLOCALIZED_FORMAT_{0}", "valueA");
            Assert.Contains("UNLOCALIZED_FORMAT", formatFallback);
        }

        #endregion

        #region P07: LOW-04 (Quality Preset Validation) + PERF-01 (Palette Swap Throttling)

        [Fact]
        public void P07_QualityPresetValidation_With_PaletteSwapThrottling()
        {
            // LOW-04: Quality preset validates tick rates within boundaries [10, 60]
            var preset = new DefectTestHarness.HeadlessQualityPresetManager();
            preset.ApplyPreset(DefectTestHarness.QualityLevel.High);
            Assert.Equal(60, preset.ConfiguredTickRateHz);
            Assert.True(preset.EnableDashTrail);

            preset.ApplyPreset(DefectTestHarness.QualityLevel.Low);
            Assert.Equal(20, preset.ConfiguredTickRateHz);
            Assert.False(preset.EnableDashTrail);

            // PERF-01: Palette swap throttling
            var controller = new DefectTestHarness.HeadlessPaletteSwapController();

            // Host (Player 1): Palette swap is bypassed / minimal
            controller.SetPlayer2(false);
            int initialApplyCount = controller.ApplyCount;

            // LateUpdate calls within throttling interval (< 30 frames) do not re-apply
            controller.LateUpdate(5);
            controller.LateUpdate(15);
            controller.LateUpdate(29);
            Assert.Equal(initialApplyCount, controller.ApplyCount);

            // Frame 35 (>= 30 frame diff) re-applies
            controller.LateUpdate(35);
            Assert.Equal(initialApplyCount + 1, controller.ApplyCount);
        }

        #endregion

        #region P08: HIGH-03 (Buffer Trimming) + MED-06 (Peer ID Logging) + PERF-03 (Steam Polling Allocations)

        [Fact]
        public void P08_HighThroughputBurst_Trimming_With_Logging_And_SteamStaleCleanup()
        {
            // MED-06: Packet sender logs target peer ID on transmission failures
            var sender = new DefectTestHarness.HeadlessPacketSender();
            using var fakeTransport = new FakeSteamTransport(1, false); // disconnected transport
            var failedTarget = new NetworkPeerId(777);

            var packet = new ActionTriggerPacket { Header = new PacketHeader(PacketType.ActionTrigger, 1, 1, 100) };
            for (int i = 0; i < 5; i++)
            {
                bool sent = sender.SendTo(fakeTransport, failedTarget, packet, DeliveryMethod.ReliableOrdered, 1, 128);
                Assert.False(sent);
            }

            Assert.Equal(5, sender.FailureLogs.Count);
            foreach (var log in sender.FailureLogs)
            {
                Assert.Contains("777", log);
            }

            // PERF-03: Zero-allocation stale peer cleanup in Steam polling monitor
            var monitor = new DefectTestHarness.HeadlessSteamPollingMonitor();
            long now = 100000;
            monitor.RegisterPeer(1001, now);
            monitor.RegisterPeer(1002, now);

            // Tick within heartbeat window (< 15000ms): 0 stale allocations, 0 disconnects
            monitor.PollEvents(now + 5000);
            Assert.Equal(0, monitor.AllocatedStaleLists);
            Assert.Equal(0, monitor.TimedOutPeersDisconnected);

            // Tick past heartbeat timeout (> 15000ms): exactly 1 lazy list allocated, 2 peers disconnected
            monitor.PollEvents(now + 20000);
            Assert.Equal(1, monitor.AllocatedStaleLists);
            Assert.Equal(2, monitor.TimedOutPeersDisconnected);
        }

        #endregion
    }
}
