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
            using var host = new LiteNetLibTransport(1, "Host");
            using var client = new LiteNetLibTransport(2, "Client");

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
            using var host = new LiteNetLibTransport(1, "Host");
            using var client = new LiteNetLibTransport(2, "Client");
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

        /// <summary>
        /// Minimal UDP relay between one client and the host. Used to force a deterministic
        /// packet loss without touching production transport code.
        /// </summary>
        private sealed class UdpRelay : IDisposable
        {
            private readonly Socket _socket;
            private readonly Thread _thread;
            private volatile bool _running = true;
            private volatile IPEndPoint? _clientEndpoint;
            private IPEndPoint _hostEndpoint = new IPEndPoint(IPAddress.Loopback, 0);
            private int _dropClientDataFrames;

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
                            continue;
                        }

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
