using System;
using System.Collections.Generic;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using SilksongCoop.Tests.TestHarness;
using Xunit;

namespace SilksongCoop.Tests
{
    public class Milestone4AdversarialStressTests
    {
        #region Area 1: Simultaneous Mutual Deaths

        [Theory]
        [InlineData(0, 0)]
        [InlineData(20, 5)]
        [InlineData(50, 15)]
        [InlineData(100, 30)]
        public void Area1_SimultaneousMutualDeaths_BothReachTeamWipe_ImmediatelyWithoutSpectatorFreeze(int latencyMs, int jitterMs)
        {
            using var harness = new DualClientTestHarness(latencyMs: latencyMs, jitterMs: jitterMs);
            harness.HostDeath.ReviveCooldownDuration = 25.0f;
            harness.ClientDeath.ReviveCooldownDuration = 25.0f;

            int hostWipeCount = 0;
            int clientWipeCount = 0;
            harness.HostDeath.OnTeamWipeTriggered += () => hostWipeCount++;
            harness.ClientDeath.OnTeamWipeTriggered += () => clientWipeCount++;

            // Both die on the exact same frame/tick
            bool hostSuppressed = harness.HostDeath.OnLocalPlayerDied(10f, 20f);
            bool clientSuppressed = harness.ClientDeath.OnLocalPlayerDied(30f, 40f);

            Assert.True(hostSuppressed, "Host must suppress bench reload on initial solo death.");
            Assert.True(clientSuppressed, "Client must suppress bench reload on initial solo death.");
            Assert.Equal(DeathState.Spectating, harness.HostDeath.CurrentState);
            Assert.Equal(DeathState.Spectating, harness.ClientDeath.CurrentState);

            // Step time forward just enough for packets to cross (e.g. 2x latency + jitter + margins), NOT 25s!
            int maxTransitTimeMs = Math.Max(50, (latencyMs + jitterMs) * 3 + 50);
            harness.AdvanceTime(maxTransitTimeMs);

            // Both must be in TeamWipe
            Assert.Equal(DeathState.TeamWipe, harness.HostDeath.CurrentState);
            Assert.Equal(DeathState.TeamWipe, harness.ClientDeath.CurrentState);

            // Crucial: Spectator time must NOT have had to expire!
            Assert.True(harness.HostDeath.RemainingSpectatorTime > 20.0f,
                $"Host remaining spectator time ({harness.HostDeath.RemainingSpectatorTime}) should be near 25s, not expired.");
            Assert.True(harness.ClientDeath.RemainingSpectatorTime > 20.0f,
                $"Client remaining spectator time ({harness.ClientDeath.RemainingSpectatorTime}) should be near 25s, not expired.");

            // Both must have triggered team wipe
            Assert.True(hostWipeCount >= 1, "Host must have triggered team wipe.");
            Assert.True(clientWipeCount >= 1, "Client must have triggered team wipe.");
        }

        [Fact]
        public void Area1_SimultaneousMutualDeaths_TeamWipeTriggered_ExactlyOncePerPeer()
        {
            using var harness = new DualClientTestHarness(latencyMs: 20, jitterMs: 0);

            int hostWipeCount = 0;
            int clientWipeCount = 0;
            harness.HostDeath.OnTeamWipeTriggered += () => hostWipeCount++;
            harness.ClientDeath.OnTeamWipeTriggered += () => clientWipeCount++;

            // Both die simultaneously
            harness.HostDeath.OnLocalPlayerDied(10f, 10f);
            harness.ClientDeath.OnLocalPlayerDied(20f, 20f);

            // Allow all cross-traffic to settle
            harness.DeliverAll(100);

            // Team wipe event must fire EXACTLY ONCE per client!
            Assert.Equal(1, hostWipeCount);
            Assert.Equal(1, clientWipeCount);
        }

        #endregion

        #region Area 2: Rapid Succession Deaths

        [Fact]
        public void Area2_RapidSuccessionDeaths_PlayerBDies50msLater_BeforePacketArrives_BothTransitionToTeamWipe()
        {
            // Latency = 100ms, so Player B dies 50ms later, before Player A's packet arrives
            using var harness = new DualClientTestHarness(latencyMs: 100, jitterMs: 0);

            int hostWipeCount = 0;
            int clientWipeCount = 0;
            harness.HostDeath.OnTeamWipeTriggered += () => hostWipeCount++;
            harness.ClientDeath.OnTeamWipeTriggered += () => clientWipeCount++;

            // Host dies at t = 0ms
            harness.HostDeath.OnLocalPlayerDied(10f, 10f);
            Assert.Equal(DeathState.Spectating, harness.HostDeath.CurrentState);

            // Advance 50ms
            harness.AdvanceTime(50);
            Assert.Equal(DeathState.Alive, harness.ClientDeath.CurrentState);

            // Client dies at t = 50ms (before Host packet arrives at t=100ms)
            harness.ClientDeath.OnLocalPlayerDied(20f, 20f);
            Assert.Equal(DeathState.Spectating, harness.ClientDeath.CurrentState);

            // Advance another 150ms to deliver all packets
            harness.AdvanceTime(150);

            Assert.Equal(DeathState.TeamWipe, harness.HostDeath.CurrentState);
            Assert.Equal(DeathState.TeamWipe, harness.ClientDeath.CurrentState);
            Assert.True(hostWipeCount >= 1, "Host must trigger TeamWipe.");
            Assert.True(clientWipeCount >= 1, "Client must trigger TeamWipe.");
        }

        [Fact]
        public void Area2_RapidSuccessionDeaths_PlayerBDies50msLater_AfterPacketArrives_BothTransitionToTeamWipe()
        {
            // Latency = 20ms, so Player A's death packet arrives at t=20ms, before Player B dies at t=50ms
            using var harness = new DualClientTestHarness(latencyMs: 20, jitterMs: 0);

            int hostWipeCount = 0;
            int clientWipeCount = 0;
            harness.HostDeath.OnTeamWipeTriggered += () => hostWipeCount++;
            harness.ClientDeath.OnTeamWipeTriggered += () => clientWipeCount++;

            // Host dies at t = 0ms
            harness.HostDeath.OnLocalPlayerDied(10f, 10f);

            // Advance 30ms (Host packet delivered to Client)
            harness.AdvanceTime(30);
            Assert.False(harness.ClientDeath.IsPartnerAlive);
            Assert.Equal(DeathState.Alive, harness.ClientDeath.CurrentState);

            // Client dies at t = 50ms (knowing partner is already dead)
            bool clientSuppressed = harness.ClientDeath.OnLocalPlayerDied(20f, 20f);

            // Client dies while partner dead -> direct TeamWipe, no bench suppression
            Assert.False(clientSuppressed, "Bench reload should not be suppressed when partner is already dead.");
            Assert.Equal(DeathState.TeamWipe, harness.ClientDeath.CurrentState);

            // Advance another 50ms so Client's wipe packet reaches Host
            harness.AdvanceTime(50);

            Assert.Equal(DeathState.TeamWipe, harness.HostDeath.CurrentState);
            Assert.True(hostWipeCount >= 1);
            Assert.True(clientWipeCount >= 1);
        }

        #endregion

        #region Area 3: Packet Interleaving & Reordering on Channel 4

        [Fact]
        public void Area3_Channel4_InterleavedWithChannel0Transforms_UnderHighJitterAndLoss_PreservesOrder()
        {
            using var harness = new DualClientTestHarness(latencyMs: 50, jitterMs: 30, lossRate: 0.5f, seed: 12345);

            var deliveredDeathTypes = new List<DeathEventType>();
            harness.ClientTransport.OnDataReceived += (peer, data, offset, length) =>
            {
                var span = new ReadOnlySpan<byte>(data, offset, length);
                if (PacketHeader.TryReadFrom(span, out var header) && header.PacketId == PacketType.PlayerDeath)
                {
                    var reader = new PacketReader(span);
                    var death = DeathLifecyclePacket.Deserialize(ref reader);
                    deliveredDeathTypes.Add(death.EventType);
                }
            };

            // Send interleaved packets:
            // 1. Channel 4: SoloDeathOccurred
            // 2. Channel 0: 10 Transform packets (unreliable, high drop)
            // 3. Channel 4: PartnerReviveTrigger
            // 4. Channel 0: 10 Transform packets
            // 5. Channel 4: TeamWipeOccurred

            void SendDeath(DeathEventType type)
            {
                var packet = new DeathLifecyclePacket
                {
                    Header = new PacketHeader(PacketType.PlayerDeath, 4, 0, (uint)Environment.TickCount),
                    EventType = type,
                    RemainingSeconds = 15f,
                    Epoch = 1
                };
                Span<byte> buffer = stackalloc byte[64];
                var writer = new PacketWriter(buffer);
                packet.Serialize(ref writer);
                harness.HostTransport.Send(harness.ClientTransport.LocalPeerId, buffer.ToArray(), 0, writer.Position, DeliveryMethod.ReliableOrdered, 4);
            }

            void SendTransform(float x)
            {
                var trans = new TransformSyncPacket
                {
                    Header = new PacketHeader(PacketType.TransformSync, 0, 0, (uint)Environment.TickCount),
                    PositionX = x,
                    PositionY = 10f
                };
                Span<byte> buffer = stackalloc byte[64];
                var writer = new PacketWriter(buffer);
                trans.Serialize(ref writer);
                harness.HostTransport.Send(harness.ClientTransport.LocalPeerId, buffer.ToArray(), 0, writer.Position, DeliveryMethod.Unreliable, 0);
            }

            SendDeath(DeathEventType.SoloDeathOccurred);
            for (int i = 0; i < 10; i++) SendTransform(i);
            SendDeath(DeathEventType.PartnerReviveTrigger);
            for (int i = 0; i < 10; i++) SendTransform(i + 10);
            SendDeath(DeathEventType.TeamWipeOccurred);

            // Deliver all packets across the harness
            harness.DeliverAll(100);

            // Channel 4 packets MUST arrive in exact sequential order: Solo -> Revive -> TeamWipe
            Assert.Equal(3, deliveredDeathTypes.Count);
            Assert.Equal(DeathEventType.SoloDeathOccurred, deliveredDeathTypes[0]);
            Assert.Equal(DeathEventType.PartnerReviveTrigger, deliveredDeathTypes[1]);
            Assert.Equal(DeathEventType.TeamWipeOccurred, deliveredDeathTypes[2]);
        }

        #endregion

        #region Area 4: Idempotency Under Duplicate Death Packets

        [Fact]
        public void Area4_DuplicateSoloDeathPackets_WhileSpectating_TriggersTeamWipeOnlyOnce()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.IsPartnerAlive = true;
            coordinator.OnLocalPlayerDied(10f, 10f);
            Assert.Equal(DeathState.Spectating, coordinator.CurrentState);

            int wipeEvents = 0;
            int outgoingPackets = 0;
            coordinator.OnTeamWipeTriggered += () => wipeEvents++;
            coordinator.OnOutgoingDeathPacket += p => outgoingPackets++;

            var peer = new NetworkPeerId(2);
            var soloPacket = new DeathLifecyclePacket
            {
                EventType = DeathEventType.SoloDeathOccurred,
                CoordinateX = 50f,
                CoordinateY = 50f,
                RemainingSeconds = 20f,
                Epoch = 1
            };

            // First packet triggers TeamWipe
            coordinator.HandleDeathPacket(peer, soloPacket);
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
            Assert.Equal(1, wipeEvents);
            Assert.Equal(1, outgoingPackets);

            // 5 duplicate packets arrive
            for (int i = 0; i < 5; i++)
            {
                coordinator.HandleDeathPacket(peer, soloPacket);
            }

            // Coordinator must remain in TeamWipe and NOT re-fire events or outgoing packets
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
            Assert.Equal(1, wipeEvents);
            Assert.Equal(1, outgoingPackets);
        }

        [Fact]
        public void Area4_DuplicateTeamWipePackets_WhileAlreadyInTeamWipe_DoesNotReFireWipeEvent()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.IsPartnerAlive = true;

            int wipeEvents = 0;
            coordinator.OnTeamWipeTriggered += () => wipeEvents++;

            var peer = new NetworkPeerId(2);
            var wipePacket = new DeathLifecyclePacket
            {
                EventType = DeathEventType.TeamWipeOccurred,
                CoordinateX = 50f,
                CoordinateY = 50f,
                RemainingSeconds = 0f,
                Epoch = 1
            };

            // First TeamWipe packet triggers wipe
            coordinator.HandleDeathPacket(peer, wipePacket);
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
            Assert.Equal(1, wipeEvents);

            // Multiple rapid duplicate TeamWipe packets arrive
            for (int i = 0; i < 5; i++)
            {
                coordinator.HandleDeathPacket(peer, wipePacket);
            }

            // IDEMPOTENCY REQUIREMENT: Must not fire OnTeamWipeTriggered again!
            Assert.Equal(1, wipeEvents);
        }

        #endregion

        #region Area 5: Bench Respawn Reset Racing With Incoming Packets

        [Fact]
        public void Area5_StaleTeamWipePacket_ArrivingAfterBenchReset_MustNotReWipeAlivePlayer()
        {
            using var harness = new DualClientTestHarness(latencyMs: 0, jitterMs: 0);

            // 1. Both die and enter TeamWipe
            harness.HostDeath.OnLocalPlayerDied(10f, 10f);
            harness.ClientDeath.OnLocalPlayerDied(20f, 20f);
            harness.DeliverAll(10);

            Assert.Equal(DeathState.TeamWipe, harness.HostDeath.CurrentState);
            Assert.Equal(DeathState.TeamWipe, harness.ClientDeath.CurrentState);

            // 2. Host finishes bench respawn reload and resets
            harness.HostDeath.ResetAfterBenchRespawn();
            Assert.Equal(DeathState.Alive, harness.HostDeath.CurrentState);
            Assert.True(harness.HostDeath.IsPartnerAlive);

            int hostWipeCountAfterReset = 0;
            harness.HostDeath.OnTeamWipeTriggered += () => hostWipeCountAfterReset++;

            // 3. A delayed/stale TeamWipeOccurred packet from Client arrives at Host AFTER Host reset
            var staleWipePacket = new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 0, (uint)Environment.TickCount),
                EventType = DeathEventType.TeamWipeOccurred,
                CoordinateX = 20f,
                CoordinateY = 20f,
                RemainingSeconds = 0f,
                Epoch = 1
            };

            harness.HostDeath.HandleDeathPacket(harness.ClientTransport.LocalPeerId, staleWipePacket);

            // RACING CONDITION CHECK: Host must NOT be thrown back into TeamWipe!
            Assert.Equal(DeathState.Alive, harness.HostDeath.CurrentState);
            Assert.Equal(0, hostWipeCountAfterReset);
        }

        [Fact]
        public void Area5_StaleSoloDeathPacket_ArrivingAfterBenchReset_MustNotMarkPartnerDead()
        {
            using var harness = new DualClientTestHarness(latencyMs: 0, jitterMs: 0);

            // 1. Team wipe occurred
            harness.HostDeath.OnLocalPlayerDied(10f, 10f);
            harness.ClientDeath.OnLocalPlayerDied(20f, 20f);
            harness.DeliverAll(10);

            // 2. Both reset at bench
            harness.HostDeath.ResetAfterBenchRespawn();
            harness.ClientDeath.ResetAfterBenchRespawn();

            Assert.True(harness.HostDeath.IsPartnerAlive);

            // 3. A delayed SoloDeathOccurred packet from before the reset arrives at Host
            var staleSoloPacket = new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 0, (uint)Environment.TickCount),
                EventType = DeathEventType.SoloDeathOccurred,
                CoordinateX = 20f,
                CoordinateY = 20f,
                RemainingSeconds = 20f,
                Epoch = 1
            };

            harness.HostDeath.HandleDeathPacket(harness.ClientTransport.LocalPeerId, staleSoloPacket);

            // Partner should NOT be marked dead by a packet from prior death lifecycle!
            Assert.True(harness.HostDeath.IsPartnerAlive,
                "Partner must still be considered alive after bench reset; stale packet must not corrupt state.");

            // And a subsequent solo death by Host should enter Spectating, NOT immediate TeamWipe
            bool hostSoloSuppressed = harness.HostDeath.OnLocalPlayerDied(5f, 5f);
            Assert.True(hostSoloSuppressed, "Subsequent death must be treated as solo death (suppress bench reload).");
            Assert.Equal(DeathState.Spectating, harness.HostDeath.CurrentState);
        }

        [Fact]
        public void Area5_StalePartnerRevivePacket_ArrivingDuringTeamWipe_MustNotMarkPartnerAlive()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.ForceBenchRespawn();
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
            Assert.False(coordinator.IsPartnerAlive);

            // Stale PartnerReviveTrigger arrives while in TeamWipe
            var revivePacket = new DeathLifecyclePacket
            {
                EventType = DeathEventType.PartnerReviveTrigger,
                CoordinateX = 100f,
                CoordinateY = 50f,
                Epoch = 1
            };

            coordinator.HandleDeathPacket(new NetworkPeerId(2), revivePacket);

            // While in TeamWipe, a stale revive packet must NOT set IsPartnerAlive to true!
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
            Assert.False(coordinator.IsPartnerAlive,
                "Partner must remain dead during TeamWipe; stale revive packet must not revive partner state.");
        }

        [Fact]
        public void Area2_AsymmetricLatency_SimultaneousDeath_BothTransitionCleanly()
        {
            using var harness = new DualClientTestHarness();
            harness.HostTransport.LatencyMs = 10;
            harness.ClientTransport.LatencyMs = 150;

            harness.HostDeath.OnLocalPlayerDied(10f, 10f);
            harness.ClientDeath.OnLocalPlayerDied(20f, 20f);

            harness.DeliverAll(100);

            Assert.Equal(DeathState.TeamWipe, harness.HostDeath.CurrentState);
            Assert.Equal(DeathState.TeamWipe, harness.ClientDeath.CurrentState);
        }

        #endregion
    }
}
