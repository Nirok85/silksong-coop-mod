using System;

namespace SilksongCoop.Core.Protocol.Packets
{
    /// <summary>
    /// Categorizes the type of item in an item unlock packet.
    /// </summary>
    public enum ItemCategory : byte
    {
        Crest = 0,
        Tool = 1,
        Ability = 2,
        KeyItem = 3
    }

    /// <summary>
    /// Network packet representing an item unlock event.
    /// </summary>
    public struct ItemUnlockPacket
    {
        public PacketHeader Header;
        public string ItemId;
        public ItemCategory Category;
        public Guid TransactionId;
        public uint Timestamp;

        /// <summary>
        /// Serializes this packet to the specified writer.
        /// </summary>
        /// <param name="writer">The packet writer to serialize to.</param>
        public void Serialize(ref PacketWriter writer)
        {
            writer.WriteHeader(Header);
            writer.WriteString(ItemId ?? string.Empty);
            writer.WriteByte((byte)Category);
            writer.WriteGuid(TransactionId);
            writer.WriteUInt32(Timestamp != 0 ? Timestamp : Header.Timestamp);
        }

        /// <summary>
        /// Deserializes an item unlock packet from the specified reader.
        /// </summary>
        /// <param name="reader">The packet reader to deserialize from.</param>
        /// <returns>The deserialized item unlock packet.</returns>
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
