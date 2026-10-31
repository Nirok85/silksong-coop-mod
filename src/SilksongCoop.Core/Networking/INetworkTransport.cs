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

        /// <summary>
        /// All currently connected peers. Single-peer transports return a list with one
        /// entry (or empty when disconnected); N-player transports may return more.
        /// </summary>
        System.Collections.Generic.IReadOnlyList<NetworkPeerId> ConnectedPeers { get; }

        /// <summary>
        /// Current round-trip time in milliseconds, or 0 when the transport cannot
        /// measure it. Fed into the diagnostics overlay and adaptive interpolation.
        /// </summary>
        int RoundTripTimeMs { get; }

        /// <summary>
        /// Estimated packet loss percentage (0-100) for the current window.
        /// </summary>
        int PacketLossPercent { get; }

        /// <summary>
        /// Number of reliable-ordered packets waiting for an acknowledgement. A sender that
        /// needs to stream a large burst (for example the world-state catch-up replay) must
        /// throttle itself below the transport's backlog cap, otherwise the transport has to
        /// drop new packets to preserve stream continuity.
        /// </summary>
        int ReliableBacklogCount { get; }

        void StartHost(int port = 7777);
        void Connect(string addressOrCode);
        void Disconnect();
        void PollEvents();
        void Send(NetworkPeerId target, byte[] data, int offset, int length, DeliveryMethod method, byte channel);
    }
}
