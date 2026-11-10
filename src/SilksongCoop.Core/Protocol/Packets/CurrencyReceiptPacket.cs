using System;

namespace SilksongCoop.Core.Protocol.Packets
{
    public struct CurrencyReceiptPacket : IPacket
    {
        public PacketHeader Header;
        public Guid TransactionId;

        public void Serialize(ref PacketWriter writer)
        {
            writer.WriteHeader(Header);
            writer.WriteGuid(TransactionId);
        }
    }
}
