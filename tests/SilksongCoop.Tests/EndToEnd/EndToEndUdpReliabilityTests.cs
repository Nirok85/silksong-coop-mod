using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using SilksongCoop.Core.Networking;
using Xunit;

namespace SilksongCoop.Tests.EndToEnd
{
    /// <summary>
    /// Real-socket reliability tests that the original end-to-end suite did not cover:
    /// reconnecting to the SAME host, and a deliberately dropped reliable datagram that
    /// must be retransmitted exactly once.
    /// </summary>
    public class EndToEndUdpReliabilityTests
    {
        [Fact]
        public void Client_Reconnects_ToTheSameHost_AndExchangesData()
        {
            using var host = new LiteNetLibTransport(1, "Host") { RequireRoomCode = false };
            using var client = new LiteNetLibTransport(2, "Client") { RequireRoomCode = false };

            host.StartHost(0);
            int hostPort = host.BoundPort;
            client.Connect($"127.0.0.1:{hostPort}");
            PumpUntil(() => host.IsConnected && client.IsConnected, host, client, maxMs: 3000);

            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);

            client.Disconnect(DisconnectReason.GracefulDisconnect);
            PumpUntil(() => !host.IsConnected && !client.IsConnected, host, client, maxMs: 2000);
            Assert.False(client.IsConnected);

            // Reconnect to the SAME host/port. A fresh host would hide stale endpoint or
            // session state bugs, which is exactly what the old test did.
            client.Connect($"127.0.0.1:{hostPort}");
            PumpUntil(() => host.IsConnected && client.IsConnected, host, client, maxMs: 3000);

            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);
            Assert.Equal(client.LocalPeerId, host.RemotePeerId);
            Assert.Equal(host.LocalPeerId, client.RemotePeerId);

            var received = new List<int>();
            host.OnDataReceived += (peer, data, offset, length) =>
            {
                if (length >= 4)
                {
                    lock (received) received.Add(BitConverter.ToInt32(data, offset));
                }
            };

            byte[] payload = BitConverter.GetBytes(4242);
            client.Send(host.LocalPeerId, payload, 0, payload.Length, DeliveryMethod.ReliableOrdered, 1);

            PumpUntil(() => { lock (received) { return received.Count > 0; } }, host, client, maxMs: 3000);

            lock (received)
            {
                Assert.Contains(4242, received);
            }
        }

        [Fact]
        public void DroppedReliableDatagram_IsRetransmittedExactlyOnce()
        {
            using var host = new LiteNetLibTransport(1, "Host") { RequireRoomCode = false };
            using var client = new LiteNetLibTransport(2, "Client") { RequireRoomCode = false };
            using var relay = new UdpRelay();

            host.StartHost(0);
            relay.ConfigureHost(host.BoundPort);
            client.Connect($"127.0.0.1:{relay.Port}");
            PumpUntil(() => host.IsConnected && client.IsConnected, host, client, maxMs: 5000);
            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);

            var received = new List<int>();
            host.OnDataReceived += (peer, data, offset, length) =>
            {
                if (length >= 4)
                {
                    lock (received) received.Add(BitConverter.ToInt32(data, offset));
                }
            };

            // Drop exactly the next client-to-host Data frame. The transport must retry it.
            relay.DropNextClientDataFrames(1);

            byte[] payload = BitConverter.GetBytes(777);
            client.Send(host.LocalPeerId, payload, 0, payload.Length, DeliveryMethod.ReliableOrdered, 1);

            PumpUntil(() => { lock (received) { return received.Count > 0; } }, host, client, maxMs: 5000);

            // Keep pumping briefly to catch a duplicate delivery.
            var deadline = DateTime.UtcNow.AddMilliseconds(500);
            while (DateTime.UtcNow < deadline)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(5);
            }

            lock (received)
            {
                Assert.Single(received.FindAll(value => value == 777));
            }
        }

        [Fact]
        public void AuthenticatedBackpressure_RetriesDroppedFramesAndDrainsInOrder()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            using var client = new LiteNetLibTransport(2, "Client") { QueueReliableWhenBusy = true };
            using var relay = new UdpRelay();
            host.StartHost(0);
            relay.ConfigureHost(host.BoundPort);
            var code = RoomCodeCodec.EncodeWithSecret(IPAddress.Loopback, relay.Port);
            host.SetRoomCode(code);
            client.Connect(code);
            PumpUntil(() => host.IsConnected && client.IsConnected, host, client, maxMs: 3000);
            Assert.True(host.IsConnected && client.IsConnected);

            var delivered = new List<int>();
            host.OnDataReceived += (_, data, offset, length) =>
            {
                if (length == sizeof(int)) delivered.Add(BitConverter.ToInt32(data, offset));
            };

            relay.DropNextClientDataFrames(16);
            int total = NetworkLimits.ReliableBacklogCap + 32;
            for (int i = 0; i < total; i++)
            {
                var payload = BitConverter.GetBytes(i);
                client.Send(host.LocalPeerId, payload, 0, payload.Length,
                    DeliveryMethod.ReliableOrdered, NetworkChannels.World);
            }

            Assert.Equal(NetworkLimits.ReliableBacklogCap, client.ReliableBacklogCount);
            PumpUntil(() => delivered.Count == total && client.ReliableBacklogCount == 0,
                host, client, maxMs: 8000);
            Assert.Equal(System.Linq.Enumerable.Range(0, total), delivered);
            Assert.Equal(0, client.DroppedReliablePackets);
        }

        [Fact]
        public void AuthenticatedUnreliableDatagram_IsAppliedOnlyOnce()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            using var client = new LiteNetLibTransport(2, "Client");
            using var relay = new UdpRelay();
            host.StartHost(0);
            relay.ConfigureHost(host.BoundPort);
            var code = RoomCodeCodec.EncodeWithSecret(IPAddress.Loopback, relay.Port);
            host.SetRoomCode(code);
            client.Connect(code);
            PumpUntil(() => host.IsConnected && client.IsConnected, host, client, maxMs: 3000);
            Assert.True(host.IsConnected && client.IsConnected);

            int delivered = 0;
            host.OnDataReceived += (_, _, _, _) => delivered++;
            relay.DuplicateNextClientDataFrame();
            client.Send(host.LocalPeerId, new byte[] { 42 }, 0, 1,
                DeliveryMethod.Unreliable, NetworkChannels.Transform);
            PumpUntil(() => delivered > 0, host, client, maxMs: 1000);
            for (int i = 0; i < 10; i++)
            {
                host.PollEvents();
                Thread.Sleep(5);
            }
            Assert.Equal(1, delivered);
        }

        [Fact]
        public void OversizeAndInvalidMacFrames_DoNotBlockValidReliableTraffic()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            using var client = new LiteNetLibTransport(2, "Client");
            using var relay = new UdpRelay();
            host.StartHost(0);
            relay.ConfigureHost(host.BoundPort);
            var code = RoomCodeCodec.EncodeWithSecret(IPAddress.Loopback, relay.Port);
            host.SetRoomCode(code);
            client.Connect(code);
            PumpUntil(() => host.IsConnected && client.IsConnected, host, client, maxMs: 3000);
            Assert.True(host.IsConnected && client.IsConnected);

            const int maxAuthenticatedPayload = 2048 - 8 - 16;
            var payload = new byte[maxAuthenticatedPayload + 1];
            Assert.False(client.Send(host.LocalPeerId, payload, 0, payload.Length,
                DeliveryMethod.ReliableOrdered, NetworkChannels.World));
            Assert.Equal(0, client.ReliableBacklogCount);

            // Inject on the connected endpoint. 64 bad MACs previously stopped valid
            // traffic for 100 ms. The oversize frame must also leave the receive loop alive.
            var badMac = new byte[8 + 16];
            badMac[0] = 0x03;
            badMac[7] = 1;
            for (int i = 0; i < 64; i++) relay.InjectToHost(badMac);
            relay.InjectToHost(new byte[4096]);

            var lengths = new List<int>();
            host.OnDataReceived += (_, _, _, length) => lengths.Add(length);
            Assert.True(client.Send(host.LocalPeerId, payload, 0, maxAuthenticatedPayload,
                DeliveryMethod.ReliableOrdered, NetworkChannels.World));
            // Poll until the relay has forwarded the first valid send. Do not poll the
            // client, so a retransmission cannot hide a rejected first attempt.
            var deadline = DateTime.UtcNow.AddMilliseconds(2000);
            while (lengths.Count == 0 && DateTime.UtcNow < deadline)
            {
                host.PollEvents();
                Thread.Sleep(1);
            }
            Assert.Equal(new[] { maxAuthenticatedPayload }, lengths);
        }

        internal static void PumpUntil(Func<bool> condition, LiteNetLibTransport host, LiteNetLibTransport client, int maxMs)
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

        /// <summary>
        /// Minimal UDP relay between one client and the host. Used to force a deterministic
        /// packet loss without touching production transport code.
        /// </summary>
        internal sealed class UdpRelay : IDisposable
        {
            private readonly Socket _socket;
            private readonly Thread _thread;
            private volatile bool _running = true;
            private volatile IPEndPoint? _clientEndpoint;
            private IPEndPoint _hostEndpoint = new IPEndPoint(IPAddress.Loopback, 0);
            private int _dropClientDataFrames;
            private int _droppedDataFrames;

            public int DroppedDataFrames => Volatile.Read(ref _droppedDataFrames);

            public void InjectToHost(byte[] frame) => _socket.SendTo(frame, _hostEndpoint);
            private int _duplicateNextClientDataFrame;

            public void DuplicateNextClientDataFrame() => Interlocked.Exchange(ref _duplicateNextClientDataFrame, 1);

            public UdpRelay()
            {
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;

                _thread = new Thread(Loop) { IsBackground = true, Name = "SilksongCoop-UdpRelay" };
                _thread.Start();
            }

            public int Port { get; }

            public void ConfigureHost(int hostPort)
            {
                _hostEndpoint = new IPEndPoint(IPAddress.Loopback, hostPort);
            }

            public void DropNextClientDataFrames(int count)
            {
                Interlocked.Exchange(ref _dropClientDataFrames, Math.Max(0, count));
            }

            private void Loop()
            {
                byte[] buffer = new byte[65535];
                while (_running)
                {
                    try
                    {
                        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                        int length = _socket.ReceiveFrom(buffer, ref remote);
                        var endpoint = (IPEndPoint)remote;

                        if (endpoint.Port == _hostEndpoint.Port)
                        {
                            var client = _clientEndpoint;
                            if (client != null)
                            {
                                _socket.SendTo(buffer, 0, length, SocketFlags.None, client);
                            }
                            continue;
                        }

                        _clientEndpoint = endpoint;

                        // Frame type 0x03 is Data; drop before forwarding so the sender's
                        // retransmit timer sees no ACK.
                        if (buffer.Length > 0 && buffer[0] == 0x03 &&
                            Volatile.Read(ref _dropClientDataFrames) > 0)
                        {
                            Interlocked.Decrement(ref _dropClientDataFrames);
                            Interlocked.Increment(ref _droppedDataFrames);
                            continue;
                        }

                        _socket.SendTo(buffer, 0, length, SocketFlags.None, _hostEndpoint);
                        if (length > 0 && buffer[0] == 0x03 &&
                            Interlocked.Exchange(ref _duplicateNextClientDataFrame, 0) != 0)
                            _socket.SendTo(buffer, 0, length, SocketFlags.None, _hostEndpoint);
                    }
                    catch (SocketException)
                    {
                        // Transient; the loop ends on Dispose.
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                }
            }

            public void Dispose()
            {
                _running = false;
                try { _socket.Close(); } catch { /* best-effort */ }
                try { _thread.Join(1000); } catch { /* best-effort */ }
                _socket.Dispose();
            }
        }
    }
}
