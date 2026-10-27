using System;

namespace SilksongCoop.Core.Protocol.Packets
{
    /// <summary>
    /// Network packet representing a transform synchronization event.
    /// </summary>
    public struct TransformSyncPacket
    {
        public const ushort FlagGrounded = 1 << 0;
        public const ushort FlagDashing = 1 << 1;
        public const ushort FlagWallSliding = 1 << 2;
        public const ushort FlagJumping = 1 << 3;
        public const ushort FlagFalling = 1 << 4;
        public const ushort FlagAttacking = 1 << 5;
        public const ushort FlagInvulnerable = 1 << 6;
        public const ushort FlagSwimming = 1 << 7;

        public PacketHeader Header;
        public float PositionX;
        public float PositionY;
        public float VelocityX;
        public float VelocityY;
        public byte FacingDirection;
        public ushort StateFlags;
        public int AnimationClipHash;
        public ushort NormalizedTime;
        public int SceneNameHash;

        public bool FacingRight => FacingDirection != 0;
        public bool IsGrounded => (StateFlags & FlagGrounded) != 0;
        public bool IsDashing => (StateFlags & FlagDashing) != 0;

        /// <summary>
        /// Serializes this packet to the specified writer.
        /// </summary>
        /// <param name="writer">The packet writer to serialize to.</param>
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

        /// <summary>
        /// Deserializes a transform sync packet from the specified reader.
        /// </summary>
        /// <param name="reader">The packet reader to deserialize from.</param>
        /// <returns>The deserialized transform sync packet.</returns>
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
