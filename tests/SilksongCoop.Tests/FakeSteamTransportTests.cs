using System;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.State;
using SilksongCoop.Tests.TestHarness;
using Xunit;

namespace SilksongCoop.Tests
{
    public class FakeSteamTransportTests
    {
        [Fact]
        public void CreatePair_ConnectsBothPeers()
        {
            var (host, client) = FakeSteamTransport.CreatePair();
            host.StartHost();
            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);
        }

        [Fact]
        public void Send_DeliversToPeer()
        {
            var (host, client) = FakeSteamTransport.CreatePair();
            host.StartHost();
            byte[]? received = null;
            client.OnDataReceived += (_, data, _, _) => received = data;
            host.Send(client.LocalPeerId, new byte[] { 1, 2, 3 }, 0, 3, DeliveryMethod.ReliableOrdered, 0);
            Assert.NotNull(received);
            Assert.Equal(3, received!.Length);
        }

        [Fact]
        public void Disconnect_NotifiesPeer()
        {
            var (host, client) = FakeSteamTransport.CreatePair();
            host.StartHost();
            bool notified = false;
            client.OnPeerDisconnected += (_, _) => notified = true;
            host.Disconnect();
            Assert.True(notified);
        }

        [Fact]
        public void StartHost_WithoutPeer_DoesNotThrow()
        {
            var host = new FakeSteamTransport(1, true);
            host.StartHost();
            Assert.True(host.IsConnected);
            Assert.Empty(host.ConnectedPeers);
        }

        [Fact]
        public void Send_ForwardsChannelToReceiver()
        {
            var (host, client) = FakeSteamTransport.CreatePair();
            host.StartHost();
            host.Send(client.LocalPeerId, new byte[] { 1 }, 0, 1, DeliveryMethod.ReliableOrdered, NetworkChannels.Vitals);
            Assert.Equal(NetworkChannels.Vitals, client.LastReceivedChannel);
        }

        [Fact]
        public void SessionState_RemotePeerIds_AddRemove_KeepsPrimaryInSync()
        {
            var session = new CoopSessionState();
            var host = new NetworkPeerId(1, "Host");
            var guest = new NetworkPeerId(2, "Guest");

            session.SetHosting(host, "SK-TEST");
            session.SetConnected(guest, SessionRole.Host);
            Assert.Single(session.RemotePeerIds);
            Assert.Equal(guest, session.RemotePeerIds[0]);

            var third = new NetworkPeerId(3, "Third");
            session.AddRemotePeer(third);
            Assert.Equal(2, session.RemotePeerIds.Count);

            session.RemoveRemotePeer(guest);
            Assert.Single(session.RemotePeerIds);
            Assert.Equal(third, session.RemotePeerId);

            session.RemoveRemotePeer(third);
            Assert.Empty(session.RemotePeerIds);
            Assert.Null(session.RemotePeerId);
        }
    }
}
