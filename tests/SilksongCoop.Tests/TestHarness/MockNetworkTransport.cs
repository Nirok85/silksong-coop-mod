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
        #region Nested Types

        /// <summary>
        /// Represents a packet queued for delivery in the mock transport.
        /// </summary>
        public struct QueuedPacket
        {
            public long DeliveryTimeTicks;
            public byte Channel;
            public DeliveryMethod Method;
            public uint Sequence;
            public byte[] Data;
            public NetworkPeerId Sender;
        }

        #endregion

        #region Fields

        private MockNetworkTransport? _peer;
        private readonly List<QueuedPacket> _incomingQueue = new();
        private readonly object _lock = new();
        private readonly Random _random;

        private readonly Dictionary<(byte Channel, DeliveryMethod Method), uint> _sendSequences = new();
        private readonly Dictionary<(byte Channel, DeliveryMethod Method), uint> _receivedSequences = new();
        private readonly Dictionary<byte, SortedDictionary<uint, QueuedPacket>> _reliablePending = new();

        #endregion

        #region Events

        public event Action<NetworkPeerId>? OnPeerConnected;
        public event Action<NetworkPeerId, DisconnectReason>? OnPeerDisconnected;
        public event Action<NetworkPeerId, byte[], int, int>? OnDataReceived;

        #endregion

        #region Properties

        public bool IsHost { get; private set; }
        public bool IsConnected { get; private set; }
        public NetworkPeerId LocalPeerId { get; private set; }
        public System.Collections.Generic.IReadOnlyList<NetworkPeerId> ConnectedPeers =>
            IsConnected && _peer != null
                ? new System.Collections.Generic.List<NetworkPeerId> { _peer.LocalPeerId }
                : new System.Collections.Generic.List<NetworkPeerId>();

        /// <summary>Simulated round-trip time (two one-way latencies).</summary>
        public int RoundTripTimeMs => LatencyMs * 2;

        /// <summary>Simulated packet-loss percentage for the configured loss rate.</summary>
        public int PacketLossPercent => (int)Math.Round(PacketLossRate * 100f);

        public int LatencyMs { get; set; }
        public int JitterMs { get; set; }
        public float PacketLossRate { get; set; }

        /// <summary>
        /// The mock delivers packets immediately, so there is never a reliable backlog.
        /// </summary>
        public int ReliableBacklogCount => 0;

        public long CurrentVirtualTimeMs { get; set; }
        public bool UseVirtualTime { get; set; }

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new mock transport instance for testing.
        /// </summary>
        /// <param name="peerId">Numeric peer identifier.</param>
        /// <param name="displayName">Optional display name for the peer.</param>
        /// <param name="seed">Random seed for deterministic packet loss simulation.</param>
        public MockNetworkTransport(ulong peerId, string displayName = "Peer", int seed = 1337)
        {
            LocalPeerId = new NetworkPeerId(peerId, displayName);
            _random = new Random(seed);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Creates a paired host and client mock transport with configurable network conditions.
        /// </summary>
        /// <param name="latencyMs">Simulated one-way latency in milliseconds.</param>
        /// <param name="jitterMs">Simulated jitter range in milliseconds.</param>
        /// <param name="lossRate">Packet loss rate for unreliable/sequenced packets (0-1).</param>
        /// <param name="useVirtualTime">Whether to use virtual time for deterministic tests.</param>
        /// <param name="seed">Random seed for reproducibility.</param>
        /// <returns>A tuple of connected host and client transports.</returns>
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

            return (host, client);
        }

        /// <summary>
        /// Sets the paired mock transport for simulating network communication.
        /// </summary>
        /// <param name="peer">The peer transport to communicate with.</param>
        public void SetPeer(MockNetworkTransport peer)
        {
            _peer = peer;
        }

        /// <summary>
        /// Starts the transport in host mode.
        /// </summary>
        /// <param name="port">Unused in mock transport.</param>
        public void StartHost(int port = 7777)
        {
            IsHost = true;
        }

        /// <summary>
        /// Connects to the paired mock transport.
        /// </summary>
        /// <param name="addressOrCode">Unused in mock transport; connection is via paired peer.</param>
        public void Connect(string addressOrCode)
        {
            if (_peer == null)
                throw new InvalidOperationException("No paired mock transport specified for connection.");

            IsConnected = true;
            _peer.IsConnected = true;

            OnPeerConnected?.Invoke(_peer.LocalPeerId);
            _peer.OnPeerConnected?.Invoke(LocalPeerId);
        }

        /// <summary>
        /// Disconnects from the paired mock transport.
        /// </summary>
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

        /// <summary>
        /// Sends data to a target peer through the paired transport.
        /// </summary>
        /// <param name="target">Target peer identifier.</param>
        /// <param name="data">Data buffer to send.</param>
        /// <param name="offset">Offset within the buffer.</param>
        /// <param name="length">Number of bytes to send.</param>
        /// <param name="method">Delivery method (reliable, unreliable, or sequenced).</param>
        /// <param name="channel">Channel index for ordering.</param>
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
                var sequenceKey = (channel, method);
                _sendSequences.TryGetValue(sequenceKey, out seq);
                _sendSequences[sequenceKey] = seq + 1;
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

        /// <summary>
        /// Enqueues an incoming packet for delivery processing.
        /// </summary>
        /// <param name="packet">The packet to enqueue.</param>
        public void EnqueueIncoming(QueuedPacket packet)
        {
            lock (_lock)
            {
                _incomingQueue.Add(packet);
            }
        }

        /// <summary>
        /// Processes queued incoming packets that are ready for delivery.
        /// </summary>
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

        #endregion

        #region Private Methods

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

                var sequenceKey = (channel, DeliveryMethod.ReliableOrdered);
                _receivedSequences.TryGetValue(sequenceKey, out uint expectedSeq);

                if (packet.Sequence == expectedSeq)
                {
                    DispatchPacket(packet);
                    expectedSeq++;
                    _receivedSequences[sequenceKey] = expectedSeq;

                    // Drain subsequent ordered packets
                    while (pending.TryGetValue(expectedSeq, out var nextPkt))
                    {
                        pending.Remove(expectedSeq);
                        DispatchPacket(nextPkt);
                        expectedSeq++;
                        _receivedSequences[sequenceKey] = expectedSeq;
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
                var sequenceKey = (channel, DeliveryMethod.Sequenced);
                if (!_receivedSequences.TryGetValue(sequenceKey, out uint latestSeq))
                {
                    // First packet on this channel is always accepted.
                    _receivedSequences[sequenceKey] = packet.Sequence;
                    DispatchPacket(packet);
                }
                else if (packet.Sequence > latestSeq)
                {
                    _receivedSequences[sequenceKey] = packet.Sequence;
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

        private long GetCurrentTimeMs()
        {
            if (UseVirtualTime)
                return CurrentVirtualTimeMs;

            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Advances virtual time for deterministic testing.
        /// </summary>
        /// <param name="ms">Milliseconds to advance.</param>
        public void AdvanceVirtualTime(long ms)
        {
            CurrentVirtualTimeMs += ms;
        }

        /// <summary>
        /// Disposes the transport, releasing resources and disconnecting.
        /// </summary>
        public void Dispose()
        {
            Disconnect();
        }

        #endregion
    }
}
