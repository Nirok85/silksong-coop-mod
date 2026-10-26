using System;

namespace SilksongCoop.Core.Protocol.Packets
{
    public enum CurrencyType : byte
    {
        Rosaries = 0,
        ShellShards = 1
    }

    public struct CurrencySyncPacket
    {
        public PacketHeader Header;
        public CurrencyType Currency;
        public int TotalWalletAmount;
        public short DeltaAmount;
        public uint PickupUniqueId;
        public Guid TransactionId;

        public void Serialize(ref PacketWriter writer)
        {
            writer.WriteHeader(Header);
            writer.WriteByte((byte)Currency);
            writer.WriteInt32(TotalWalletAmount);
            writer.WriteInt16(DeltaAmount);
            writer.WriteUInt32(PickupUniqueId);
            writer.WriteGuid(TransactionId);
        }

        public static CurrencySyncPacket Deserialize(ref PacketReader reader)
        {
            return new CurrencySyncPacket
            {
                Header = reader.ReadHeader(),
                Currency = (CurrencyType)reader.ReadByte(),
                TotalWalletAmount = reader.ReadInt32(),
                DeltaAmount = reader.ReadInt16(),
                PickupUniqueId = reader.ReadUInt32(),
                TransactionId = reader.ReadGuid()
            };
        }
    }
}
