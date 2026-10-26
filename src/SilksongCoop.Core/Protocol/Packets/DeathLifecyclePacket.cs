using System;

namespace SilksongCoop.Core.Protocol.Packets
{
    public enum DeathEventType : byte
    {
        SoloDeathOccurred = 0,
        SpectatorCountdownSync = 1,
        PartnerReviveTrigger = 2,
        TeamWipeOccurred = 3,
        BenchCheckpointUpdate = 4
    }

    public struct DeathLifecyclePacket
    {
        public PacketHeader Header;
        public DeathEventType EventType;
        public byte FallenPlayerIndex;
        public float CoordinateX;
        public float CoordinateY;
        public float RemainingSeconds;
        public ushort BenchSceneHash;
        public uint Epoch;

        public void Serialize(ref PacketWriter writer)
        {
            writer.WriteHeader(Header);
            writer.WriteByte((byte)EventType);
            writer.WriteByte(FallenPlayerIndex);
            writer.WriteFloat(CoordinateX);
            writer.WriteFloat(CoordinateY);
            writer.WriteFloat(RemainingSeconds);
            writer.WriteUInt16(BenchSceneHash);
            writer.WriteUInt32(Epoch);
        }

        public static DeathLifecyclePacket Deserialize(ref PacketReader reader)
        {
            return new DeathLifecyclePacket
            {
                Header = reader.ReadHeader(),
                EventType = (DeathEventType)reader.ReadByte(),
                FallenPlayerIndex = reader.ReadByte(),
                CoordinateX = reader.ReadFloat(),
                CoordinateY = reader.ReadFloat(),
                RemainingSeconds = reader.ReadFloat(),
                BenchSceneHash = reader.ReadUInt16(),
                Epoch = reader.ReadUInt32()
            };
        }
    }
}
