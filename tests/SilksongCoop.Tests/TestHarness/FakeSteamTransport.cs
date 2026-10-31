using System;
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
        private FakeSteamTransport? _peer;
        private bool _connected;

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

        public void StartHost(int port = 7777)
        {
            _connected = true;
            if (_peer == null) return;

            _peer._connected = true;
            OnPeerConnected?.Invoke(_peer.LocalPeerId);
            _peer.OnPeerConnected?.Invoke(LocalPeerId);
        }

        public void Connect(string addressOrCode)
        {
            // Simulated join: host must already be listening.
            if (_peer == null) return;
            _connected = true;
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

        public void PollEvents() { }

        public byte LastReceivedChannel { get; private set; }
        public int SimulatedRttMs { get; set; } = 30;

        public void Send(NetworkPeerId target, byte[] data, int offset, int length, DeliveryMethod method, byte channel)
        {
            if (!_connected || _peer == null) return;
            var copy = new byte[length];
            Array.Copy(data, offset, copy, 0, length);
            _peer.LastReceivedChannel = channel;
            _peer.OnDataReceived?.Invoke(LocalPeerId, copy, 0, copy.Length);
        }

        public void Dispose() => Disconnect();
    }
}
