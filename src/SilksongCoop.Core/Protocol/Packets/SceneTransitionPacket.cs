using System;

namespace SilksongCoop.Core.Protocol.Packets
{
    public struct SceneTransitionPacket
    {
        public PacketHeader Header;
        public string TargetScene;
        public string EntryGate;
        public int SceneNameHash;
        public byte TransitionPhase; // 0 = Started, 1 = Loaded, 2 = Ready

        public void Serialize(ref PacketWriter writer)
        {
            writer.WriteHeader(Header);
            writer.WriteString(TargetScene ?? string.Empty);
            writer.WriteString(EntryGate ?? string.Empty);
            writer.WriteInt32(SceneNameHash);
            writer.WriteByte(TransitionPhase);
        }

        public static SceneTransitionPacket Deserialize(ref PacketReader reader)
        {
            return new SceneTransitionPacket
            {
                Header = reader.ReadHeader(),
                TargetScene = reader.ReadString() ?? string.Empty,
                EntryGate = reader.ReadString() ?? string.Empty,
                SceneNameHash = reader.ReadInt32(),
                TransitionPhase = reader.ReadByte()
            };
        }
    }
}
