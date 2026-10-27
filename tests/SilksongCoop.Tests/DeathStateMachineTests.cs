using System;
using System.Net;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.State;
using SilksongCoop.Core.Protocol.Packets;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Property/state-machine tests for <see cref="DeathLifecycleCoordinator"/> (plan item 12).
    /// </summary>
    public class DeathStateMachineTests
    {
        [Fact]
        public void SoloDeath_WhilePartnerAlive_EntersSpectatingAndBroadcasts()
        {
            var c = new DeathLifecycleCoordinator();
            bool suppressed = c.OnLocalPlayerDied(1f, 2f);
            Assert.True(suppressed);
            Assert.Equal(DeathState.Spectating, c.CurrentState);
            Assert.True(c.RemainingSpectatorTime > 0f);
        }

        [Fact]
        public void SoloDeath_WhilePartnerDead_TriggersTeamWipe()
        {
            var c = new DeathLifecycleCoordinator();
            c.IsPartnerAlive = false;
            bool suppressed = c.OnLocalPlayerDied(1f, 2f);
            Assert.False(suppressed);
            Assert.Equal(DeathState.TeamWipe, c.CurrentState);
        }

        [Fact]
        public void RemotePartnerDeath_WhileLocalAlive_MarksPartnerDead()
        {
            var c = new DeathLifecycleCoordinator();
            c.OnRemotePartnerDied(3f, 4f);
            Assert.False(c.IsPartnerAlive);
            Assert.Equal(DeathState.Alive, c.CurrentState);
        }

        [Fact]
        public void RemotePartnerDeath_WhileLocalSpectating_TriggersTeamWipe()
        {
            var c = new DeathLifecycleCoordinator();
            c.OnLocalPlayerDied(1f, 2f); // partner alive -> spectating
            c.OnRemotePartnerDied(3f, 4f);
            Assert.Equal(DeathState.TeamWipe, c.CurrentState);
        }

        [Fact]
        public void UpdateTick_Spectating_DecrementsTimer()
        {
            var c = new DeathLifecycleCoordinator();
            c.OnLocalPlayerDied(1f, 2f);
            float before = c.RemainingSpectatorTime;
            c.UpdateTick(1f);
            Assert.True(c.RemainingSpectatorTime < before);
        }

        [Fact]
        public void UpdateTick_Spectating_TimerExpiry_WithAlivePartner_RequestsRevive()
        {
            var c = new DeathLifecycleCoordinator();
            c.OnLocalPlayerDied(1f, 2f);
            (float X, float Y)? revived = null;
            c.OnReviveAtPartnerRequested += (x, y) => revived = (x, y);
            c.LastPartnerPosition = (7f, 8f);
            // drain the timer
            for (int i = 0; i < 100 && c.CurrentState == DeathState.Spectating; i++)
                c.UpdateTick(1f);
            Assert.Equal(DeathState.RespawningAtPartner, c.CurrentState);
            Assert.NotNull(revived);
        }

        [Fact]
        public void UpdateTick_Spectating_TimerExpiry_WithDeadPartner_TriggersTeamWipe()
        {
            var c = new DeathLifecycleCoordinator();
            c.OnLocalPlayerDied(1f, 2f);
            c.IsPartnerAlive = false;
            for (int i = 0; i < 100 && c.CurrentState == DeathState.Spectating; i++)
                c.UpdateTick(1f);
            Assert.Equal(DeathState.TeamWipe, c.CurrentState);
        }

        [Fact]
        public void CompleteRevival_ReturnsToAlive()
        {
            var c = new DeathLifecycleCoordinator();
            c.OnLocalPlayerDied(1f, 2f);
            c.CompleteRevival();
            Assert.Equal(DeathState.Alive, c.CurrentState);
        }

        [Fact]
        public void ResetAfterBenchRespawn_AdvancesEpoch()
        {
            var c = new DeathLifecycleCoordinator();
            uint before = c.CurrentEpoch;
            c.ResetAfterBenchRespawn();
            Assert.Equal(before + 1, c.CurrentEpoch);
            Assert.Equal(DeathState.Alive, c.CurrentState);
        }

        [Fact]
        public void SynchronizeEpochForNewSession_ResetsToEpochOne()
        {
            var c = new DeathLifecycleCoordinator();
            c.ResetAfterBenchRespawn();
            c.ResetAfterBenchRespawn();
            c.SynchronizeEpochForNewSession();
            Assert.Equal((uint)1, c.CurrentEpoch);
            Assert.Equal(DeathState.Alive, c.CurrentState);
            Assert.True(c.IsPartnerAlive);
        }

        [Fact]
        public void ForceBenchRespawn_TriggersTeamWipeAndPacket()
        {
            var c = new DeathLifecycleCoordinator();
            DeathLifecyclePacket? outgoing = null;
            c.OnOutgoingDeathPacket += p => outgoing = p;
            c.ForceBenchRespawn();
            Assert.Equal(DeathState.TeamWipe, c.CurrentState);
            Assert.NotNull(outgoing);
            Assert.Equal(DeathEventType.TeamWipeOccurred, outgoing!.Value.EventType);
        }

        [Fact]
        public void HandleDeathPacket_StaleEpoch_IsIgnored()
        {
            var c = new DeathLifecycleCoordinator();
            c.ResetAfterBenchRespawn(); // epoch 2
            var packet = new DeathLifecyclePacket
            {
                EventType = DeathEventType.SoloDeathOccurred,
                CoordinateX = 1f, CoordinateY = 2f,
                Epoch = 1 // stale
            };
            c.HandleDeathPacket(new NetworkPeerId(2, "P"), packet);
            Assert.Equal(DeathState.Alive, c.CurrentState);
        }

        [Fact]
        public void HandleDeathPacket_HigherEpoch_IsAdopted()
        {
            var c = new DeathLifecycleCoordinator();
            var packet = new DeathLifecyclePacket
            {
                EventType = DeathEventType.SoloDeathOccurred,
                CoordinateX = 1f, CoordinateY = 2f,
                Epoch = 5
            };
            c.HandleDeathPacket(new NetworkPeerId(2, "P"), packet);
            Assert.Equal((uint)5, c.CurrentEpoch);
        }

        [Fact]
        public void OnRemotePartnerRevived_MarksPartnerAlive()
        {
            var c = new DeathLifecycleCoordinator();
            c.IsPartnerAlive = false;
            c.OnRemotePartnerRevived(9f, 10f);
            Assert.True(c.IsPartnerAlive);
            Assert.Equal(9f, c.LastPartnerPosition.X);
        }
    }
}
