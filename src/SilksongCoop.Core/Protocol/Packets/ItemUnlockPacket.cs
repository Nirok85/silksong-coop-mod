using System;

namespace SilksongCoop.Core.Protocol.Packets
{
    public enum ItemCategory : byte
    {
        Crest = 0,
        Tool = 1,
        Ability = 2,
        KeyItem = 3
    }

    public struct ItemUnlockPacket
    {
        public PacketHeader Header;
        public string ItemId;
        public ItemCategory Category;
        public Guid TransactionId;
        public uint Timestamp;

        public void Serialize(ref PacketWriter writer)
        {
            writer.WriteHeader(Header);
            writer.WriteString(ItemId ?? string.Empty);
            writer.WriteByte((byte)Category);
            writer.WriteGuid(TransactionId);
            writer.WriteUInt32(Timestamp != 0 ? Timestamp : Header.Timestamp);
        }

        public static ItemUnlockPacket Deserialize(ref PacketReader reader)
        {
            return new ItemUnlockPacket
            {
                Header = reader.ReadHeader(),
                ItemId = reader.ReadString() ?? string.Empty,
                Category = (ItemCategory)reader.ReadByte(),
                TransactionId = reader.ReadGuid(),
                Timestamp = reader.ReadUInt32()
            };
        }
    }
}
