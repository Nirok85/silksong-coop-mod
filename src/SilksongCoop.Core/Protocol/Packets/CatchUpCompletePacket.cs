using System;

namespace SilksongCoop.Core.Protocol.Packets
{
    /// <summary>
    /// Sent by a peer right after dispatching its initial catch-up burst to one peer
    /// (wallet seed, vitals, claimed pickups, inventory, quest replay, bench checkpoint).
    /// Readiness (<see cref="PeerReadyPacket"/>) only said the receiver can apply state;
    /// this marker says the sender actually handed that state to the transport. Additive
    /// within protocol v11: a peer that does not know the type rejects the datagram and
    /// continues exactly as before.
    /// </summary>
    public struct CatchUpCompletePacket : IPacket
    {
        public PacketHeader Header;

        /// <summary>
        /// The PeerReady save-instance token this catch-up belongs to. A marker for an
        /// older (already replaced) session instance is discarded by the receiver.
        /// </summary>
        public Guid SaveInstance;

        public void Serialize(ref PacketWriter writer)
        {
            writer.WriteHeader(Header);
            writer.WriteGuid(SaveInstance);
        }
    }
}
