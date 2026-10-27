using System;

namespace SilksongCoop.Core.Protocol.Packets
{
    public struct BenchCheckpointSyncPacket
    {
        public PacketHeader Header;
        public string SceneName;
        public string RespawnMarker;
        public int FacingDirection;
        public bool AtBench;

        public void Serialize(ref PacketWriter writer)
        {
            writer.WriteHeader(Header);
            writer.WriteString(SceneName ?? string.Empty);
            writer.WriteString(RespawnMarker ?? string.Empty);
            writer.WriteInt32(FacingDirection);
            writer.WriteByte((byte)(AtBench ? 1 : 0));
        }

        public static BenchCheckpointSyncPacket Deserialize(ref PacketReader reader)
        {
            return new BenchCheckpointSyncPacket
            {
                Header = reader.ReadHeader(),
                SceneName = reader.ReadString() ?? string.Empty,
                RespawnMarker = reader.ReadString() ?? string.Empty,
                FacingDirection = reader.ReadInt32(),
                AtBench = reader.ReadByte() != 0
            };
        }
    }
}
