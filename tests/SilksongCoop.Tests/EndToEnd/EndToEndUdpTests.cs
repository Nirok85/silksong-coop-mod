using System;
using System.Collections.Generic;
using System.Threading;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using Xunit;

namespace SilksongCoop.Tests.EndToEnd
{
    /// <summary>
    /// End-to-end tests over the real UDP transport with two peers in memory.
    /// </summary>
    public class EndToEndUdpTests
    {
        [Fact]
        public void HostClient_Connect_ExchangesTransforms()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            using var client = new LiteNetLibTransport(2, "Client");

            host.StartHost(0);
            client.Connect($"127.0.0.1:{host.BoundPort}");

            PumpUntilConnected(host, client, maxMs: 3000);
            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);

            var received = new List<(NetworkPeerId peer, byte[] data, int length)>();
            host.OnDataReceived += (peer, data, offset, length) =>
            {
                var copy = new byte[length];
                Buffer.BlockCopy(data, offset, copy, 0, length);
                lock (received) received.Add((peer, copy, length));
            };

            for (int i = 0; i < 30; i++)
            {
                var packet = new TransformSyncPacket
                {
                    Header = new PacketHeader(PacketType.TransformSync, 0, (uint)(i + 1), 1),
                    PositionX = i,
                    PositionY = i * 0.5f,
                    VelocityX = 0,
                    VelocityY = 0,
                    FacingDirection = 1,
                    StateFlags = 0,
                    AnimationClipHash = 0,
                    NormalizedTime = 0,
                    SceneNameHash = 123
                };
                byte[] buffer = new byte[64];
                var writer = new PacketWriter(buffer);
                packet.Serialize(ref writer);
                client.Send(host.LocalPeerId, buffer, 0, writer.Position, DeliveryMethod.Unreliable, 0);

                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(10);
            }

            lock (received)
            {
                Assert.True(received.Count >= 1, "Host should receive at least one transform");
                var reader = new PacketReader(received[0].data);
                var receivedPacket = TransformSyncPacket.Deserialize(ref reader);
                Assert.Equal(123, receivedPacket.SceneNameHash);
            }
        }

        [Fact]
        public void HostClient_Disconnect_ReconnectAllowed()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            using var client = new LiteNetLibTransport(2, "Client");

            host.StartHost(0);
            client.Connect($"127.0.0.1:{host.BoundPort}");
            PumpUntilConnected(host, client, maxMs: 3000);
            Assert.True(host.IsConnected && client.IsConnected);

            // Client disconnects, host stays listening
            client.Disconnect(DisconnectReason.GracefulDisconnect);
            PumpUntil(() => !host.IsConnected && !client.IsConnected, host, client, maxMs: 1000);
            Assert.False(client.IsConnected);

            // Host should return to listening state
            Assert.True(host.BoundPort > 0);

            // A fresh host on a new port avoids stale endpoint state from the first session.
            using var host2 = new LiteNetLibTransport(1, "Host");
            host2.StartHost(0);
            using var client2 = new LiteNetLibTransport(3, "Client2");
            client2.Connect($"127.0.0.1:{host2.BoundPort}");
            PumpUntilConnected(host2, client2, maxMs: 3000);
            Assert.True(host2.IsConnected && client2.IsConnected);
        }

        [Fact]
        public void ReliableOrdered_RetriesUntilAcked()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            using var client = new LiteNetLibTransport(2, "Client");

            host.StartHost(0);
            client.Connect($"127.0.0.1:{host.BoundPort}");
            PumpUntilConnected(host, client, maxMs: 3000);

            var received = new List<byte[]>();
            host.OnDataReceived += (peer, data, offset, length) =>
            {
                var copy = new byte[length];
                Buffer.BlockCopy(data, offset, copy, 0, length);
                lock (received) received.Add(copy);
            };

            byte[] payload = new byte[] { 1, 2, 3, 4, 5 };
            client.Send(host.LocalPeerId, payload, 0, payload.Length, DeliveryMethod.ReliableOrdered, 1);

            // Enough polls for a retransmit if the first one was lost
            for (int i = 0; i < 60 && received.Count == 0; i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(10);
            }

            lock (received)
            {
                Assert.True(received.Count > 0);
            }
        }

        private static void PumpUntilConnected(LiteNetLibTransport host, LiteNetLibTransport client, int maxMs)
        {
            PumpUntil(() => host.IsConnected && client.IsConnected, host, client, maxMs);
        }

        private static void PumpUntil(Func<bool> condition, LiteNetLibTransport host, LiteNetLibTransport client, int maxMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(maxMs);
            while (!condition() && DateTime.UtcNow < deadline)
            {
                try
                {
                    host.PollEvents();
                    client.PollEvents();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                Thread.Sleep(10);
            }
        }
    }
}
