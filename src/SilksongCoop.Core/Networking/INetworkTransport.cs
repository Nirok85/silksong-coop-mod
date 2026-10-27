using System;

namespace SilksongCoop.Core.Networking
{
    /// <summary>
    /// Abstraction for network transport layer supporting host/client communication.
    /// </summary>
    public interface INetworkTransport : IDisposable
    {
        event Action<NetworkPeerId> OnPeerConnected;
        event Action<NetworkPeerId, DisconnectReason> OnPeerDisconnected;
        event Action<NetworkPeerId, byte[], int, int> OnDataReceived;

        bool IsHost { get; }
        bool IsConnected { get; }
        NetworkPeerId LocalPeerId { get; }

        void StartHost(int port = 7777);
        void Connect(string addressOrCode);
        void Disconnect();
        void PollEvents();
        void Send(NetworkPeerId target, byte[] data, int offset, int length, DeliveryMethod method, byte channel);
    }
}
