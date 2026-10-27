using System;

namespace SilksongCoop.Core.Protocol.Packets
{
    /// <summary>
    /// Network packet representing vitals synchronization (health, silk, upgrades).
    /// </summary>
    public struct VitalsSyncPacket
    {
        public PacketHeader Header;
        public byte CurrentHealth;
        public byte CurrentSilk;
        public byte MaxHealth;
        public byte MaxSilkSpools;
        public byte NeedleUpgradeLevel;

        /// <summary>
        /// Serializes this packet to the specified writer.
        /// </summary>
        /// <param name="writer">The packet writer to serialize to.</param>
        public void Serialize(ref PacketWriter writer)
        {
            writer.WriteHeader(Header);
            writer.WriteByte(CurrentHealth);
            writer.WriteByte(CurrentSilk);
            writer.WriteByte(MaxHealth);
            writer.WriteByte(MaxSilkSpools);
            writer.WriteByte(NeedleUpgradeLevel);
        }

        /// <summary>
        /// Deserializes a vitals sync packet from the specified reader.
        /// </summary>
        /// <param name="reader">The packet reader to deserialize from.</param>
        /// <returns>The deserialized vitals sync packet.</returns>
        public static VitalsSyncPacket Deserialize(ref PacketReader reader)
        {
            return new VitalsSyncPacket
            {
                Header = reader.ReadHeader(),
                CurrentHealth = reader.ReadByte(),
                CurrentSilk = reader.ReadByte(),
                MaxHealth = reader.ReadByte(),
                MaxSilkSpools = reader.ReadByte(),
                NeedleUpgradeLevel = reader.ReadByte()
            };
        }
    }
}
