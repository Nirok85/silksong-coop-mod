using System;

namespace SilksongCoop.Core.Protocol.Packets
{
    public struct VitalsSyncPacket
    {
        public PacketHeader Header;
        public byte CurrentHealth;
        public byte CurrentSilk;
        public byte MaxHealth;
        public byte MaxSilkSpools;
        public byte NeedleUpgradeLevel;

        public void Serialize(ref PacketWriter writer)
        {
            writer.WriteHeader(Header);
            writer.WriteByte(CurrentHealth);
            writer.WriteByte(CurrentSilk);
            writer.WriteByte(MaxHealth);
            writer.WriteByte(MaxSilkSpools);
            writer.WriteByte(NeedleUpgradeLevel);
        }

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
