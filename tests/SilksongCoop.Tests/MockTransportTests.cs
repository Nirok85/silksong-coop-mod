using System;
using System.Collections.Generic;
using System.Text;
using SilksongCoop.Core.Networking;
using Xunit;

namespace SilksongCoop.Tests
{
    public class MockTransportTests
    {
        [Fact]
        public void ConnectionHandshake_ConnectsBothPeersAndTriggersEvents()
        {
            var (host, client) = MockNetworkTransport.CreatePair();

            Assert.True(host.IsHost);
            Assert.False(client.IsHost);
            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);
            Assert.Equal((ulong)1, host.LocalPeerId.Value);
            Assert.Equal((ulong)2, client.LocalPeerId.Value);
        }

        [Fact]
        public void ReliableOrdered_DeliversInSequence_EvenWithSimulatedJitter()
        {
            // Paired transports with 20ms latency and 15ms jitter with virtual clock
            var (host, client) = MockNetworkTransport.CreatePair(latencyMs: 20, jitterMs: 15, useVirtualTime: true, seed: 1234);

            var receivedInts = new List<int>();
            client.OnDataReceived += (peer, data, offset, length) =>
            {
                int val = BitConverter.ToInt32(data, offset);
                receivedInts.Add(val);
            };

            // Host sends integers 0 through 24 over Channel 1 (ReliableOrdered)
            for (int i = 0; i < 25; i++)
            {
                byte[] bytes = BitConverter.GetBytes(i);
                host.Send(client.LocalPeerId, bytes, 0, bytes.Length, DeliveryMethod.ReliableOrdered, channel: 1);
            }

            // Advance time and poll repeatedly until all packets are delivered
            for (int tick = 0; tick < 50; tick++)
            {
                host.AdvanceVirtualTime(10);
                client.AdvanceVirtualTime(10);
                host.PollEvents();
                client.PollEvents();
            }

            // All 25 packets must arrive in exact sequential order
            Assert.Equal(25, receivedInts.Count);
            for (int i = 0; i < 25; i++)
            {
                Assert.Equal(i, receivedInts[i]);
            }
        }

        [Fact]
        public void Sequenced_DropsOutdatedPackets()
        {
            var (host, client) = MockNetworkTransport.CreatePair();

            var receivedSeqs = new List<int>();
            client.OnDataReceived += (peer, data, offset, length) =>
            {
                int val = BitConverter.ToInt32(data, offset);
                receivedSeqs.Add(val);
            };

            // Manually enqueue packet 5, then packet 2 (outdated)
            byte channel = 0;
            var pktNewer = new MockNetworkTransport.QueuedPacket
            {
                Channel = channel,
                Method = DeliveryMethod.Sequenced,
                Sequence = 5,
                Data = BitConverter.GetBytes(5),
                DeliveryTimeTicks = 0,
                Sender = host.LocalPeerId
            };

            var pktOlder = new MockNetworkTransport.QueuedPacket
            {
                Channel = channel,
                Method = DeliveryMethod.Sequenced,
                Sequence = 2,
                Data = BitConverter.GetBytes(2),
                DeliveryTimeTicks = 0,
                Sender = host.LocalPeerId
            };

            client.EnqueueIncoming(pktNewer);
            client.EnqueueIncoming(pktOlder);

            client.PollEvents();

            // Packet 5 is accepted; packet 2 must be dropped because sequence 2 < 5
            Assert.Single(receivedSeqs);
            Assert.Equal(5, receivedSeqs[0]);
        }

        [Fact]
        public void MultiChannel_ChannelSegregation()
        {
            var (host, client) = MockNetworkTransport.CreatePair();

            var ch0Packets = new List<string>();
            var ch1Packets = new List<string>();

            client.OnDataReceived += (peer, data, offset, length) =>
            {
                string msg = Encoding.UTF8.GetString(data, offset, length);
                if (msg.StartsWith("CH0")) ch0Packets.Add(msg);
                else if (msg.StartsWith("CH1")) ch1Packets.Add(msg);
            };

            // Send packets across both channels
            for (int i = 0; i < 10; i++)
            {
                byte[] data0 = Encoding.UTF8.GetBytes($"CH0_{i}");
                byte[] data1 = Encoding.UTF8.GetBytes($"CH1_{i}");
                host.Send(client.LocalPeerId, data0, 0, data0.Length, DeliveryMethod.Unreliable, channel: 0);
                host.Send(client.LocalPeerId, data1, 0, data1.Length, DeliveryMethod.ReliableOrdered, channel: 1);
            }

            client.PollEvents();

            Assert.Equal(10, ch0Packets.Count);
            Assert.Equal(10, ch1Packets.Count);
        }

        [Fact]
        public void PacketLossSimulation_DropsUnreliablePackets()
        {
            // Loss rate = 100% (1.0f) for unreliable packets
            var (host, client) = MockNetworkTransport.CreatePair(lossRate: 1.0f);

            int receivedCount = 0;
            client.OnDataReceived += (peer, data, offset, length) => receivedCount++;

            byte[] dummy = new byte[4];
            host.Send(client.LocalPeerId, dummy, 0, dummy.Length, DeliveryMethod.Unreliable, channel: 0);

            client.PollEvents();

            // Should be dropped
            Assert.Equal(0, receivedCount);

            // But ReliableOrdered should NEVER be dropped despite loss rate
            host.Send(client.LocalPeerId, dummy, 0, dummy.Length, DeliveryMethod.ReliableOrdered, channel: 1);
            client.PollEvents();

            Assert.Equal(1, receivedCount);
        }

        [Fact]
        public void Disconnect_TriggersPeerDisconnectedEvent()
        {
            var (host, client) = MockNetworkTransport.CreatePair();

            bool clientNotifiedOfDisconnect = false;
            client.OnPeerDisconnected += (peer, reason) =>
            {
                clientNotifiedOfDisconnect = true;
                Assert.Equal(host.LocalPeerId, peer);
            };

            host.Disconnect();

            Assert.False(host.IsConnected);
            Assert.False(client.IsConnected);
            Assert.True(clientNotifiedOfDisconnect);
        }
    }
}
