using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests.State
{
    /// <summary>
    /// Per-peer death/revive coverage for the 3+ player foundation. The aggregate
    /// single-partner behaviour lives in <see cref="DeathLifecycleCoordinatorTests"/>
    /// and must keep passing unchanged.
    /// </summary>
    public class PerPeerDeathTests
    {
        private static NetworkPeerId Peer(ulong value) => new NetworkPeerId(value, $"Peer{value}");

        private static DeathLifecyclePacket Packet(DeathEventType type, float x = 0f, float y = 0f, uint epoch = 1)
            => new DeathLifecyclePacket
            {
                EventType = type,
                CoordinateX = x,
                CoordinateY = y,
                Epoch = epoch
            };

        [Fact]
        public void OnePeerDies_OtherPeerStaysAlive()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var first = Peer(2);
            var second = Peer(3);
            coordinator.RegisterRemotePeer(first);
            coordinator.RegisterRemotePeer(second);

            coordinator.OnRemotePartnerDied(first, 10f, 20f);

            Assert.False(coordinator.IsRemotePeerAlive(first));
            Assert.True(coordinator.IsRemotePeerAlive(second));
            Assert.True(coordinator.IsPartnerAlive);
            Assert.Equal(DeathState.Alive, coordinator.CurrentState);
        }

        [Fact]
        public void AllPeersDead_DuringSpectator_TriggersTeamWipe()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var first = Peer(2);
            var second = Peer(3);
            coordinator.RegisterRemotePeer(first);
            coordinator.RegisterRemotePeer(second);

            bool wiped = false;
            coordinator.OnTeamWipeTriggered += () => wiped = true;

            Assert.True(coordinator.OnLocalPlayerDied(0f, 0f));
            Assert.Equal(DeathState.Spectating, coordinator.CurrentState);

            coordinator.OnRemotePartnerDied(first, 1f, 1f);
            Assert.Equal(DeathState.Spectating, coordinator.CurrentState);
            Assert.False(wiped);

            coordinator.OnRemotePartnerDied(second, 2f, 2f);
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
            Assert.True(wiped);
        }

        [Fact]
        public void RevivingOnePeer_DoesNotReviveTheOther()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var first = Peer(2);
            var second = Peer(3);
            coordinator.RegisterRemotePeer(first);
            coordinator.RegisterRemotePeer(second);

            coordinator.OnRemotePartnerDied(first, 10f, 10f);
            coordinator.OnRemotePartnerDied(second, 20f, 20f);

            coordinator.OnRemotePartnerRevived(first, 11f, 11f);

            Assert.True(coordinator.IsRemotePeerAlive(first));
            Assert.False(coordinator.IsRemotePeerAlive(second));
            Assert.True(coordinator.TryGetRemotePeerPosition(first, out float x, out float y));
            Assert.Equal(11f, x);
            Assert.Equal(11f, y);
        }

        [Fact]
        public void HandleDeathPacket_UpdatesOnlyTheSender()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var first = Peer(2);
            var second = Peer(3);
            coordinator.RegisterRemotePeer(first);
            coordinator.RegisterRemotePeer(second);

            coordinator.HandleDeathPacket(
                first,
                Packet(DeathEventType.SoloDeathOccurred, 12f, 34f));

            Assert.False(coordinator.IsRemotePeerAlive(first));
            Assert.True(coordinator.IsRemotePeerAlive(second));
            Assert.True(coordinator.IsPartnerAlive);

            coordinator.HandleDeathPacket(
                second,
                Packet(DeathEventType.SoloDeathOccurred, 50f, 60f));

            Assert.False(coordinator.IsRemotePeerAlive(second));
            Assert.False(coordinator.IsPartnerAlive);
        }

        [Fact]
        public void SoloBenchRespawn_RevivesOnlySenderAndKeepsPosition()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var first = Peer(2);
            var second = Peer(3);
            coordinator.RegisterRemotePeer(first);
            coordinator.RegisterRemotePeer(second);

            coordinator.OnRemotePartnerDied(first, 7f, 8f);
            coordinator.OnRemotePartnerDied(second, 9f, 10f);

            coordinator.HandleDeathPacket(first, Packet(DeathEventType.SoloBenchRespawn));

            Assert.True(coordinator.IsRemotePeerAlive(first));
            Assert.False(coordinator.IsRemotePeerAlive(second));
            Assert.True(coordinator.TryGetRemotePeerPosition(first, out float x, out float y));
            Assert.Equal(7f, x);
            Assert.Equal(8f, y);
        }

        [Fact]
        public void TeamWipeOccurred_MarksSenderDeadAndIgnoresDuplicate()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = Peer(2);
            coordinator.RegisterRemotePeer(peer);
            int wiped = 0;
            coordinator.OnTeamWipeTriggered += () => wiped++;

            coordinator.HandleDeathPacket(peer, Packet(DeathEventType.TeamWipeOccurred, 1f, 2f));

            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
            Assert.False(coordinator.IsRemotePeerAlive(peer));
            Assert.Equal(1, wiped);

            coordinator.HandleDeathPacket(peer, Packet(DeathEventType.TeamWipeOccurred, 1f, 2f));
            Assert.Equal(1, wiped);
        }

        [Fact]
        public void SpectatorTimer_RevivesAtALivingPeer()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var first = Peer(2);
            var second = Peer(3);
            coordinator.RegisterRemotePeer(first);
            coordinator.RegisterRemotePeer(second);
            coordinator.ReviveCooldownDuration = 15f;
            coordinator.OnRemotePartnerRevived(first, 10f, 10f);
            coordinator.OnRemotePartnerRevived(second, 20f, 20f);

            (float X, float Y)? target = null;
            coordinator.OnReviveAtPartnerRequested += (x, y) => target = (x, y);

            Assert.True(coordinator.OnLocalPlayerDied(0f, 0f));
            coordinator.OnRemotePartnerDied(first, 11f, 11f);

            coordinator.UpdateTick(15.1f);

            Assert.Equal(DeathState.RespawningAtPartner, coordinator.CurrentState);
            Assert.NotNull(target);
            Assert.Equal((20f, 20f), target.Value);
        }

        [Fact]
        public void LastPartnerPosition_PrefersLivingPeer()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var first = Peer(2);
            var second = Peer(3);
            coordinator.RegisterRemotePeer(first);
            coordinator.RegisterRemotePeer(second);

            coordinator.OnRemotePartnerRevived(first, 10f, 11f);
            coordinator.OnRemotePartnerDied(second, 20f, 21f);

            Assert.Equal((10f, 11f), coordinator.LastPartnerPosition);

            coordinator.OnRemotePartnerDied(first, 12f, 13f);
            coordinator.OnRemotePartnerRevived(second, 30f, 31f);

            Assert.Equal((30f, 31f), coordinator.LastPartnerPosition);
        }

        [Fact]
        public void UpdatePartnerPosition_TracksEachPeerIndependently()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var first = Peer(2);
            var second = Peer(3);
            coordinator.RegisterRemotePeer(first);
            coordinator.RegisterRemotePeer(second);

            coordinator.UpdatePartnerPosition(first, 1f, 2f);
            coordinator.UpdatePartnerPosition(second, 3f, 4f);

            Assert.True(coordinator.TryGetRemotePeerPosition(first, out float firstX, out float firstY));
            Assert.Equal((1f, 2f), (firstX, firstY));
            Assert.True(coordinator.TryGetRemotePeerPosition(second, out float secondX, out float secondY));
            Assert.Equal((3f, 4f), (secondX, secondY));
            Assert.Equal((1f, 2f), coordinator.LastPartnerPosition);
        }

        [Fact]
        public void RemoveRemotePeer_DropsOnlyThatPeer()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var first = Peer(2);
            var second = Peer(3);
            coordinator.RegisterRemotePeer(first);
            coordinator.RegisterRemotePeer(second);
            coordinator.OnRemotePartnerDied(first, 1f, 1f);

            coordinator.RemoveRemotePeer(first);

            Assert.False(coordinator.TryGetRemotePeerPosition(first, out _, out _));
            Assert.True(coordinator.TryGetRemotePeerPosition(second, out _, out _));
            Assert.True(coordinator.IsPartnerAlive);
        }

        [Fact]
        public void RemoveLastPeer_ResetsAggregateFlag()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = Peer(2);
            coordinator.RegisterRemotePeer(peer);
            coordinator.OnRemotePartnerDied(peer, 1f, 1f);
            Assert.False(coordinator.IsPartnerAlive);

            coordinator.RemoveRemotePeer(peer);

            Assert.True(coordinator.IsPartnerAlive);
        }

        [Fact]
        public void ClearRemotePeers_ResetsEveryPeerAndAggregate()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.RegisterRemotePeer(Peer(2));
            coordinator.RegisterRemotePeer(Peer(3));
            coordinator.OnRemotePartnerDied(Peer(2), 1f, 1f);
            Assert.True(coordinator.IsPartnerAlive);

            coordinator.OnRemotePartnerDied(Peer(3), 1f, 1f);
            Assert.False(coordinator.IsPartnerAlive);

            coordinator.ClearRemotePeers();

            Assert.True(coordinator.IsPartnerAlive);
            Assert.False(coordinator.TryGetRemotePeerPosition(Peer(2), out _, out _));
            Assert.False(coordinator.TryGetRemotePeerPosition(Peer(3), out _, out _));
        }

        [Fact]
        public void SingleRegisteredPeer_KeepsLegacyAggregateBehaviour()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = Peer(2);
            coordinator.RegisterRemotePeer(peer);

            coordinator.HandleDeathPacket(peer, Packet(DeathEventType.SoloDeathOccurred, 12f, 34f));
            Assert.False(coordinator.IsPartnerAlive);

            coordinator.HandleDeathPacket(peer, Packet(DeathEventType.PartnerReviveTrigger, 99f, 88f));
            Assert.True(coordinator.IsPartnerAlive);
            Assert.Equal((99f, 88f), coordinator.LastPartnerPosition);
        }

        [Fact]
        public void SynchronizeEpochForNewSession_ClearsPerPeerState()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = Peer(2);
            coordinator.RegisterRemotePeer(peer);
            coordinator.OnRemotePartnerDied(peer, 1f, 1f);

            coordinator.SynchronizeEpochForNewSession();

            Assert.True(coordinator.IsPartnerAlive);
            Assert.False(coordinator.TryGetRemotePeerPosition(peer, out _, out _));
            Assert.Equal(DeathState.Alive, coordinator.CurrentState);
            Assert.Equal(1u, coordinator.CurrentEpoch);
        }
    }
}
