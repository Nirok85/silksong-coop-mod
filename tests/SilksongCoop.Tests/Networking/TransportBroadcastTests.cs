using System;
using System.Collections.Generic;
using SilksongCoop.Core.Networking;
using Xunit;

namespace SilksongCoop.Tests.Networking
{
    public class TransportBroadcastTests
    {
        [Fact]
        public void SendToAll_LegacyTransport_SendsToEveryConnectedPeer()
        {
            var transport = new RecordingTransport(new[]
            {
                new NetworkPeerId(2), new NetworkPeerId(3), new NetworkPeerId(4)
            });

            transport.SendToAll(new byte[8], 0, 8, DeliveryMethod.ReliableOrdered, 5);

            Assert.Equal(3, transport.Sent.Count);
            Assert.Equal(new NetworkPeerId(2), transport.Sent[0].Target);
            Assert.Equal(new NetworkPeerId(3), transport.Sent[1].Target);
            Assert.Equal(new NetworkPeerId(4), transport.Sent[2].Target);
            Assert.All(transport.Sent, sent => Assert.Equal(8, sent.Length));
        }

        [Fact]
        public void SendToAll_MultiPeerTransport_UsesOptimizedBroadcast()
        {
            var transport = new RecordingMultiPeerTransport(new[]
            {
                new NetworkPeerId(2), new NetworkPeerId(3)
            });

            transport.SendToAll(new byte[8], 0, 8, DeliveryMethod.ReliableOrdered, 5);

            Assert.Equal(1, transport.BroadcastCalls);
            Assert.Empty(transport.Sent);
        }

        [Fact]
        public void MultiPeerTransport_ReportsCapabilities()
        {
            var transport = new RecordingMultiPeerTransport(new[]
            {
                new NetworkPeerId(2), new NetworkPeerId(3)
            });

            Assert.Equal(3, transport.MaxRemotePeers);
            Assert.False(transport.SupportsHostRelay);
        }

        [Fact]
        public void SendToAll_AllAccepted_ReturnsTrue()
        {
            var transport = new RecordingTransport(new[]
            {
                new NetworkPeerId(2), new NetworkPeerId(3)
            });

            bool accepted = transport.SendToAll(new byte[8], 0, 8, DeliveryMethod.ReliableOrdered, 5);

            Assert.True(accepted);
            Assert.Equal(2, transport.Sent.Count);
        }

        [Fact]
        public void SendToAll_PartialRefusal_ReturnsFalseButAttemptsEveryPeer()
        {
            var refused = new NetworkPeerId(3);
            var transport = new RecordingTransport(new[]
            {
                new NetworkPeerId(2), refused, new NetworkPeerId(4)
            });
            transport.RefuseTargets.Add(refused);

            bool accepted = transport.SendToAll(new byte[8], 0, 8, DeliveryMethod.ReliableOrdered, 5);

            // The aggregate result must report the refusal: sync managers that mark state
            // "sent" on true would otherwise suppress a batch one peer never received.
            Assert.False(accepted);
            Assert.Equal(3, transport.Sent.Count);
        }

        [Fact]
        public void SendToAll_MultiPeerRefusal_ReturnsFalse()
        {
            var transport = new RecordingMultiPeerTransport(new[] { new NetworkPeerId(2) });
            transport.BroadcastResult = false;

            bool accepted = transport.SendToAll(new byte[8], 0, 8, DeliveryMethod.ReliableOrdered, 5);

            Assert.False(accepted);
            Assert.Equal(1, transport.BroadcastCalls);
        }

        [Fact]
        public void SendToAll_NoPeers_DoesNothing()
        {
            var transport = new RecordingTransport(Array.Empty<NetworkPeerId>());

            bool accepted = transport.SendToAll(new byte[8], 0, 8, DeliveryMethod.Unreliable, 0);

            Assert.False(accepted);
            Assert.Empty(transport.Sent);
        }

        private class RecordingTransport : INetworkTransport
        {
            private readonly IReadOnlyList<NetworkPeerId> _peers;

            public RecordingTransport(IReadOnlyList<NetworkPeerId> peers)
            {
                _peers = peers;
            }

            public List<(NetworkPeerId Target, int Length)> Sent { get; } = new();

            /// <summary>Targets whose Send must report a refusal (test-only backpressure).</summary>
            public HashSet<NetworkPeerId> RefuseTargets { get; } = new();

            public bool IsHost => true;
            public bool IsConnected => _peers.Count > 0;
            public NetworkPeerId LocalPeerId => new NetworkPeerId(1);
            public IReadOnlyList<NetworkPeerId> ConnectedPeers => _peers;
            public int RoundTripTimeMs => 10;
            public int PacketLossPercent => 0;
            public int ReliableBacklogCount => 0;

            public event Action<NetworkPeerId>? OnPeerConnected;
            public event Action<NetworkPeerId, DisconnectReason>? OnPeerDisconnected;
            public event Action<NetworkPeerId, byte[], int, int>? OnDataReceived;

            public void StartHost(int port = 7777)
            {
                OnPeerConnected?.Invoke(LocalPeerId);
            }

            public void Connect(string addressOrCode) { }

            public void Disconnect()
            {
                OnPeerDisconnected?.Invoke(LocalPeerId, DisconnectReason.GracefulDisconnect);
            }

            public void PollEvents() { }

            public void Deliver(NetworkPeerId sender, byte[] data)
            {
                OnDataReceived?.Invoke(sender, data, 0, data.Length);
            }

            public virtual bool Send(
                NetworkPeerId target,
                byte[] data,
                int offset,
                int length,
                DeliveryMethod method,
                byte channel)
            {
                Sent.Add((target, length));
                return !RefuseTargets.Contains(target);
            }

            public void Dispose() { }
        }

        private sealed class RecordingMultiPeerTransport : RecordingTransport, IMultiPeerTransport
        {
            public RecordingMultiPeerTransport(IReadOnlyList<NetworkPeerId> peers) : base(peers) { }

            public int BroadcastCalls { get; private set; }

            /// <summary>Result returned by the next Broadcast calls (test-only refusal).</summary>
            public bool BroadcastResult { get; set; } = true;

            public int MaxRemotePeers => 3;
            public bool SupportsHostRelay => false;

            public bool Broadcast(byte[] data, int offset, int length, DeliveryMethod method, byte channel)
            {
                BroadcastCalls++;
                return BroadcastResult;
            }
        }
    }
}
