using System.Collections.Generic;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests.State
{
    /// <summary>
    /// Regression coverage for the P0 revive fixes:
    /// - the revive target is the nearest living peer, not dictionary order;
    /// - PartnerReviveTrigger is emitted only after the revive actually completed;
    /// - a bench respawn does not resurrect other dead peers;
    /// - a death immediately after a bench respawn is not swallowed by a time window.
    /// </summary>
    public class DeathReviveTargetTests
    {
        private static NetworkPeerId Peer(ulong value) => new NetworkPeerId(value, $"Peer{value}");

        private static DeathLifecyclePacket Packet(DeathEventType type, uint epoch = 1, float x = 0f, float y = 0f)
            => new DeathLifecyclePacket
            {
                EventType = type,
                CoordinateX = x,
                CoordinateY = y,
                Epoch = epoch
            };

        [Fact]
        public void ReviveTarget_PicksNearestLivingPeer()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var far = Peer(2);
            var near = Peer(3);
            coordinator.RegisterRemotePeer(far);
            coordinator.RegisterRemotePeer(near);
            coordinator.UpdatePartnerPosition(far, 100f, 0f);
            coordinator.UpdatePartnerPosition(near, 5f, 0f);
            coordinator.ReviveCooldownDuration = 15f;

            var requested = new List<(float X, float Y)>();
            coordinator.OnReviveAtPartnerRequested += (x, y) => requested.Add((x, y));

            coordinator.OnLocalPlayerDied(0f, 0f);
            coordinator.UpdateTick(15.1f);

            Assert.Equal(DeathState.RespawningAtPartner, coordinator.CurrentState);
            Assert.Equal(near, coordinator.ReviveTargetPeer);
            Assert.Equal((5f, 0f), coordinator.ReviveTargetPosition);
            Assert.Single(requested);
            Assert.Equal((5f, 0f), requested[0]);
        }

        [Fact]
        public void ReviveTarget_SkipsDeadPeer()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var dead = Peer(2);
            var alive = Peer(3);
            coordinator.RegisterRemotePeer(dead);
            coordinator.RegisterRemotePeer(alive);
            coordinator.UpdatePartnerPosition(dead, 1f, 0f);
            coordinator.UpdatePartnerPosition(alive, 50f, 0f);
            coordinator.OnRemotePartnerDied(dead, 1f, 0f);
            coordinator.ReviveCooldownDuration = 15f;

            coordinator.OnLocalPlayerDied(0f, 0f);
            coordinator.UpdateTick(15.1f);

            Assert.Equal(alive, coordinator.ReviveTargetPeer);
            Assert.Equal((50f, 0f), coordinator.ReviveTargetPosition);
        }

        [Fact]
        public void TimerExpiry_DoesNotAnnounceSuccessBeforeConfirmation()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = Peer(2);
            coordinator.RegisterRemotePeer(peer);
            coordinator.UpdatePartnerPosition(peer, 7f, 8f);
            coordinator.ReviveCooldownDuration = 15f;

            var outgoing = new List<DeathLifecyclePacket>();
            coordinator.OnOutgoingDeathPacket += packet => outgoing.Add(packet);

            coordinator.OnLocalPlayerDied(0f, 0f);
            coordinator.UpdateTick(15.1f);

            Assert.Equal(DeathState.RespawningAtPartner, coordinator.CurrentState);
            Assert.DoesNotContain(outgoing, p => p.EventType == DeathEventType.PartnerReviveTrigger);

            coordinator.ConfirmReviveAtPartner(7.5f, 8.5f);

            Assert.Equal(DeathState.Alive, coordinator.CurrentState);
            var revivePacket = Assert.Single(outgoing, p => p.EventType == DeathEventType.PartnerReviveTrigger);
            Assert.Equal(7.5f, revivePacket.CoordinateX);
            Assert.Equal(8.5f, revivePacket.CoordinateY);
        }

        [Fact]
        public void FailRevive_DoesNotAnnounceSuccess()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = Peer(2);
            coordinator.RegisterRemotePeer(peer);
            coordinator.UpdatePartnerPosition(peer, 7f, 8f);
            coordinator.ReviveCooldownDuration = 15f;

            var outgoing = new List<DeathLifecyclePacket>();
            coordinator.OnOutgoingDeathPacket += packet => outgoing.Add(packet);

            coordinator.OnLocalPlayerDied(0f, 0f);
            coordinator.UpdateTick(15.1f);
            coordinator.FailReviveAtPartner();

            Assert.Equal(DeathState.Alive, coordinator.CurrentState);
            Assert.Null(coordinator.ReviveTargetPeer);
            Assert.DoesNotContain(outgoing, p => p.EventType == DeathEventType.PartnerReviveTrigger);
        }

        [Fact]
        public void BenchCheckpointUpdate_MarksOnlyItsSenderAlive()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var first = Peer(2);
            var second = Peer(3);
            coordinator.RegisterRemotePeer(first);
            coordinator.RegisterRemotePeer(second);

            coordinator.OnRemotePartnerDied(first, 1f, 1f);
            coordinator.OnRemotePartnerDied(second, 2f, 2f);
            Assert.False(coordinator.IsPartnerAlive);

            coordinator.HandleDeathPacket(first, Packet(DeathEventType.BenchCheckpointUpdate, epoch: 3));

            Assert.True(coordinator.IsRemotePeerAlive(first));
            Assert.False(coordinator.IsRemotePeerAlive(second));
            Assert.True(coordinator.IsPartnerAlive);
        }

        [Fact]
        public void BenchRespawn_DoesNotResurrectOtherPeers()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var first = Peer(2);
            var second = Peer(3);
            coordinator.RegisterRemotePeer(first);
            coordinator.RegisterRemotePeer(second);
            coordinator.OnRemotePartnerDied(first, 1f, 1f);
            coordinator.OnRemotePartnerDied(second, 2f, 2f);

            coordinator.ResetAfterBenchRespawn();

            Assert.Equal(DeathState.Alive, coordinator.CurrentState);
            Assert.False(coordinator.IsRemotePeerAlive(first));
            Assert.False(coordinator.IsRemotePeerAlive(second));
            Assert.False(coordinator.IsPartnerAlive);
        }

        [Fact]
        public void SoloBenchFallback_DoesNotResurrectDeadPeer()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var alive = Peer(2);
            var dead = Peer(3);
            coordinator.RegisterRemotePeer(alive);
            coordinator.RegisterRemotePeer(dead);
            coordinator.OnRemotePartnerDied(dead, 2f, 2f);

            coordinator.ForceBenchRespawn(notifyPartner: false);

            Assert.False(coordinator.IsRemotePeerAlive(dead));
            Assert.True(coordinator.IsRemotePeerAlive(alive));
        }

        [Fact]
        public void PartnerDeath_ImmediatelyAfterBenchRespawn_IsProcessed()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = Peer(2);
            coordinator.RegisterRemotePeer(peer);
            coordinator.ForceBenchRespawn();
            coordinator.ResetAfterBenchRespawn(); // epoch 2

            // The partner finished its own reload and is alive again.
            coordinator.HandleDeathPacket(peer, Packet(DeathEventType.BenchCheckpointUpdate, epoch: 2));
            Assert.True(coordinator.IsRemotePeerAlive(peer));

            // It dies immediately, in the same epoch. The old five-second post-bench
            // window dropped this genuine death.
            coordinator.HandleDeathPacket(peer, Packet(DeathEventType.SoloDeathOccurred, epoch: 2, x: 5f, y: 6f));

            Assert.False(coordinator.IsRemotePeerAlive(peer));
            Assert.False(coordinator.IsPartnerAlive);
        }

        [Fact]
        public void StaleTeamWipe_WithOlderEpoch_IsStillIgnoredAfterBenchReset()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = Peer(2);
            coordinator.RegisterRemotePeer(peer);
            coordinator.ForceBenchRespawn();
            coordinator.ResetAfterBenchRespawn(); // epoch 2
            int wipes = 0;
            coordinator.OnTeamWipeTriggered += () => wipes++;

            coordinator.HandleDeathPacket(peer, Packet(DeathEventType.TeamWipeOccurred, epoch: 1));

            Assert.Equal(DeathState.Alive, coordinator.CurrentState);
            Assert.Equal(0, wipes);
        }

        [Fact]
        public void ReviveTarget_PrefersSameScenePeerOverANearerPeerElsewhere()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var nearbyOtherRoom = Peer(2);
            var fartherSameRoom = Peer(3);
            coordinator.RegisterRemotePeer(nearbyOtherRoom);
            coordinator.RegisterRemotePeer(fartherSameRoom);
            coordinator.UpdatePartnerPosition(nearbyOtherRoom, 3f, 0f);
            coordinator.UpdatePartnerPosition(fartherSameRoom, 40f, 0f);
            coordinator.SetRemotePeerInSameScene(nearbyOtherRoom, false);
            coordinator.SetRemotePeerInSameScene(fartherSameRoom, true);
            coordinator.ReviveCooldownDuration = 15f;

            coordinator.OnLocalPlayerDied(0f, 0f);
            coordinator.UpdateTick(15.1f);

            Assert.Equal(fartherSameRoom, coordinator.ReviveTargetPeer);
        }

        [Fact]
        public void ReviveTarget_FallsBackToLivingPeerInAnotherScene()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var otherRoom = Peer(2);
            coordinator.RegisterRemotePeer(otherRoom);
            coordinator.UpdatePartnerPosition(otherRoom, 3f, 0f);
            coordinator.SetRemotePeerInSameScene(otherRoom, false);
            coordinator.ReviveCooldownDuration = 15f;

            coordinator.OnLocalPlayerDied(0f, 0f);
            coordinator.UpdateTick(15.1f);

            // The plugin validates the scene again and uses the bench fallback.
            Assert.Equal(otherRoom, coordinator.ReviveTargetPeer);
        }

        [Fact]
        public void BeginSoloBenchRespawn_DoesNotAnnounceSuccessBeforeReload()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = Peer(2);
            coordinator.RegisterRemotePeer(peer);
            var outgoing = new List<DeathLifecyclePacket>();
            coordinator.OnOutgoingDeathPacket += packet => outgoing.Add(packet);

            coordinator.BeginSoloBenchRespawn();

            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
            Assert.DoesNotContain(outgoing, p => p.EventType == DeathEventType.SoloBenchRespawn);

            coordinator.ResetAfterBenchRespawn();

            Assert.Equal(DeathState.Alive, coordinator.CurrentState);
            var packet = Assert.Single(outgoing, p => p.EventType == DeathEventType.SoloBenchRespawn);
            Assert.Equal(coordinator.CurrentEpoch, packet.Epoch);
        }

        [Fact]
        public void IncomingTeamWipe_MarksEveryRegisteredPeerDead()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var sender = Peer(2);
            var other = Peer(3);
            coordinator.RegisterRemotePeer(sender);
            coordinator.RegisterRemotePeer(other);

            coordinator.HandleDeathPacket(sender, Packet(DeathEventType.TeamWipeOccurred, epoch: 1));

            Assert.False(coordinator.IsRemotePeerAlive(sender));
            Assert.False(coordinator.IsRemotePeerAlive(other));
            Assert.False(coordinator.IsPartnerAlive);
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
        }

        [Fact]
        public void ConfirmRevive_ClearsTargetAndKeepsOtherPeersUntouched()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var target = Peer(2);
            var other = Peer(3);
            coordinator.RegisterRemotePeer(target);
            coordinator.RegisterRemotePeer(other);
            coordinator.UpdatePartnerPosition(target, 10f, 10f);
            coordinator.UpdatePartnerPosition(other, 20f, 20f);
            coordinator.ReviveCooldownDuration = 15f;

            coordinator.OnLocalPlayerDied(0f, 0f);
            coordinator.UpdateTick(15.1f);
            Assert.Equal(target, coordinator.ReviveTargetPeer);

            coordinator.ConfirmReviveAtPartner(11f, 11f);

            Assert.Null(coordinator.ReviveTargetPeer);
            Assert.True(coordinator.TryGetRemotePeerPosition(target, out float targetX, out float targetY));
            Assert.Equal((11f, 11f), (targetX, targetY));
            Assert.True(coordinator.TryGetRemotePeerPosition(other, out float otherX, out float otherY));
            Assert.Equal((20f, 20f), (otherX, otherY));
        }
    }
}
