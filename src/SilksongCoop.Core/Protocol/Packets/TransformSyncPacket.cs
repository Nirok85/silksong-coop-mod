using System;

namespace SilksongCoop.Core.Protocol.Packets
{
    public struct TransformSyncPacket
    {
        public const ushort FlagGrounded     = 0x0001;
        public const ushort FlagDashing      = 0x0002;
        public const ushort FlagWallSliding  = 0x0004;
        public const ushort FlagJumping      = 0x0008;
        public const ushort FlagFalling      = 0x0010;
        public const ushort FlagAttacking    = 0x0020;
        public const ushort FlagHealing      = 0x0040;
        public const ushort FlagInvulnerable = 0x0080;
        public const ushort FlagSwimming     = 0x0100;
        public const ushort FlagCrouching    = 0x0200;

        public PacketHeader Header;
        public float PositionX;
        public float PositionY;
        public float VelocityX;
        public float VelocityY;
        public byte FacingDirection; // 0 = Left, 1 = Right
        public ushort StateFlags;
        public int AnimationClipHash;
        public ushort NormalizedTime;
        public int SceneNameHash;

        public bool FacingRight => FacingDirection != 0;
        public bool IsGrounded => (StateFlags & FlagGrounded) != 0;
        public bool IsDashing => (StateFlags & FlagDashing) != 0;

        public void Serialize(ref PacketWriter writer)
        {
            writer.WriteHeader(Header);
            writer.WriteFloat(PositionX);
            writer.WriteFloat(PositionY);
            writer.WriteFloat(VelocityX);
            writer.WriteFloat(VelocityY);
            writer.WriteByte(FacingDirection);
            writer.WriteUInt16(StateFlags);
            writer.WriteInt32(AnimationClipHash);
            writer.WriteUInt16(NormalizedTime);
            writer.WriteInt32(SceneNameHash);
        }

        public static TransformSyncPacket Deserialize(ref PacketReader reader)
        {
            var packet = new TransformSyncPacket
            {
                Header = reader.ReadHeader(),
                PositionX = reader.ReadFloat(),
                PositionY = reader.ReadFloat(),
                VelocityX = reader.ReadFloat(),
                VelocityY = reader.ReadFloat(),
                FacingDirection = reader.ReadByte(),
                StateFlags = reader.ReadUInt16(),
                AnimationClipHash = reader.ReadInt32(),
                NormalizedTime = reader.ReadUInt16(),
                SceneNameHash = reader.ReadInt32()
            };
            return packet;
        }
    }
}
