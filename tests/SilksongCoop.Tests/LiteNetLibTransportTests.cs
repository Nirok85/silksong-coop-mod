using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SilksongCoop.Core.Networking;
using Xunit;

namespace SilksongCoop.Tests
{
    public class LiteNetLibTransportTests
    {
        [Fact]
        public void LiteNetLibTransport_LoopbackHandshake_ConnectsBothPeersAndSetsBoundPort()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            using var client = new LiteNetLibTransport(2, "Client");

            host.StartHost(0); // Auto-bind ephemeral port
            Assert.True(host.BoundPort > 0);
            Assert.True(host.IsHost);
            Assert.False(host.IsConnected);

            client.Connect($"127.0.0.1:{host.BoundPort}");
            Assert.False(client.IsHost);

            for (int i = 0; i < 30 && (!host.IsConnected || !client.IsConnected); i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(10);
            }

            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);
            Assert.True(host.RemotePeerId.HasValue);
            Assert.True(client.RemotePeerId.HasValue);
            Assert.Equal((ulong)2, host.RemotePeerId.Value.Value);
            Assert.Equal((ulong)1, client.RemotePeerId.Value.Value);
        }

        [Fact]
        public void LiteNetLibTransport_ReliableOrdered_DeliversInSequenceAndAcknowledges()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            using var client = new LiteNetLibTransport(2, "Client");

            host.StartHost(0);
            client.Connect($"127.0.0.1:{host.BoundPort}");

            for (int i = 0; i < 30 && (!host.IsConnected || !client.IsConnected); i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(10);
            }

            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);

            var receivedNumbers = new List<int>();
            client.OnDataReceived += (peer, data, offset, len) =>
            {
                receivedNumbers.Add(BitConverter.ToInt32(data, offset));
            };

            for (int i = 0; i < 20; i++)
            {
                byte[] payload = BitConverter.GetBytes(i);
                host.Send(client.LocalPeerId, payload, 0, payload.Length, DeliveryMethod.ReliableOrdered, 1);
            }

            for (int i = 0; i < 40 && receivedNumbers.Count < 20; i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(5);
            }

            Assert.Equal(20, receivedNumbers.Count);
            for (int i = 0; i < 20; i++)
            {
                Assert.Equal(i, receivedNumbers[i]);
            }
        }

        [Fact]
        public void LiteNetLibTransport_DisconnectInsidePeerDisconnectedCallback_DoesNotThrow()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            using var client = new LiteNetLibTransport(2, "Client");

            host.StartHost(0);
            client.Connect($"127.0.0.1:{host.BoundPort}");

            for (int i = 0; i < 30 && (!host.IsConnected || !client.IsConnected); i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(10);
            }

            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);

            bool callbackInvoked = false;
            client.OnPeerDisconnected += (peer, reason) =>
            {
                callbackInvoked = true;
                client.Disconnect(); // Reentrant disconnect
            };

            host.Disconnect(); // Sends Disconnect envelope

            var ex = Record.Exception(() =>
            {
                for (int i = 0; i < 15; i++)
                {
                    client.PollEvents();
                    Thread.Sleep(5);
                }
            });

            Assert.Null(ex);
            Assert.True(callbackInvoked);
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void LiteNetLibTransport_SendAfterDisconnect_HandledSafelyWithoutException()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            host.StartHost(0);
            host.Disconnect();

            byte[] data = new byte[8];
            var ex = Record.Exception(() => host.Send(new NetworkPeerId(2), data, 0, data.Length, DeliveryMethod.ReliableOrdered, 1));
            Assert.Null(ex);
        }

        [Fact]
        public void LiteNetLibTransport_MalformedDatagramShortHeader_IgnoredWithoutCrash()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            host.StartHost(0);

            // Send raw UDP packet smaller than minimum envelope header (8 bytes)
            using var rawSender = new UdpClient();
            byte[] tinyPayload = new byte[4] { 0x01, 0x02, 0x03, 0x04 };
            rawSender.Send(tinyPayload, tinyPayload.Length, "127.0.0.1", host.BoundPort);

            Thread.Sleep(15);
            var ex = Record.Exception(() => host.PollEvents());
            Assert.Null(ex);
        }

        [Fact]
        public void LiteNetLibTransport_UnreliableDelivery_TransmitsDataAcrossPeers()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            using var client = new LiteNetLibTransport(2, "Client");

            host.StartHost(0);
            client.Connect($"127.0.0.1:{host.BoundPort}");

            for (int i = 0; i < 30 && (!host.IsConnected || !client.IsConnected); i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(10);
            }

            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);

            byte[] receivedData = Array.Empty<byte>();
            host.OnDataReceived += (peer, data, offset, len) =>
            {
                receivedData = new byte[len];
                Buffer.BlockCopy(data, offset, receivedData, 0, len);
            };

            byte[] sentData = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE };
            client.Send(host.LocalPeerId, sentData, 0, sentData.Length, DeliveryMethod.Unreliable, 0);

            for (int i = 0; i < 20 && receivedData.Length == 0; i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(5);
            }

            Assert.Equal(sentData, receivedData);
        }

        [Fact]
        public void LiteNetLibTransport_SequencedDelivery_DiscardsOutdatedSequences()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            using var client = new LiteNetLibTransport(2, "Client");

            host.StartHost(0);
            client.Connect($"127.0.0.1:{host.BoundPort}");

            for (int i = 0; i < 30 && (!host.IsConnected || !client.IsConnected); i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(10);
            }

            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);

            var receivedPackets = new List<int>();
            host.OnDataReceived += (peer, data, offset, len) =>
            {
                receivedPackets.Add(BitConverter.ToInt32(data, offset));
            };

            // Send sequenced packets on channel 0
            for (int i = 0; i < 5; i++)
            {
                byte[] payload = BitConverter.GetBytes(i);
                client.Send(host.LocalPeerId, payload, 0, payload.Length, DeliveryMethod.Sequenced, 0);
            }

            for (int i = 0; i < 20 && receivedPackets.Count < 5; i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(5);
            }

            Assert.NotEmpty(receivedPackets);
            // Verify monotonically increasing sequences
            for (int i = 1; i < receivedPackets.Count; i++)
            {
                Assert.True(receivedPackets[i] > receivedPackets[i - 1]);
            }
        }

        [Fact]
        public void LiteNetLibTransport_PingPong_CalculatesRoundTripTime()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            using var client = new LiteNetLibTransport(2, "Client");

            host.StartHost(0);
            client.Connect($"127.0.0.1:{host.BoundPort}");

            for (int i = 0; i < 30 && (!host.IsConnected || !client.IsConnected); i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(10);
            }

            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);

            // Default initial RTT is 30ms, must stay in valid bound [0, 2000]
            Assert.InRange(host.RoundTripTimeMs, 0, 2000);
            Assert.InRange(client.RoundTripTimeMs, 0, 2000);
        }

        [Fact]
        public void LiteNetLibTransport_ReconnectSession_ResetsSequencesAndReestablishesCleanly()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            using var client = new LiteNetLibTransport(2, "Client");

            host.StartHost(0);
            client.Connect($"127.0.0.1:{host.BoundPort}");

            for (int i = 0; i < 30 && (!host.IsConnected || !client.IsConnected); i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(10);
            }

            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);

            // Send initial reliable packet
            byte[] msg1 = new byte[] { 1, 2, 3 };
            client.Send(host.LocalPeerId, msg1, 0, msg1.Length, DeliveryMethod.ReliableOrdered, 1);

            for (int i = 0; i < 10; i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(5);
            }

            // Client disconnects
            client.Disconnect();
            Assert.False(client.IsConnected);

            // Drain disconnect event on host
            for (int i = 0; i < 10; i++)
            {
                host.PollEvents();
                Thread.Sleep(5);
            }

            // Reconnect client to same host on new ephemeral port
            client.Connect($"127.0.0.1:{host.BoundPort}");

            for (int i = 0; i < 30 && (!host.IsConnected || !client.IsConnected); i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(10);
            }

            Assert.True(host.IsConnected, "Host must accept reconnection");
            Assert.True(client.IsConnected, "Client must be connected after reconnect");

            // Verify reliable transmission works on reconnected session without sequence desync
            bool receivedOnHost = false;
            host.OnDataReceived += (peer, data, offset, len) =>
            {
                if (len == 4 && BitConverter.ToInt32(data, offset) == 42)
                {
                    receivedOnHost = true;
                }
            };

            byte[] msg2 = BitConverter.GetBytes(42);
            client.Send(host.LocalPeerId, msg2, 0, msg2.Length, DeliveryMethod.ReliableOrdered, 1);

            for (int i = 0; i < 20 && !receivedOnHost; i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(5);
            }

            Assert.True(receivedOnHost, "Host must receive reliable packet in reconnected session");
        }

        [Fact]
        public async Task LiteNetLibTransport_ConcurrentSendAndPoll_ThreadSafety()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            using var client = new LiteNetLibTransport(2, "Client");

            host.StartHost(0);
            client.Connect($"127.0.0.1:{host.BoundPort}");

            for (int i = 0; i < 30 && (!host.IsConnected || !client.IsConnected); i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(10);
            }

            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);

            var exceptions = new ConcurrentBag<Exception>();
            using var cts = new CancellationTokenSource(1500);
            var token = cts.Token;

            var t1 = Task.Run(() =>
            {
                try
                {
                    byte[] data = new byte[8];
                    while (!token.IsCancellationRequested)
                    {
                        client.Send(host.LocalPeerId, data, 0, data.Length, DeliveryMethod.Unreliable, 0);
                        Thread.Sleep(2);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            var t2 = Task.Run(() =>
            {
                try
                {
                    byte[] data = new byte[8];
                    while (!token.IsCancellationRequested)
                    {
                        host.Send(client.LocalPeerId, data, 0, data.Length, DeliveryMethod.Unreliable, 0);
                        Thread.Sleep(2);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            var t3 = Task.Run(() =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        client.PollEvents();
                        Thread.Sleep(1);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            var t4 = Task.Run(() =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        host.PollEvents();
                        Thread.Sleep(1);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            await Task.WhenAll(t1, t2, t3, t4);

            Assert.Empty(exceptions);
        }

        [Fact]
        public void LiteNetLibTransport_ReliableUnordered_DeliversAndDrainsBacklog()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            using var client = new LiteNetLibTransport(2, "Client");

            host.StartHost(0);
            client.Connect($"127.0.0.1:{host.BoundPort}");

            for (int i = 0; i < 30 && (!host.IsConnected || !client.IsConnected); i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(10);
            }

            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);

            var received = new List<int>();
            client.OnDataReceived += (_, data, offset, _) =>
            {
                lock (received) received.Add(BitConverter.ToInt32(data, offset));
            };

            for (int i = 0; i < 10; i++)
            {
                byte[] payload = BitConverter.GetBytes(i);
                host.Send(client.LocalPeerId, payload, 0, payload.Length, DeliveryMethod.ReliableUnordered, 2);
            }

            for (int i = 0; i < 100; i++)
            {
                host.PollEvents();
                client.PollEvents();
                lock (received)
                {
                    if (received.Count == 10) break;
                }
                Thread.Sleep(5);
            }

            lock (received)
            {
                Assert.Equal(10, received.Count);
            }

            // The ACKs must retire the ReliableUnordered sends from the retransmit list;
            // otherwise every packet would be retried until the session times out.
            var unacknowledgedField = typeof(LiteNetLibTransport).GetField(
                "_unacknowledged", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(unacknowledgedField);

            int pending = int.MaxValue;
            for (int i = 0; i < 100 && pending != 0; i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(5);
                object list = unacknowledgedField!.GetValue(host)!;
                pending = (int)list.GetType().GetProperty("Count")!.GetValue(list)!;
            }

            Assert.Equal(0, pending);
        }

        [Fact]
        public void LiteNetLibTransport_StaleGenerationEvent_IsNotDispatched()
        {
            using var transport = new LiteNetLibTransport(1, "Host");
            transport.StartHost(0);

            int disconnected = 0;
            transport.OnPeerDisconnected += (_, _) => disconnected++;

            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var eventType = typeof(LiteNetLibTransport).GetNestedType("DeferredEvent", BindingFlags.NonPublic);
            var kindType = typeof(LiteNetLibTransport).GetNestedType("DeferredEventKind", BindingFlags.NonPublic);
            var queueField = typeof(LiteNetLibTransport).GetField("_pendingEvents", flags);
            var generationField = typeof(LiteNetLibTransport).GetField("_sessionGeneration", flags);
            var drain = typeof(LiteNetLibTransport).GetMethod("DrainPendingEvents", flags);

            Assert.NotNull(eventType);
            Assert.NotNull(kindType);
            Assert.NotNull(queueField);
            Assert.NotNull(generationField);
            Assert.NotNull(drain);

            object peerDisconnected = Enum.Parse(kindType!, "PeerDisconnected");
            ConstructorInfo? ctor = null;
            foreach (var candidate in eventType!.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (candidate.GetParameters().Length == 5)
                {
                    ctor = candidate;
                    break;
                }
            }
            Assert.NotNull(ctor);

            object queue = queueField!.GetValue(transport)!;
            var enqueue = queue.GetType().GetMethod("Enqueue")!;

            long staleGeneration = (long)generationField!.GetValue(transport)! - 1;
            object stale = ctor!.Invoke(new object?[]
            {
                peerDisconnected, new NetworkPeerId(2), DisconnectReason.Timeout, null, staleGeneration
            });
            enqueue.Invoke(queue, new[] { stale });
            drain!.Invoke(transport, null);
            Assert.Equal(0, disconnected);

            long currentGeneration = (long)generationField.GetValue(transport)!;
            object current = ctor.Invoke(new object?[]
            {
                peerDisconnected, new NetworkPeerId(2), DisconnectReason.Timeout, null, currentGeneration
            });
            enqueue.Invoke(queue, new[] { current });
            drain.Invoke(transport, null);
            Assert.Equal(1, disconnected);
        }
    }
}
