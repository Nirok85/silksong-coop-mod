using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests.State
{
    public class DeathHandshakeRegressionTests
    {
        [Fact]
        public void RemoteRecoverySnapshotDuringLocalWipe_IsRememberedWithoutEndingLocalWipe()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.RegisterRemotePeer(Peer);
            coordinator.ForceBenchRespawn();
            coordinator.HandleDeathPacket(Peer, Packet(DeathEventType.LifecycleSnapshot, 2));
            Assert.True(coordinator.IsRemotePeerAlive(Peer));
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
            coordinator.ResetAfterBenchRespawn();
            Assert.True(coordinator.IsRemotePeerAlive(Peer));
        }
        private static readonly NetworkPeerId Peer = new NetworkPeerId(2);
        private static DeathLifecyclePacket Packet(DeathEventType type, uint epoch, byte dead = 0) => new()
        {
            EventType = type, Epoch = epoch, FallenPlayerIndex = dead,
            CoordinateX = 12f, CoordinateY = 34f
        };

        [Fact]
        public void LowerEpochHandshake_AllowsSubsequentGenuineDeathWithoutLoweringLocalEpoch()
        {
            var host = new DeathLifecycleCoordinator();
            host.ResetAfterBenchRespawn();
            host.ResetAfterBenchRespawn();
            host.RegisterRemotePeer(Peer);
            host.HandleDeathPacket(Peer, Packet(DeathEventType.LifecycleSnapshot, 1));
            host.HandleDeathPacket(Peer, Packet(DeathEventType.SoloDeathOccurred, 1));
            Assert.False(host.IsRemotePeerAlive(Peer));
            Assert.Equal(3u, host.CurrentEpoch);
        }

        [Fact]
        public void DeadSenderHandshake_PreservesSpectatorStateAndCoordinates()
        {
            var sender = new DeathLifecycleCoordinator();
            sender.RegisterRemotePeer(Peer);
            sender.OnLocalPlayerDied(7f, 8f);
            sender.UpdateTick(2f);
            var snapshot = sender.CreateLifecycleSnapshot(90f, 91f);
            Assert.Equal(DeathEventType.LifecycleSnapshot, snapshot.EventType);
            Assert.Equal(1, snapshot.FallenPlayerIndex);
            Assert.Equal(7f, snapshot.CoordinateX);
            Assert.Equal(8f, snapshot.CoordinateY);
            Assert.Equal(sender.RemainingSpectatorTime, snapshot.RemainingSeconds);
            Assert.Equal(DeathState.Spectating, sender.CurrentState);

            var receiver = new DeathLifecycleCoordinator();
            receiver.RegisterRemotePeer(Peer);
            receiver.HandleDeathPacket(Peer, snapshot);
            Assert.False(receiver.IsRemotePeerAlive(Peer));
            Assert.Equal(DeathState.Alive, receiver.CurrentState);
            Assert.True(receiver.TryGetRemotePeerPosition(Peer, out float x, out float y));
            Assert.Equal(7f, x);
            Assert.Equal(8f, y);
        }

        [Fact]
        public void AliveSnapshot_UsesProvidedLocalPosition()
        {
            var sender = new DeathLifecycleCoordinator();
            var snapshot = sender.CreateLifecycleSnapshot(8f, 9f);
            Assert.Equal(0, snapshot.FallenPlayerIndex);
            Assert.Equal(8f, snapshot.CoordinateX);
            Assert.Equal(9f, snapshot.CoordinateY);
            Assert.Equal(0f, snapshot.RemainingSeconds);
        }

        [Theory]
        [InlineData(DeathEventType.BenchCheckpointUpdate)]
        [InlineData(DeathEventType.SoloBenchRespawn)]
        [InlineData(DeathEventType.LifecycleSnapshot)]
        public void OlderRecovery_CannotResurrectDeadPeer(DeathEventType recovery)
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.RegisterRemotePeer(Peer);
            coordinator.HandleDeathPacket(Peer, Packet(DeathEventType.LifecycleSnapshot, 2));
            coordinator.HandleDeathPacket(Peer, Packet(DeathEventType.SoloDeathOccurred, 2));
            coordinator.HandleDeathPacket(Peer, Packet(recovery, 1));
            Assert.False(coordinator.IsRemotePeerAlive(Peer));
            Assert.Equal(2u, coordinator.CurrentEpoch);
        }

        [Fact]
        public void UnknownLowerBench_DoesNotResurrectDeadPeer()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.ResetAfterBenchRespawn();
            coordinator.RegisterRemotePeer(Peer);
            coordinator.OnRemotePartnerDied(Peer, 1f, 2f);
            coordinator.HandleDeathPacket(Peer, Packet(DeathEventType.BenchCheckpointUpdate, 1));
            Assert.False(coordinator.IsRemotePeerAlive(Peer));
        }

        [Fact]
        public void PureEpochSync_PreservesLocalAndRemoteDeathState()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var living = new NetworkPeerId(3);
            coordinator.RegisterRemotePeer(Peer);
            coordinator.RegisterRemotePeer(living);
            coordinator.OnRemotePartnerDied(Peer, 1f, 2f);
            coordinator.OnLocalPlayerDied(3f, 4f);
            coordinator.HandleDeathPacket(Peer, Packet(DeathEventType.EpochSync, 5));
            Assert.Equal(5u, coordinator.CurrentEpoch);
            Assert.Equal(DeathState.Spectating, coordinator.CurrentState);
            Assert.False(coordinator.IsRemotePeerAlive(Peer));
            Assert.True(coordinator.IsRemotePeerAlive(living));
            coordinator.HandleDeathPacket(Peer, Packet(DeathEventType.EpochSync, 1));
            Assert.Equal(5u, coordinator.CurrentEpoch);
            coordinator.ResetAfterBenchRespawn();
            Assert.Equal(5u, coordinator.CurrentEpoch);
        }

        [Fact]
        public void PeerEpochsAreIndependent_AndClearedOnDisconnect()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var older = new NetworkPeerId(3);
            coordinator.RegisterRemotePeer(Peer);
            coordinator.RegisterRemotePeer(older);
            coordinator.HandleDeathPacket(Peer, Packet(DeathEventType.LifecycleSnapshot, 5));
            coordinator.HandleDeathPacket(older, Packet(DeathEventType.LifecycleSnapshot, 1));
            coordinator.HandleDeathPacket(older, Packet(DeathEventType.SoloDeathOccurred, 1));
            Assert.False(coordinator.IsRemotePeerAlive(older));
            Assert.True(coordinator.IsRemotePeerAlive(Peer));
            Assert.Equal(5u, coordinator.CurrentEpoch);
            coordinator.RemoveRemotePeer(Peer);
            coordinator.RegisterRemotePeer(Peer);
            coordinator.HandleDeathPacket(Peer, Packet(DeathEventType.LifecycleSnapshot, 1, 1));
            Assert.False(coordinator.IsRemotePeerAlive(Peer));
        }

        [Fact]
        public void BroadcastEpochSync_ReportsActualDeathRatherThanClaimingAlive()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.RegisterRemotePeer(Peer);
            coordinator.OnLocalPlayerDied(2f, 3f);
            DeathLifecyclePacket? sent = null;
            coordinator.OnOutgoingDeathPacket += packet => sent = packet;
            coordinator.BroadcastEpochSync();
            Assert.True(sent.HasValue);
            Assert.Equal(DeathEventType.LifecycleSnapshot, sent.Value.EventType);
            Assert.Equal(1, sent.Value.FallenPlayerIndex);
            Assert.Equal(DeathState.Spectating, coordinator.CurrentState);
        }
    }
}
