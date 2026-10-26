using System;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests
{
    public class DeathLifecycleCoordinatorTests
    {
        [Fact]
        public void SoloDeath_WhenPartnerAlive_EntersSpectatorAndSuppressesBench()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.IsPartnerAlive = true;
            coordinator.ReviveCooldownDuration = 20.0f;

            bool suppressed = coordinator.OnLocalPlayerDied(10f, 5f);

            Assert.True(suppressed); // Bench reload suppressed!
            Assert.Equal(DeathState.Spectating, coordinator.CurrentState);
            Assert.Equal(20.0f, coordinator.RemainingSpectatorTime);
        }

        [Fact]
        public void SoloDeath_WhenPartnerDead_TriggersTeamWipe()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.IsPartnerAlive = false;

            bool suppressed = coordinator.OnLocalPlayerDied(10f, 5f);

            Assert.False(suppressed); // Bench reload NOT suppressed!
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
        }

        [Fact]
        public void SpectatorTimer_WhenExpires_TriggersReviveAtPartner()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.IsPartnerAlive = true;
            coordinator.ReviveCooldownDuration = 15.0f;
            coordinator.LastPartnerPosition = (42f, 18f);

            coordinator.OnLocalPlayerDied(0f, 0f);

            (float X, float Y)? reviveTarget = null;
            coordinator.OnReviveAtPartnerRequested += (x, y) => reviveTarget = (x, y);

            // Advance time by 10s (5s remaining)
            coordinator.UpdateTick(10.0f);
            Assert.Equal(DeathState.Spectating, coordinator.CurrentState);
            Assert.Null(reviveTarget);

            // Advance remaining 5.5s
            coordinator.UpdateTick(5.5f);
            Assert.Equal(DeathState.RespawningAtPartner, coordinator.CurrentState);
            Assert.NotNull(reviveTarget);
            Assert.Equal(42f, reviveTarget.Value.X);
            Assert.Equal(18f, reviveTarget.Value.Y);

            // Complete revival
            coordinator.CompleteRevival();
            Assert.Equal(DeathState.Alive, coordinator.CurrentState);
        }

        [Fact]
        public void PartnerDying_DuringSpectator_TransitionsToTeamWipe()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.IsPartnerAlive = true;
            coordinator.ReviveCooldownDuration = 25.0f;

            coordinator.OnLocalPlayerDied(0f, 0f);
            Assert.Equal(DeathState.Spectating, coordinator.CurrentState);

            bool wipeTriggered = false;
            coordinator.OnTeamWipeTriggered += () => wipeTriggered = true;

            // Partner dies while we are spectating
            coordinator.OnRemotePartnerDied(50f, 20f);

            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
            Assert.True(wipeTriggered);
        }

        [Fact]
        public void InboundDeathPacket_HandlesSoloDeathAndPartnerRevive()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.IsPartnerAlive = true;

            // Inbound SoloDeathOccurred packet
            var soloPacket = new DeathLifecyclePacket
            {
                EventType = DeathEventType.SoloDeathOccurred,
                CoordinateX = 12f,
                CoordinateY = 34f,
                Epoch = 1
            };

            coordinator.HandleDeathPacket(new NetworkPeerId(2), soloPacket);
            Assert.False(coordinator.IsPartnerAlive);

            // Inbound PartnerReviveTrigger packet
            var revivePacket = new DeathLifecyclePacket
            {
                EventType = DeathEventType.PartnerReviveTrigger,
                CoordinateX = 99f,
                CoordinateY = 88f,
                Epoch = 1
            };

            coordinator.HandleDeathPacket(new NetworkPeerId(2), revivePacket);
            Assert.True(coordinator.IsPartnerAlive);
            Assert.Equal((99f, 88f), coordinator.LastPartnerPosition);
        }
    }
}
