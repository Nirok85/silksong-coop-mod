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

        [Fact]
        public void PollEvents_QueuedPongPacket_PreventsHeartbeatTimeout()
        {
            var (host, client) = FakeSteamTransport.CreatePair();
            long currentTime = 10000;
            host.TimeProvider = () => currentTime;
            client.TimeProvider = () => currentTime;
            host.StartHost();

            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);

            // Advance time past the heartbeat timeout threshold (15000ms)
            currentTime += FakeSteamTransport.SteamHeartbeatTimeoutMs + 1000; // 16s later

            // A Pong packet arrived and is waiting in the incoming Steam queue
            host.EnqueueIncomingPong(client.LocalPeerId);

            bool disconnected = false;
            host.OnPeerDisconnected += (_, reason) =>
            {
                if (reason == DisconnectReason.Timeout) disconnected = true;
            };

            // Execute PollEvents: incoming queue must be drained before checking timeout
            host.PollEvents();

            Assert.False(disconnected, "Peer should not be disconnected when queued Pong is available");
            Assert.True(host.IsConnected, "Host should remain connected after processing queued Pong");
            Assert.Equal(currentTime, host.LastPongReceivedMs);
        }

        [Fact]
        public void PollEvents_WithoutQueuedPongPacket_TimesOutAfterThreshold()
        {
            var (host, client) = FakeSteamTransport.CreatePair();
            long currentTime = 10000;
            host.TimeProvider = () => currentTime;
            host.StartHost();

            currentTime += FakeSteamTransport.SteamHeartbeatTimeoutMs + 1000;

            bool timeoutOccurred = false;
            host.OnPeerDisconnected += (_, reason) =>
            {
                if (reason == DisconnectReason.Timeout) timeoutOccurred = true;
            };

            host.PollEvents();

            Assert.True(timeoutOccurred, "Host should disconnect when no packet was received and timeout exceeded");
            Assert.False(host.IsConnected, "Host should no longer be connected after timeout");
        }
    }
}
