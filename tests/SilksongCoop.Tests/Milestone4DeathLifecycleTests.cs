using System;
using System.Collections.Generic;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.Revive;
using SilksongCoop.Core.State;
using SilksongCoop.Tests.TestHarness;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Milestone 4 Automated Test Suite: Cooperative Death & Respawn Lifecycle (Features F22–F25).
    /// Covers 4 testing tiers: Unit state machine transitions, boundary & protocol conditions,
    /// dual-client headless transport interactions over Channel 4, and full lifecycle application scenarios.
    /// </summary>
    public class Milestone4DeathLifecycleTests
    {
        #region Tier 1: Feature Coverage Tests

        [Fact]
        public void Tier1_F22_SoloDeath_SuppressesBenchReload_AndEmitsSoloDeathPacket()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.IsPartnerAlive = true;
            coordinator.ReviveCooldownDuration = 20.0f;

            DeathLifecyclePacket? emittedPacket = null;
            coordinator.OnOutgoingDeathPacket += p => emittedPacket = p;

            bool benchSuppressed = coordinator.OnLocalPlayerDied(15.5f, 22.0f);

            Assert.True(benchSuppressed, "Bench reload must be suppressed when partner is alive.");
            Assert.Equal(DeathState.Spectating, coordinator.CurrentState);
            Assert.Equal(20.0f, coordinator.RemainingSpectatorTime);
            Assert.NotNull(emittedPacket);
            Assert.Equal(DeathEventType.SoloDeathOccurred, emittedPacket.Value.EventType);
            Assert.Equal(15.5f, emittedPacket.Value.CoordinateX);
            Assert.Equal(22.0f, emittedPacket.Value.CoordinateY);
            Assert.Equal(20.0f, emittedPacket.Value.RemainingSeconds);
        }

        [Fact]
        public void Tier1_F23_SpectatorCountdown_TicksDown_AndTriggersReviveAtPartner()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.IsPartnerAlive = true;
            coordinator.ReviveCooldownDuration = 15.0f;
            coordinator.LastPartnerPosition = (35.0f, 12.0f);

            coordinator.OnLocalPlayerDied(0f, 0f);
            Assert.Equal(DeathState.Spectating, coordinator.CurrentState);

            (float X, float Y)? requestedRevivePos = null;
            coordinator.OnReviveAtPartnerRequested += (x, y) => requestedRevivePos = (x, y);

            // Tick 10 seconds -> 5s remaining, still spectating
            coordinator.UpdateTick(10.0f);
            Assert.Equal(DeathState.Spectating, coordinator.CurrentState);
            Assert.Equal(5.0f, coordinator.RemainingSpectatorTime);
            Assert.Null(requestedRevivePos);

            // Tick remaining 5.1 seconds -> Triggers partner revive
            coordinator.UpdateTick(5.1f);
            Assert.Equal(DeathState.RespawningAtPartner, coordinator.CurrentState);
            Assert.NotNull(requestedRevivePos);
            Assert.Equal(35.0f, requestedRevivePos.Value.X);
            Assert.Equal(12.0f, requestedRevivePos.Value.Y);

            // Complete revival -> returns to Alive
            coordinator.CompleteRevival();
            Assert.Equal(DeathState.Alive, coordinator.CurrentState);
        }

        [Fact]
        public void Tier1_F25_SimultaneousDeaths_InboundSoloDeathOccurred_ImmediatelyWipesWithoutWaiting()
        {
            // Both players die at virtually the same instant
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.IsPartnerAlive = true;
            coordinator.ReviveCooldownDuration = 25.0f;

            // Local player died first and entered spectator mode
            coordinator.OnLocalPlayerDied(10f, 5f);
            Assert.Equal(DeathState.Spectating, coordinator.CurrentState);
            Assert.Equal(25.0f, coordinator.RemainingSpectatorTime);

            bool teamWipeFired = false;
            DeathLifecyclePacket? wipePacket = null;
            coordinator.OnTeamWipeTriggered += () => teamWipeFired = true;
            coordinator.OnOutgoingDeathPacket += p => { if (p.EventType == DeathEventType.TeamWipeOccurred) wipePacket = p; };

            // Inbound SoloDeathOccurred arrives from partner who died concurrently
            var inboundPacket = new DeathLifecyclePacket
            {
                EventType = DeathEventType.SoloDeathOccurred,
                CoordinateX = 80f,
                CoordinateY = 40f,
                RemainingSeconds = 24.9f,
                Epoch = 1
            };
            coordinator.HandleDeathPacket(new NetworkPeerId(2), inboundPacket);

            // Must transition to TeamWipe IMMEDIATELY without waiting 25s!
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
            Assert.False(coordinator.IsPartnerAlive);
            Assert.True(teamWipeFired, "OnTeamWipeTriggered must fire immediately on concurrent death.");
            Assert.NotNull(wipePacket);
            Assert.Equal(DeathEventType.TeamWipeOccurred, wipePacket.Value.EventType);
        }

        [Fact]
        public void Tier1_F25_BenchReload_ResetsCoordinatorState()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.IsPartnerAlive = false;

            // Trigger wipe
            coordinator.OnLocalPlayerDied(0f, 0f);
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
            Assert.False(coordinator.IsPartnerAlive);

            // Bench reload complete
            coordinator.ResetAfterBenchRespawn();

            Assert.Equal(DeathState.Alive, coordinator.CurrentState);
            Assert.True(coordinator.IsPartnerAlive);
            Assert.Equal(0f, coordinator.RemainingSpectatorTime);
        }

        [Fact]
        public void Tier1_F25_ForceBenchRespawn_TriggersWipeDirectly()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.IsPartnerAlive = true;

            bool wipeFired = false;
            coordinator.OnTeamWipeTriggered += () => wipeFired = true;

            coordinator.ForceBenchRespawn();

            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
            Assert.False(coordinator.IsPartnerAlive);
            Assert.True(wipeFired);
        }

        #endregion

        #region Tier 2: Boundary Tests

        [Theory]
        [InlineData(5.0f, 15.0f)]   // Below 15s -> clamped to 15s
        [InlineData(14.9f, 15.0f)]  // Below 15s -> clamped to 15s
        [InlineData(15.0f, 15.0f)]  // Exact min bound
        [InlineData(22.5f, 22.5f)]  // Mid-range value
        [InlineData(30.0f, 30.0f)]  // Exact max bound
        [InlineData(45.0f, 30.0f)]  // Above 30s -> clamped to 30s
        public void Tier2_ReviveCooldownDuration_ClampingBehavior(float setDuration, float expectedDuration)
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.IsPartnerAlive = true;
            coordinator.ReviveCooldownDuration = setDuration;

            coordinator.OnLocalPlayerDied(0f, 0f);

            Assert.Equal(expectedDuration, coordinator.RemainingSpectatorTime);
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1, 1)]
        [InlineData(2, 1)]
        [InlineData(5, 2)]
        [InlineData(6, 3)]
        [InlineData(7, 3)]
        [InlineData(10, 5)]
        [InlineData(11, 5)]
        public void Tier2_CalculateReviveHealth_ClampsAndCalculatesCorrectly(int maxHealth, int expectedHealth)
        {
            int actual = SafeGroundSolver.CalculateReviveHealth(maxHealth);
            Assert.Equal(expectedHealth, actual);
        }

        [Fact]
        public void Tier2_TeamWipe_ReEntrancyIdempotent_DoesNotDuplicateWipeEvents()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.IsPartnerAlive = false;

            int wipeEventCount = 0;
            coordinator.OnTeamWipeTriggered += () => wipeEventCount++;

            // First death call triggers wipe
            bool firstSuppressed = coordinator.OnLocalPlayerDied(10f, 10f);
            Assert.False(firstSuppressed);
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
            Assert.Equal(1, wipeEventCount);

            // Re-entrant call while already in TeamWipe
            bool secondSuppressed = coordinator.OnLocalPlayerDied(10f, 10f);
            Assert.False(secondSuppressed);
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
            Assert.Equal(1, wipeEventCount); // Event count MUST NOT increment!
        }

        [Fact]
        public void Tier2_RapidDeathPacketBurst_DoesNotThrowOrCorruptState()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.IsPartnerAlive = true;

            for (int i = 0; i < 100; i++)
            {
                var packet = new DeathLifecyclePacket
                {
                    EventType = DeathEventType.SoloDeathOccurred,
                    CoordinateX = i * 1.5f,
                    CoordinateY = i * 0.5f,
                    RemainingSeconds = 20f,
                    Epoch = 1
                };
                coordinator.HandleDeathPacket(new NetworkPeerId(2), packet);
            }

            Assert.False(coordinator.IsPartnerAlive);
        }

        #endregion

        #region Tier 3: Interaction & Headless Network Tests (Channel 4 Isolation)

        [Fact]
        public void Tier3_Channel4_DeathPacketDelivered_OverReliableOrdered()
        {
            using var harness = new DualClientTestHarness();

            // Verify both started alive
            Assert.Equal(DeathState.Alive, harness.HostDeath.CurrentState);
            Assert.Equal(DeathState.Alive, harness.ClientDeath.CurrentState);

            // Host dies while Client alive -> Solo death suppresses bench
            bool suppressed = harness.HostDeath.OnLocalPlayerDied(100f, 50f);
            Assert.True(suppressed);
            Assert.Equal(DeathState.Spectating, harness.HostDeath.CurrentState);

            // Dispatch pending packets
            harness.Step(16);

            // Verify packet arrived on Client over Channel 4
            Assert.NotEmpty(harness.DeliveredDeathsToClient);
            var receivedPacket = harness.DeliveredDeathsToClient[0];
            Assert.Equal(DeathEventType.SoloDeathOccurred, receivedPacket.EventType);
            Assert.Equal(100f, receivedPacket.CoordinateX);
            Assert.Equal(50f, receivedPacket.CoordinateY);

            // Client now recognizes that host/partner is dead
            Assert.False(harness.ClientDeath.IsPartnerAlive);
        }

        [Fact]
        public void Tier3_SimultaneousDeaths_AcrossNetworkHarness_BothClientsTriggerTeamWipe()
        {
            using var harness = new DualClientTestHarness();

            // Both take lethal damage simultaneously before either receives the other's packet
            harness.HostDeath.OnLocalPlayerDied(10f, 10f);
            harness.ClientDeath.OnLocalPlayerDied(50f, 20f);

            // Both start in Spectating initially
            Assert.Equal(DeathState.Spectating, harness.HostDeath.CurrentState);
            Assert.Equal(DeathState.Spectating, harness.ClientDeath.CurrentState);

            int hostWipeCount = 0;
            int clientWipeCount = 0;
            harness.HostDeath.OnTeamWipeTriggered += () => hostWipeCount++;
            harness.ClientDeath.OnTeamWipeTriggered += () => clientWipeCount++;

            // Step transport so packets cross over Channel 4
            harness.Step(50);

            // Both must now have transitioned to TeamWipe!
            Assert.Equal(DeathState.TeamWipe, harness.HostDeath.CurrentState);
            Assert.Equal(DeathState.TeamWipe, harness.ClientDeath.CurrentState);
            Assert.True(hostWipeCount >= 1, "Host must have triggered team wipe.");
            Assert.True(clientWipeCount >= 1, "Client must have triggered team wipe.");
        }

        [Fact]
        public void Tier3_Channel4_ImmuneToTransformPacketLoss()
        {
            using var harness = new DualClientTestHarness();
            harness.HostTransport.PacketLossRate = 0.5f; // 50% loss
            harness.ClientTransport.PacketLossRate = 0.5f;

            // Host dies
            harness.HostDeath.OnLocalPlayerDied(12.0f, 34.0f);

            // Step multiple frames allowing reliable retransmission on Channel 4
            for (int i = 0; i < 20; i++)
            {
                harness.Step(50);
            }

            // ReliableOrdered channel 4 guarantees delivery despite packet loss
            Assert.False(harness.ClientDeath.IsPartnerAlive);
        }

        #endregion

        #region Tier 4: Application Scenarios

        [Fact]
        public void Tier4_FullSoloDeathSpectateAndReviveCycle()
        {
            using var harness = new DualClientTestHarness();
            harness.HostDeath.ReviveCooldownDuration = 15.0f;
            harness.HostDeath.LastPartnerPosition = (200.0f, 75.0f);

            // 1. Host dies
            harness.HostDeath.OnLocalPlayerDied(10.0f, 10.0f);
            Assert.Equal(DeathState.Spectating, harness.HostDeath.CurrentState);

            // Step transport to deliver death packet to Client
            harness.Step(16);
            Assert.False(harness.ClientDeath.IsPartnerAlive);

            // 2. Spectator timer counts down for 15s
            for (int i = 0; i < 150; i++)
            {
                harness.HostDeath.UpdateTick(0.1f);
                harness.Step(16);
            }

            // Host reaches 0s -> triggers RespawningAtPartner
            Assert.Equal(DeathState.RespawningAtPartner, harness.HostDeath.CurrentState);

            // Complete revival
            harness.HostDeath.CompleteRevival();
            Assert.Equal(DeathState.Alive, harness.HostDeath.CurrentState);

            // Step so Client receives revival
            harness.Step(16);
            Assert.True(harness.ClientDeath.IsPartnerAlive);
        }

        [Fact]
        public void Tier4_FullDualWipeAndBenchRespawnCycle()
        {
            using var harness = new DualClientTestHarness();

            // 1. Host dies
            harness.HostDeath.OnLocalPlayerDied(10f, 10f);
            harness.Step(16);

            // 2. Client dies 5s later while Host is spectating
            harness.ClientDeath.OnLocalPlayerDied(40f, 20f);
            harness.Step(16);

            // Both are now in TeamWipe
            Assert.Equal(DeathState.TeamWipe, harness.HostDeath.CurrentState);
            Assert.Equal(DeathState.TeamWipe, harness.ClientDeath.CurrentState);

            // 3. Bench scene reloads -> both reset
            harness.HostDeath.ResetAfterBenchRespawn();
            harness.ClientDeath.ResetAfterBenchRespawn();

            Assert.Equal(DeathState.Alive, harness.HostDeath.CurrentState);
            Assert.Equal(DeathState.Alive, harness.ClientDeath.CurrentState);
            Assert.True(harness.HostDeath.IsPartnerAlive);
            Assert.True(harness.ClientDeath.IsPartnerAlive);

            // 4. Verify subsequent death enters spectator mode normally (no lingering wipe state)
            bool nextDeathSuppressed = harness.HostDeath.OnLocalPlayerDied(5f, 5f);
            Assert.True(nextDeathSuppressed, "Subsequent death must successfully enter spectator mode after bench reset.");
            Assert.Equal(DeathState.Spectating, harness.HostDeath.CurrentState);
        }

        #endregion
    }
}
