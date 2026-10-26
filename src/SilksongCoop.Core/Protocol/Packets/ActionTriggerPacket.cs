using System;

namespace SilksongCoop.Core.Protocol.Packets
{
    public enum ActionType : byte
    {
        SlashNormal = 0,
        SlashUp = 1,
        SlashDown = 2,
        HarpoonDash = 3,
        NeedleThrow = 4,
        ThreadBindStart = 5,
        ThreadBindComplete = 6,
        ToolDeploy = 7,
        Jump = 8,
        AirDash = 9,
        WallJump = 10
    }

    public struct ActionTriggerPacket
    {
        public PacketHeader Header;
        public ActionType Action;
        public uint ActionSequenceId;
        public float OriginX;
        public float OriginY;
        public float DirectionX;
        public float DirectionY;
        public int Parameter;

        public void Serialize(ref PacketWriter writer)
        {
            writer.WriteHeader(Header);
            writer.WriteByte((byte)Action);
            writer.WriteUInt32(ActionSequenceId);
            writer.WriteFloat(OriginX);
            writer.WriteFloat(OriginY);
            writer.WriteFloat(DirectionX);
            writer.WriteFloat(DirectionY);
            writer.WriteInt32(Parameter);
        }

        public static ActionTriggerPacket Deserialize(ref PacketReader reader)
        {
            return new ActionTriggerPacket
            {
                Header = reader.ReadHeader(),
                Action = (ActionType)reader.ReadByte(),
                ActionSequenceId = reader.ReadUInt32(),
                OriginX = reader.ReadFloat(),
                OriginY = reader.ReadFloat(),
                DirectionX = reader.ReadFloat(),
                DirectionY = reader.ReadFloat(),
                Parameter = reader.ReadInt32()
            };
        }
    }
}
