using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SilksongCoop.Core.Networking
{
    /// <summary>
    /// Deterministic in-memory simulated transport for automated headless testing.
    /// Simulates latency, jitter, packet loss, and multi-channel ordering.
    /// </summary>
    public class MockNetworkTransport : INetworkTransport
    {
        public struct QueuedPacket
        {
            public long DeliveryTimeTicks;
            public byte Channel;
            public DeliveryMethod Method;
            public uint Sequence;
            public byte[] Data;
            public NetworkPeerId Sender;
        }

        private MockNetworkTransport? _peer;
        private readonly List<QueuedPacket> _incomingQueue = new();
        private readonly object _lock = new();
        private readonly Random _random;

        private readonly Dictionary<byte, uint> _sendSequences = new();
        private readonly Dictionary<byte, uint> _receivedSequences = new();
        private readonly Dictionary<byte, SortedDictionary<uint, QueuedPacket>> _reliablePending = new();

        public event Action<NetworkPeerId>? OnPeerConnected;
        public event Action<NetworkPeerId, DisconnectReason>? OnPeerDisconnected;
        public event Action<NetworkPeerId, byte[], int, int>? OnDataReceived;

        public bool IsHost { get; private set; }
        public bool IsConnected { get; private set; }
        public NetworkPeerId LocalPeerId { get; private set; }

        public int LatencyMs { get; set; }
        public int JitterMs { get; set; }
        public float PacketLossRate { get; set; }

        public long CurrentVirtualTimeMs { get; set; }
        public bool UseVirtualTime { get; set; }

        public MockNetworkTransport(ulong peerId, string displayName = "Peer", int seed = 1337)
        {
            LocalPeerId = new NetworkPeerId(peerId, displayName);
            _random = new Random(seed);
        }

        public static (MockNetworkTransport Host, MockNetworkTransport Client) CreatePair(
            int latencyMs = 0,
            int jitterMs = 0,
            float lossRate = 0f,
            bool useVirtualTime = false,
            int seed = 42)
        {
            var host = new MockNetworkTransport(1, "Host", seed)
            {
                LatencyMs = latencyMs,
                JitterMs = jitterMs,
                PacketLossRate = lossRate,
                UseVirtualTime = useVirtualTime
            };

            var client = new MockNetworkTransport(2, "Client", seed + 1)
            {
                LatencyMs = latencyMs,
                JitterMs = jitterMs,
                PacketLossRate = lossRate,
                UseVirtualTime = useVirtualTime
            };

            host.StartHost();
            host.SetPeer(client);
            client.SetPeer(host);
            client.Connect("host");

            // Complete immediate connection handshake
            host.IsConnected = true;
            client.IsConnected = true;
            host.OnPeerConnected?.Invoke(client.LocalPeerId);
            client.OnPeerConnected?.Invoke(host.LocalPeerId);

            return (host, client);
        }

        public void SetPeer(MockNetworkTransport peer)
        {
            _peer = peer;
        }

        public void StartHost(int port = 7777)
        {
            IsHost = true;
        }

        public void Connect(string addressOrCode)
        {
            if (_peer == null)
                throw new InvalidOperationException("No paired mock transport specified for connection.");

            IsConnected = true;
            _peer.IsConnected = true;

            OnPeerConnected?.Invoke(_peer.LocalPeerId);
            _peer.OnPeerConnected?.Invoke(LocalPeerId);
        }

        public void Disconnect()
        {
            if (!IsConnected) return;

            IsConnected = false;
            var peer = _peer;
            if (peer != null && peer.IsConnected)
            {
                peer.IsConnected = false;
                peer.OnPeerDisconnected?.Invoke(LocalPeerId, DisconnectReason.GracefulDisconnect);
            }
            OnPeerDisconnected?.Invoke(peer != null ? peer.LocalPeerId : new NetworkPeerId(0), DisconnectReason.GracefulDisconnect);
        }

        public void Send(NetworkPeerId target, byte[] data, int offset, int length, DeliveryMethod method, byte channel)
        {
            if (!IsConnected || _peer == null) return;

            // Packet loss simulation (only for unreliable or sequenced)
            if ((method == DeliveryMethod.Unreliable || method == DeliveryMethod.Sequenced) && PacketLossRate > 0)
            {
                if (_random.NextDouble() < PacketLossRate)
                    return; // Dropped
            }

            uint seq;
            lock (_sendSequences)
            {
                _sendSequences.TryGetValue(channel, out seq);
                _sendSequences[channel] = seq + 1;
            }

            byte[] buffer = new byte[length];
            Buffer.BlockCopy(data, offset, buffer, 0, length);

            int jitter = 0;
            if (JitterMs > 0)
            {
                jitter = _random.Next(-JitterMs, JitterMs + 1);
            }

            int delay = Math.Max(0, LatencyMs + jitter);
            long now = GetCurrentTimeMs();
            long deliveryTime = now + delay;

            var packet = new QueuedPacket
            {
                DeliveryTimeTicks = deliveryTime,
                Channel = channel,
                Method = method,
                Sequence = seq,
                Data = buffer,
                Sender = LocalPeerId
            };

            _peer.EnqueueIncoming(packet);
        }

        public void EnqueueIncoming(QueuedPacket packet)
        {
            lock (_lock)
            {
                _incomingQueue.Add(packet);
            }
        }

        public void PollEvents()
        {
            if (!IsConnected) return;

            long now = GetCurrentTimeMs();
            List<QueuedPacket> readyPackets = new();

            lock (_lock)
            {
                for (int i = 0; i < _incomingQueue.Count; i++)
                {
                    if (_incomingQueue[i].DeliveryTimeTicks <= now)
                    {
                        readyPackets.Add(_incomingQueue[i]);
                    }
                }
                _incomingQueue.RemoveAll(p => p.DeliveryTimeTicks <= now);
            }

            // Sort ready packets by sequence if reliable ordered
            foreach (var packet in readyPackets)
            {
                ProcessPacket(packet);
            }
        }

        private void ProcessPacket(QueuedPacket packet)
        {
            byte channel = packet.Channel;

            if (packet.Method == DeliveryMethod.ReliableOrdered)
            {
                if (!_reliablePending.TryGetValue(channel, out var pending))
                {
                    pending = new SortedDictionary<uint, QueuedPacket>();
                    _reliablePending[channel] = pending;
                }

                _receivedSequences.TryGetValue(channel, out uint expectedSeq);

                if (packet.Sequence == expectedSeq)
                {
                    DispatchPacket(packet);
                    expectedSeq++;
                    _receivedSequences[channel] = expectedSeq;

                    // Drain subsequent ordered packets
                    while (pending.TryGetValue(expectedSeq, out var nextPkt))
                    {
                        pending.Remove(expectedSeq);
                        DispatchPacket(nextPkt);
                        expectedSeq++;
                        _receivedSequences[channel] = expectedSeq;
                    }
                }
                else if (packet.Sequence > expectedSeq)
                {
                    // Stash out-of-order packet
                    pending[packet.Sequence] = packet;
                }
                // Old duplicate reliable packets are discarded
            }
            else if (packet.Method == DeliveryMethod.Sequenced)
            {
                _receivedSequences.TryGetValue(channel, out uint latestSeq);
                if (packet.Sequence >= latestSeq)
                {
                    _receivedSequences[channel] = packet.Sequence + 1;
                    DispatchPacket(packet);
                }
                // Drop out-of-order old sequenced packet
            }
            else
            {
                // Unreliable
                DispatchPacket(packet);
            }
        }

        private void DispatchPacket(QueuedPacket packet)
        {
            OnDataReceived?.Invoke(packet.Sender, packet.Data, 0, packet.Data.Length);
        }

        public void AdvanceVirtualTime(long ms)
        {
            CurrentVirtualTimeMs += ms;
        }

        private long GetCurrentTimeMs()
        {
            if (UseVirtualTime)
                return CurrentVirtualTimeMs;

            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
