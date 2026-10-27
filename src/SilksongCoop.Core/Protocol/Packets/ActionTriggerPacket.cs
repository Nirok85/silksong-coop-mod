using System;

namespace SilksongCoop.Core.Protocol.Packets
{
    /// <summary>
    /// Identifies the type of player action in an action trigger packet.
    /// </summary>
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

    /// <summary>
    /// Network packet representing a player action trigger.
    /// </summary>
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

        /// <summary>
        /// Serializes this packet to the specified writer.
        /// </summary>
        /// <param name="writer">The packet writer to serialize to.</param>
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

        /// <summary>
        /// Deserializes an action trigger packet from the specified reader.
        /// </summary>
        /// <param name="reader">The packet reader to deserialize from.</param>
        /// <returns>The deserialized action trigger packet.</returns>
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
