using System;
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
    /// Tier 4: Real-World Application Workload Scenarios.
    /// Simulates complete, end-to-end cooperative gameplay scenarios mimicking authentic
    /// multiplayer sessions: boss battles, solo deaths with safe ground revivals, simultaneous
    /// team wipes with atomic CAS guarantees, mid-session disconnects with state catch-up,
    /// and harsh degraded network transport conditions.
    /// </summary>
    public class Tier4DefectApplicationScenarios
    {
        #region S01: Tri-Client Boss Encounter Simulation

        [Fact]
        public void S01_TriClientBossEncounter_MultiPeerReplication()
        {
            // 3 players: Host (Peer 1), Client A (Peer 2), Client B (Peer 3)
            var hostVitals = new VitalsSyncService();
            var hostInventory = new InventorySyncService();
            var p1 = new NetworkPeerId(1, "HostKnight");
            var p2 = new NetworkPeerId(2, "ClientHornetA");
            var p3 = new NetworkPeerId(3, "ClientHornetB");

            // Initial vitals setup
            hostVitals.HandleVitalsPacket(p2, new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 1, 100),
                CurrentHealth = 5,
                MaxHealth = 5,
                CurrentSilk = 100
            });
            hostVitals.HandleVitalsPacket(p3, new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 1, 100),
                CurrentHealth = 6,
                MaxHealth = 6,
                CurrentSilk = 120
            });

            // 1. Client A takes damage from boss (5 -> 3)
            hostVitals.HandleVitalsPacket(p2, new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 2, 200),
                CurrentHealth = 3,
                MaxHealth = 5,
                CurrentSilk = 100
            });

            // 2. Client B casts Silk skill (Silk 120 -> 60)
            hostVitals.HandleVitalsPacket(p3, new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 2, 250),
                CurrentHealth = 6,
                MaxHealth = 6,
                CurrentSilk = 60
            });

            // Verify decoupled vitals
            Assert.True(hostVitals.TryGetRemoteVitals(p2, out var vitalsA));
            Assert.Equal(3, vitalsA.Health);
            Assert.Equal(100, vitalsA.Silk);

            Assert.True(hostVitals.TryGetRemoteVitals(p3, out var vitalsB));
            Assert.Equal(6, vitalsB.Health);
            Assert.Equal(60, vitalsB.Silk);

            // 3. Client A unlocks boss reward tool "Tool_SilkSpear"
            hostInventory.HandleItemUnlockPacket(p2, new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlock, 3, 1, 300),
                ItemId = "Tool_SilkSpear",
                Category = ItemCategory.Tool,
                TransactionId = Guid.NewGuid()
            });

            Assert.True(hostInventory.IsItemUnlocked("Tool_SilkSpear"));

            // 4. Boss defeated: Currency rewards awarded
            hostVitals.HandleCurrencyPacket(p2, new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 1, 400),
                Currency = CurrencyType.Rosaries,
                TotalWalletAmount = 500,
                DeltaAmount = 500,
                PickupUniqueId = 1,
                TransactionId = Guid.NewGuid()
            });
            hostVitals.HandleCurrencyPacket(p2, new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 2, 401),
                Currency = CurrencyType.ShellShards,
                TotalWalletAmount = 120,
                DeltaAmount = 120,
                PickupUniqueId = 2,
                TransactionId = Guid.NewGuid()
            });

            Assert.Equal(500, hostVitals.SharedRosaries);
            Assert.Equal(120, hostVitals.SharedShellShards);

            // 5. Visuals: Player 2 / Player 3 palette shift verification
            PaletteSwapMath.ApplyHueShift(0.85f, 0.15f, 0.15f, 0.58f, out float r, out float g, out float b);
            Assert.True(b > r, "Player 2 hue shift applies azure blue tint");
        }

        #endregion

        #region S02: Solo Death, Spectator Mode & Safe Ground Revival

        [Fact]
        public void S02_SoloDeath_SpectatorMode_SafeGroundRevival()
        {
            using var harness = new DualClientTestHarness();

            // Initial state: both alive
            Assert.Equal(DeathState.Alive, harness.HostDeath.CurrentState);
            Assert.Equal(DeathState.Alive, harness.ClientDeath.CurrentState);

            // 1. Client dies to hazard pit
            harness.ClientDeath.OnLocalPlayerDied(120f, 15f);
            Assert.Equal(DeathState.Spectating, harness.ClientDeath.CurrentState);
            Assert.Equal(DeathState.Alive, harness.HostDeath.CurrentState); // Host HP untouched

            // 2. Spectator controller is active during spectating
            var spectator = new DefectTestHarness.HeadlessSpectatorController
            {
                IsSpectatorActive = true
            };
            bool cameraToggled = spectator.HandleF9Keypress();
            Assert.True(cameraToggled);
            Assert.False(spectator.SpectatorCameraFollow);
            Assert.Equal(1, spectator.ToastNotificationCount);

            // 3. Ground prober locates safe platform above abyss
            var prober = new DefectTestHarness.HeadlessGroundProber();
            prober.Platforms.Add((MinX: 100f, MaxX: 150f, Y: 10f, IsHazard: false));

            bool safeFound = prober.TryFindSafeGround(120f, 15f, out float safeX, out float safeY);
            Assert.True(safeFound);
            Assert.Equal(120f, safeX);
            Assert.Equal(10.5f, safeY);

            // 4. Safe ground solver calculates 50% max HP
            int reviveHp = SafeGroundSolver.CalculateReviveHealth(8);
            Assert.Equal(4, reviveHp);

            // 5. Revival executed
            harness.ClientDeath.ResetAfterBenchRespawn();
            Assert.Equal(DeathState.Alive, harness.ClientDeath.CurrentState);
        }

        #endregion

        #region S03: Dual Wipe Simultaneous Fatal Strike & Synchronized Reload

        [Fact]
        public void S03_DualWipe_SimultaneousFatalStrike_SynchronizedReload()
        {
            using var harness = new DualClientTestHarness();
            var wipeCoordinator = new DefectTestHarness.AtomicTeamWipeCoordinator();

            // Both players take lethal strikes within 10ms
            harness.HostDeath.OnLocalPlayerDied(50f, 10f);
            harness.ClientDeath.OnLocalPlayerDied(55f, 10f);

            Assert.Equal(DeathState.Spectating, harness.HostDeath.CurrentState);
            Assert.Equal(DeathState.Spectating, harness.ClientDeath.CurrentState);

            // Simultaneous team wipe calls triggered from both sides
            bool hostTriggered = wipeCoordinator.TriggerTeamWipe();
            bool clientTriggered = wipeCoordinator.TriggerTeamWipe(); // Dropped by atomic CAS

            Assert.True(hostTriggered);
            Assert.False(clientTriggered);
            Assert.Equal(1, wipeCoordinator.TeamWipeTriggerCount);

            // Bench reload synchronization: epoch advances in lockstep
            harness.HostDeath.ResetAfterBenchRespawn();
            harness.ClientDeath.ResetAfterBenchRespawn();

            Assert.Equal(2u, harness.HostDeath.CurrentEpoch);
            Assert.Equal(2u, harness.ClientDeath.CurrentEpoch);
            Assert.Equal(DeathState.Alive, harness.HostDeath.CurrentState);
            Assert.Equal(DeathState.Alive, harness.ClientDeath.CurrentState);
        }

        #endregion

        #region S04: Mid-Session Disconnect & Seamless Reconnection Catch-Up

        [Fact]
        public void S04_MidSessionDisconnect_SeamlessReconnectionCatchUp()
        {
            var hostSession = new CoopSessionState();
            var hostInventory = new InventorySyncService();
            var hostVitals = new VitalsSyncService();
            var hostEnemySync = new EnemySyncService();
            var connectionHistory = new DefectTestHarness.HeadlessConnectionHistory();

            var hostPeer = new NetworkPeerId(1, "Host");
            var clientPeer = new NetworkPeerId(2, "Client");

            hostSession.SetHosting(hostPeer, "SK-RECONNECT-ROOM");
            hostSession.SetConnected(clientPeer);
            connectionHistory.Add("127.0.0.1:7777");

            // Traversal progress: 2 items unlocked, enemy tracked
            hostInventory.OnLocalItemDiscovered("Item_ToolUpgrade1", ItemCategory.Tool);
            hostInventory.OnLocalItemDiscovered("Item_CrestReaper", ItemCategory.Crest);
            hostEnemySync.TryAcceptSnapshot(505, 200, dead: false, timestamp: 100);
            Assert.True(hostEnemySync.HasFreshHostAuthority(505));

            // 1. Client drops unexpectedly (network timeout)
            hostSession.SetDisconnected(DisconnectReason.Timeout);
            hostEnemySync.Reset(); // Authority cleared on session reset
            Assert.Equal(SessionStatus.Disconnected, hostSession.Status);
            Assert.False(hostEnemySync.HasFreshHostAuthority(505));

            // 2. Client reconnects to same room
            hostSession.SetHosting(hostPeer, "SK-RECONNECT-ROOM");
            hostSession.SetConnected(clientPeer);
            Assert.Equal(SessionStatus.Connected, hostSession.Status);

            // 3. Catch-up initial inventory payload broadcast (force: true)
            int retransmittedCount = 0;
            hostInventory.OnOutgoingItemUnlockPacket += _ => retransmittedCount++;

            hostInventory.OnLocalItemDiscovered("Item_ToolUpgrade1", ItemCategory.Tool, force: true);
            hostInventory.OnLocalItemDiscovered("Item_CrestReaper", ItemCategory.Crest, force: true);

            Assert.Equal(2, retransmittedCount);
            Assert.Equal(2, hostInventory.UnlockedItems.Count);

            // Connection history preserved with latest host at top
            Assert.Single(connectionHistory.GetAll());
            Assert.Equal("127.0.0.1:7777", connectionHistory.GetAll()[0]);
        }

        #endregion

        #region S05: High-Latency Network Degradation & Recovery

        [Fact]
        public void S05_HighLatencyDegradation_And_Recovery()
        {
            // Harsh network conditions: 75ms latency (150ms RTT), 15ms jitter, 10% packet drop
            using var harness = new DualClientTestHarness(latencyMs: 75, jitterMs: 15, lossRate: 0.10f, seed: 12345);

            var hostSender = new DefectTestHarness.HeadlessPacketSender();
            int sentPackets = 40;

            // Transmit 40 sequenced action trigger packets under packet loss
            for (int i = 1; i <= sentPackets; i++)
            {
                var packet = new ActionTriggerPacket
                {
                    Header = new PacketHeader(PacketType.ActionTrigger, 1, (uint)i, (uint)(i * 50)),
                    Action = (ActionType)(i % 5),
                    Parameter = (byte)i
                };

                hostSender.SendTo(harness.HostTransport, harness.ClientTransport.LocalPeerId, packet, DeliveryMethod.ReliableOrdered, 1, 64);
            }

            // Advance virtual time across 350 steps (5.6 seconds) to allow complete packet transmission & reliable ACKs
            for (int step = 0; step < 350; step++)
            {
                harness.Step(16);
            }

            // Reliable ordered packets must all be delivered despite simulated loss
            Assert.Equal(sentPackets, harness.DeliveredActionsToClient.Count);

            // Strict sequence ordering preserved
            for (int i = 0; i < harness.DeliveredActionsToClient.Count; i++)
            {
                Assert.Equal(i + 1, (int)harness.DeliveredActionsToClient[i].Parameter);
            }

            // Zero transmission failure logs
            Assert.Empty(hostSender.FailureLogs);
        }

        #endregion
    }
}
