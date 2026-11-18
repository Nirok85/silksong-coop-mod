using System;
using System.Collections.Generic;
using SilksongCoop.Core.Networking;

namespace SilksongCoop.Tests.TestHarness
{
    /// <summary>
    /// Deterministic in-memory stand-in for SteamP2PTransport. Implements
    /// <see cref="INetworkTransport"/> without touching Steamworks, so channel
    /// routing, RTT and handshake logic can be exercised in unit tests.
    /// </summary>
    public class FakeSteamTransport : INetworkTransport
    {
        public const int SteamHeartbeatTimeoutMs = 15000;

        private FakeSteamTransport? _peer;
        private bool _connected;
        private readonly Queue<(NetworkPeerId Sender, byte[]? Data, byte Channel, bool IsPong)> _incomingQueue = new();

        public bool IsHost { get; }
        public bool IsConnected => _connected;
        public NetworkPeerId LocalPeerId { get; }
        public System.Collections.Generic.IReadOnlyList<NetworkPeerId> ConnectedPeers =>
            _connected && _peer != null
                ? new System.Collections.Generic.List<NetworkPeerId> { _peer.LocalPeerId }
                : new System.Collections.Generic.List<NetworkPeerId>();
        public int RoundTripTimeMs { get; set; } = 30;
        public int PacketLossPercent { get; set; }
        public int ReliableBacklogCount { get; private set; }

        public long LastPongReceivedMs { get; set; }
        public Func<long>? TimeProvider { get; set; }
        private long NowMs() => TimeProvider != null ? TimeProvider() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public event Action<NetworkPeerId>? OnPeerConnected;
        public event Action<NetworkPeerId, DisconnectReason>? OnPeerDisconnected;
        public event Action<NetworkPeerId, byte[], int, int>? OnDataReceived;

        public FakeSteamTransport(ulong peerId, bool isHost)
        {
            LocalPeerId = new NetworkPeerId(peerId);
            IsHost = isHost;
        }

        public static (FakeSteamTransport Host, FakeSteamTransport Client) CreatePair()
        {
            var host = new FakeSteamTransport(1, true);
            var client = new FakeSteamTransport(2, false);
            host.SetPeer(client);
            client.SetPeer(host);
            return (host, client);
        }

        public void SetPeer(FakeSteamTransport peer) => _peer = peer;

        public void EnqueueIncomingPacket(NetworkPeerId sender, byte[] data, byte channel)
        {
            _incomingQueue.Enqueue((sender, data, channel, false));
        }

        public void EnqueueIncomingPong(NetworkPeerId sender)
        {
            _incomingQueue.Enqueue((sender, null, 0, true));
        }

        public void StartHost(int port = 7777)
        {
            _connected = true;
            LastPongReceivedMs = NowMs();
            if (_peer == null) return;

            _peer._connected = true;
            _peer.LastPongReceivedMs = _peer.NowMs();
            OnPeerConnected?.Invoke(_peer.LocalPeerId);
            _peer.OnPeerConnected?.Invoke(LocalPeerId);
        }

        public void Connect(string addressOrCode)
        {
            // Simulated join: host must already be listening.
            if (_peer == null) return;
            _connected = true;
            LastPongReceivedMs = NowMs();
            OnPeerConnected?.Invoke(_peer.LocalPeerId);
        }

        public void Disconnect()
        {
            if (!_connected) return;
            _connected = false;
            if (_peer != null && _peer._connected)
            {
                _peer._connected = false;
                _peer.OnPeerDisconnected?.Invoke(LocalPeerId, DisconnectReason.GracefulDisconnect);
            }
            OnPeerDisconnected?.Invoke(_peer?.LocalPeerId ?? new NetworkPeerId(0), DisconnectReason.GracefulDisconnect);
        }

        public void PollEvents()
        {
            if (!_connected) return;

            // 1. Drain incoming P2P packet queue first so queued pongs/data refresh liveness timestamps
            while (_incomingQueue.Count > 0)
            {
                var (sender, data, channel, isPong) = _incomingQueue.Dequeue();
                LastReceivedChannel = channel;
                LastPongReceivedMs = NowMs();
                if (!isPong && data != null && data.Length > 0)
                {
                    OnDataReceived?.Invoke(sender, data, 0, data.Length);
                }
            }

            // 2. Evaluate heartbeat timeout after draining incoming packets
            long now = NowMs();
            if (LastPongReceivedMs == 0) LastPongReceivedMs = now;
            if (now - LastPongReceivedMs > SteamHeartbeatTimeoutMs)
            {
                _connected = false;
                OnPeerDisconnected?.Invoke(_peer?.LocalPeerId ?? new NetworkPeerId(0), DisconnectReason.Timeout);
            }
        }

        public byte LastReceivedChannel { get; private set; }
        public int SimulatedRttMs { get; set; } = 30;

        public bool Send(NetworkPeerId target, byte[] data, int offset, int length, DeliveryMethod method, byte channel)
        {
            if (!_connected || _peer == null) return false;
            var copy = new byte[length];
            Array.Copy(data, offset, copy, 0, length);
            _peer.LastReceivedChannel = channel;
            _peer.OnDataReceived?.Invoke(LocalPeerId, copy, 0, copy.Length);
            return true;
        }

        public void Dispose() => Disconnect();
    }
}
