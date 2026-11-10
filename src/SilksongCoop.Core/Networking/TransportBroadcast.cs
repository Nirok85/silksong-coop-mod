namespace SilksongCoop.Core.Networking
{
    /// <summary>
    /// Optional transport capability for sending one payload to every connected peer.
    /// Single-peer transports can keep implementing only <see cref="INetworkTransport"/>;
    /// callers use <see cref="TransportBroadcast.SendToAll"/> to fall back to the
    /// <see cref="INetworkTransport.ConnectedPeers"/> loop.
    /// </summary>
    public interface IMultiPeerTransport : INetworkTransport
    {
        /// <summary>
        /// Maximum number of remote peers this transport can hold. Legacy single-peer
        /// transports report 1; a host-relay/full-mesh transport reports its real limit.
        /// </summary>
        int MaxRemotePeers { get; }

        /// <summary>True when the host forwards peer-to-peer traffic (UDP relay).</summary>
        bool SupportsHostRelay { get; }

        bool Broadcast(byte[] data, int offset, int length, DeliveryMethod method, byte channel);
    }

    /// <summary>
    /// Transport-agnostic broadcast helper used by sync services. Multi-peer transports
    /// get a single optimized call; legacy single-peer transports are handled by iterating
    /// the connected peer list.
    /// </summary>
    public static class TransportBroadcast
    {
        public static bool SendToAll(
            this INetworkTransport transport,
            byte[] data,
            int offset,
            int length,
            DeliveryMethod method,
            byte channel)
        {
            if (transport == null || data == null || length <= 0) return false;

            if (transport is IMultiPeerTransport multiPeer)
            {
                return multiPeer.Broadcast(data, offset, length, method, channel);
            }

            var peers = transport.ConnectedPeers;
            bool accepted = peers.Count > 0;
            for (int i = 0; i < peers.Count; i++)
            {
                accepted &= transport.Send(peers[i], data, offset, length, method, channel);
            }
            return accepted;
        }
    }
}
