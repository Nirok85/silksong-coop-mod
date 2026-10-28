using System;

namespace SilksongCoop.Core.Protocol.Packets
{
    /// <summary>
    /// Identifies the type of currency in a currency sync packet.
    /// </summary>
    public enum CurrencyType : byte
    {
        Rosaries = 0,
        ShellShards = 1
    }

    /// <summary>
    /// Network packet representing a currency synchronization event.
    /// </summary>
    public struct CurrencySyncPacket
    {
        public PacketHeader Header;
        public CurrencyType Currency;
        public int TotalWalletAmount;
        public short DeltaAmount;
        public uint PickupUniqueId;
        public Guid TransactionId;

        /// <summary>
        /// Stable key of the physical pickup that produced this delta (0 for non-pickup
        /// changes). Both clients derive the same key from the drop's scene/type/position, so
        /// a drop that exists in both simulations is credited only once.
        /// </summary>
        public ulong PickupKey;

        /// <summary>World position of the collected drop (for proximity-based deduplication).</summary>
        public float PickupX;
        public float PickupY;

        /// <summary>
        /// Serializes this packet to the specified writer.
        /// </summary>
        /// <param name="writer">The packet writer to serialize to.</param>
        public void Serialize(ref PacketWriter writer)
        {
            writer.WriteHeader(Header);
            writer.WriteByte((byte)Currency);
            writer.WriteInt32(TotalWalletAmount);
            writer.WriteInt16(DeltaAmount);
            writer.WriteUInt32(PickupUniqueId);
            writer.WriteGuid(TransactionId);
            writer.WriteUInt64(PickupKey);
            writer.WriteFloat(PickupX);
            writer.WriteFloat(PickupY);
        }

        /// <summary>
        /// Deserializes a currency sync packet from the specified reader.
        /// </summary>
        /// <param name="reader">The packet reader to deserialize from.</param>
        /// <returns>The deserialized currency sync packet.</returns>
        public static CurrencySyncPacket Deserialize(ref PacketReader reader)
        {
            return new CurrencySyncPacket
            {
                Header = reader.ReadHeader(),
                Currency = (CurrencyType)reader.ReadByte(),
                TotalWalletAmount = reader.ReadInt32(),
                DeltaAmount = reader.ReadInt16(),
                PickupUniqueId = reader.ReadUInt32(),
                TransactionId = reader.ReadGuid(),
                PickupKey = reader.ReadUInt64(),
                PickupX = reader.ReadFloat(),
                PickupY = reader.ReadFloat()
            };
        }
    }
}
