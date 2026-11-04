using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests.State
{
    public class PerPeerVitalsTests
    {
        private static VitalsSyncPacket Packet(byte hp, byte silk, byte maxHp, byte maxSilk, byte needle = 0)
            => new VitalsSyncPacket
            {
                CurrentHealth = hp,
                CurrentSilk = silk,
                MaxHealth = maxHp,
                MaxSilkSpools = maxSilk,
                NeedleUpgradeLevel = needle
            };

        [Fact]
        public void TwoPeers_AreTrackedIndependently()
        {
            var service = new VitalsSyncService();
            var first = new NetworkPeerId(2, "First");
            var second = new NetworkPeerId(3, "Second");

            service.HandleVitalsPacket(first, Packet(3, 5, 5, 1));
            service.HandleVitalsPacket(second, Packet(4, 7, 6, 2, 1));

            Assert.Equal(2, service.RemotePeers.Count);
            Assert.True(service.TryGetRemoteVitals(first, out var firstVitals));
            Assert.Equal(3, firstVitals.Health);
            Assert.Equal(5, firstVitals.Silk);
            Assert.Equal(5, firstVitals.MaxHealth);
            Assert.True(service.TryGetRemoteVitals(second, out var secondVitals));
            Assert.Equal(4, secondVitals.Health);
            Assert.Equal(6, secondVitals.MaxHealth);
            Assert.Equal(2, secondVitals.MaxSilkSpools);
            Assert.Equal(1, secondVitals.NeedleUpgradeLevel);
        }

        [Fact]
        public void LegacyFieldsMirrorFirstPeerOnly()
        {
            var service = new VitalsSyncService();
            var first = new NetworkPeerId(2, "First");
            var second = new NetworkPeerId(3, "Second");

            service.HandleVitalsPacket(first, Packet(3, 5, 5, 1));
            service.HandleVitalsPacket(second, Packet(4, 7, 6, 2));

            Assert.Equal(3, service.RemoteHealth);
            Assert.Equal(5, service.RemoteSilk);
            Assert.Equal(5, service.RemoteMaxHealth);
            Assert.Equal(1, service.RemoteMaxSilk);
        }

        [Fact]
        public void RemovingPrimary_PromotesAnotherPeer()
        {
            var service = new VitalsSyncService();
            var first = new NetworkPeerId(2, "First");
            var second = new NetworkPeerId(3, "Second");
            service.HandleVitalsPacket(first, Packet(3, 5, 5, 1));
            service.HandleVitalsPacket(second, Packet(4, 7, 6, 2, 1));

            service.RemoveRemotePeer(first);

            Assert.Single(service.RemotePeers);
            Assert.Equal(4, service.RemoteHealth);
            Assert.Equal(6, service.RemoteMaxHealth);
            Assert.Equal(2, service.RemoteMaxSilk);
            Assert.Equal(1, service.RemoteNeedleUpgradeLevel);
        }

        [Fact]
        public void RemovingAdditionalPeer_LeavesPrimaryUnchanged()
        {
            var service = new VitalsSyncService();
            var first = new NetworkPeerId(2, "First");
            var second = new NetworkPeerId(3, "Second");
            service.HandleVitalsPacket(first, Packet(3, 5, 5, 1));
            service.HandleVitalsPacket(second, Packet(4, 7, 6, 2));

            service.RemoveRemotePeer(second);

            Assert.Single(service.RemotePeers);
            Assert.Equal(3, service.RemoteHealth);
            Assert.Equal(5, service.RemoteMaxHealth);
        }

        [Fact]
        public void ClearRemotePeers_ResetsLegacyAndPerPeerState()
        {
            var service = new VitalsSyncService();
            service.HandleVitalsPacket(new NetworkPeerId(2), Packet(3, 5, 5, 1));

            service.ClearRemotePeers();

            Assert.Empty(service.RemotePeers);
            Assert.False(service.HasRemoteData);
            Assert.Equal(0, service.RemoteHealth);
            Assert.Equal(0, service.RemoteMaxHealth);
        }
    }
}
