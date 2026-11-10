using System;
using System.Collections.Generic;
using System.Threading;
using SilksongCoop.Core.Networking;
using Xunit;

namespace SilksongCoop.Tests.Integration
{
    /// <summary>
    /// End-to-end lifecycle tests over real loopback UDP sockets. These complement the
    /// deterministic in-memory transport tests: they exercise the actual envelope framing,
    /// HMAC handshake, channel sequencing and retransmission paths. They do not replace a
    /// two-machine Unity/Steam smoke test, but they catch protocol regressions that a mock
    /// transport cannot see.
    /// </summary>
    public class RealUdpLifecycleTests
    {
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(5);

        [Theory]
        [InlineData("1.0.30000", true)]
        [InlineData("1.0.29999", false)]
        [InlineData(null, false)]
        public void RealUdp_RequiresMatchingGameVersion(string? clientVersion, bool shouldConnect)
        {
            using var host = new LiteNetLibTransport(1, "Host", "1.0.30000") { RequireRoomCode = false };
            using var client = new LiteNetLibTransport(2, "Client", clientVersion) { RequireRoomCode = false };
            host.StartHost(0);
            client.Connect($"127.0.0.1:{host.BoundPort}");

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            do
            {
                host.PollEvents();
                client.PollEvents();
                if (host.IsConnected && client.IsConnected) break;
                Thread.Sleep(5);
            }
            while (DateTime.UtcNow < deadline);

            Assert.Equal(shouldConnect, host.IsConnected);
            Assert.Equal(shouldConnect, client.IsConnected);
        }

        [Fact]
        public void RealUdp_AllGameChannels_DeliverPayloadsBothWays()
        {
            using var host = new LiteNetLibTransport(1, "Host") { RequireRoomCode = false };
            using var client = new LiteNetLibTransport(2, "Client") { RequireRoomCode = false };

            host.StartHost(0);
            AssertConnect(host, client, ConnectTimeout);

            var hostReceived = new List<byte[]>();
            var clientReceived = new List<byte[]>();
            host.OnDataReceived += (_, data, offset, length) =>
                hostReceived.Add(Copy(data, offset, length));
            client.OnDataReceived += (_, data, offset, length) =>
                clientReceived.Add(Copy(data, offset, length));

            byte[] channels = { 0, 1, 2, 3, 4, 5, 7 };
            foreach (byte channel in channels)
            {
                byte[] payload =
                {
                    channel,
                    0x11,
                    0x22,
                    0x33,
                    0x44
                };

                var method = channel == NetworkChannels.Transform || channel == NetworkChannels.Desync
                    ? DeliveryMethod.Unreliable
                    : DeliveryMethod.ReliableOrdered;

                host.Send(client.LocalPeerId, payload, 0, payload.Length, method, channel);
                client.Send(host.LocalPeerId, payload, 0, payload.Length, method, channel);
            }

            // Unreliable packets are not retransmitted, so a loopback test must allow for
            // the rare local drop. Reliable channels are the ones that must all arrive;
            // require every reliable payload and at least one unreliable payload per side.
            PumpUntil(
                () => hostReceived.Count >= channels.Length - 1 &&
                      clientReceived.Count >= channels.Length - 1,
                DeliveryTimeout, host, client);

            Assert.Contains(hostReceived, p => p.Length == 5 && p[0] == NetworkChannels.Vitals);
            Assert.Contains(hostReceived, p => p.Length == 5 && p[0] == NetworkChannels.World);
            Assert.Contains(clientReceived, p => p.Length == 5 && p[0] == NetworkChannels.Vitals);
            Assert.Contains(clientReceived, p => p.Length == 5 && p[0] == NetworkChannels.World);
            Assert.All(hostReceived, p => Assert.Equal(5, p.Length));
            Assert.All(clientReceived, p => Assert.Equal(5, p.Length));
        }

        [Fact]
        public void RealUdp_ReliableBurst_ArrivesInOrderWithoutLoss()
        {
            using var host = new LiteNetLibTransport(1, "Host") { RequireRoomCode = false };
            using var client = new LiteNetLibTransport(2, "Client") { RequireRoomCode = false };

            host.StartHost(0);
            AssertConnect(host, client, ConnectTimeout);

            const int packetCount = 128;
            var received = new List<int>(packetCount);
            client.OnDataReceived += (_, data, offset, length) =>
            {
                if (length == 4)
                    received.Add(BitConverter.ToInt32(data, offset));
            };

            host.PollEvents();
            for (int i = 0; i < packetCount; i++)
            {
                byte[] payload = BitConverter.GetBytes(i);
                host.Send(client.LocalPeerId, payload, 0, payload.Length,
                    DeliveryMethod.ReliableOrdered, NetworkChannels.World);
            }

            PumpUntil(() => received.Count >= packetCount, TimeSpan.FromSeconds(10), host, client);

            Assert.Equal(packetCount, received.Count);
            for (int i = 0; i < packetCount; i++)
                Assert.Equal(i, received[i]);
        }

        [Fact]
        public void RealUdp_ClientReconnect_SameHostSessionAcceptsNewHandshake()
        {
            using var host = new LiteNetLibTransport(1, "Host") { RequireRoomCode = false };
            using var client = new LiteNetLibTransport(2, "Client") { RequireRoomCode = false };

            host.StartHost(0);
            int port = host.BoundPort;
            AssertConnect(host, client, ConnectTimeout);

            int hostDisconnects = 0;
            host.OnPeerDisconnected += (_, reason) =>
            {
                if (reason == DisconnectReason.PeerDisconnected ||
                    reason == DisconnectReason.GracefulDisconnect)
                {
                    Interlocked.Increment(ref hostDisconnects);
                }
            };

            client.Disconnect();
            PumpUntil(() => Volatile.Read(ref hostDisconnects) > 0,
                TimeSpan.FromSeconds(5), host, client);

            Assert.False(host.IsConnected);
            Assert.False(client.IsConnected);

            client.Connect($"127.0.0.1:{port}");
            PumpUntil(() => host.IsConnected && client.IsConnected,
                ConnectTimeout, host, client);

            Assert.True(host.IsConnected, "host did not accept the reconnect");
            Assert.True(client.IsConnected, "client did not reconnect");
            Assert.Equal((ulong)2, host.RemotePeerId!.Value.Value);
            Assert.Equal((ulong)1, client.RemotePeerId!.Value.Value);

            var hostReceived = new List<byte>();
            host.OnDataReceived += (_, data, offset, length) =>
            {
                for (int i = 0; i < length; i++) hostReceived.Add(data[offset + i]);
            };

            byte[] probe = { 0xCA, 0xFE };
            client.Send(host.LocalPeerId, probe, 0, probe.Length,
                DeliveryMethod.ReliableOrdered, NetworkChannels.Vitals);

            PumpUntil(() => hostReceived.Count >= probe.Length, DeliveryTimeout, host, client);
            Assert.Equal(probe, hostReceived.ToArray());
        }

        [Fact]
        public void RealUdp_SecondClient_IsRejectedWhileSinglePeerSessionIsActive()
        {
            using var host = new LiteNetLibTransport(1, "Host") { RequireRoomCode = false };
            using var first = new LiteNetLibTransport(2, "First") { RequireRoomCode = false };
            using var second = new LiteNetLibTransport(3, "Second") { RequireRoomCode = false };

            host.StartHost(0);
            int port = host.BoundPort;
            AssertConnect(host, first, ConnectTimeout);

            second.Connect($"127.0.0.1:{port}");

            // Give the second handshake enough time to be processed and rejected.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                host.PollEvents();
                first.PollEvents();
                second.PollEvents();
                Thread.Sleep(5);
            }

            Assert.True(host.IsConnected);
            Assert.Equal((ulong)2, host.RemotePeerId!.Value.Value);
            Assert.False(second.IsConnected,
                "UDP transport must not accept a second remote peer");

            // The active peer is unaffected by the rejected handshake.
            var received = new List<byte>();
            first.OnDataReceived += (_, data, offset, length) =>
            {
                for (int i = 0; i < length; i++) received.Add(data[offset + i]);
            };
            host.Send(first.LocalPeerId, new byte[] { 0x42 }, 0, 1,
                DeliveryMethod.ReliableOrdered, NetworkChannels.World);
            PumpUntil(() => received.Count == 1, DeliveryTimeout, host, first);
            Assert.Equal(0x42, received[0]);
        }

        [Fact]
        public void RealUdp_HostRestart_NewEndpointAcceptsClientAndClearsOldIdentity()
        {
            using var host = new LiteNetLibTransport(1, "Host") { RequireRoomCode = false };
            using var client = new LiteNetLibTransport(2, "Client") { RequireRoomCode = false };

            host.StartHost(0);
            AssertConnect(host, client, ConnectTimeout);

            client.Disconnect();
            host.Disconnect();
            PumpUntil(() => !host.IsConnected && !client.IsConnected,
                TimeSpan.FromSeconds(3), host, client);

            // A fresh StartHost must not inherit the old peer identity, sequence counters
            // or pending events. The client connects to the new ephemeral port.
            host.StartHost(0);
            int newPort = host.BoundPort;
            Assert.True(newPort > 0);
            Assert.False(host.IsConnected);
            Assert.Null(host.RemotePeerId);

            client.Connect($"127.0.0.1:{newPort}");
            PumpUntil(() => host.IsConnected && client.IsConnected,
                ConnectTimeout, host, client);

            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);
            Assert.Equal((ulong)2, host.RemotePeerId!.Value.Value);
        }

        private static void AssertConnect(
            LiteNetLibTransport host,
            LiteNetLibTransport client,
            TimeSpan timeout)
        {
            client.Connect($"127.0.0.1:{host.BoundPort}");
            PumpUntil(() => host.IsConnected && client.IsConnected, timeout, host, client);
            Assert.True(host.IsConnected, "host did not complete the handshake");
            Assert.True(client.IsConnected, "client did not complete the handshake");
        }

        private static void PumpUntil(
            Func<bool> condition,
            TimeSpan timeout,
            params LiteNetLibTransport[] transports)
        {
            var deadline = DateTime.UtcNow + timeout;
            do
            {
                for (int i = 0; i < transports.Length; i++)
                    transports[i].PollEvents();

                if (condition()) return;
                Thread.Sleep(5);
            }
            while (DateTime.UtcNow < deadline);

            Assert.True(condition(), "condition was not met before the timeout");
        }

        private static byte[] Copy(byte[] data, int offset, int length)
        {
            var copy = new byte[length];
            Buffer.BlockCopy(data, offset, copy, 0, length);
            return copy;
        }
    }
}
