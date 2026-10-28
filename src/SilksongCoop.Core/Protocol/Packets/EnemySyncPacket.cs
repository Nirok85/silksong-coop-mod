using System;

namespace SilksongCoop.Core.Protocol.Packets
{
    /// <summary>
    /// Event carried by <see cref="EnemySyncPacket"/>.
    /// </summary>
    public enum EnemySyncEventType : byte
    {
        /// <summary>The sender damaged the enemy; <see cref="EnemySyncPacket.Hp"/> is its HP after the hit.</summary>
        Hit = 0,

        /// <summary>The enemy died on the sender's machine.</summary>
        Death = 1
    }

    /// <summary>
    /// Replicates a single hit (or death) on a <c>HealthManager</c>. Enemies are identified by a
    /// stable 64-bit hash of their scene hierarchy path, so both clients can find the same
    /// instance without exchanging Unity instance IDs.
    /// </summary>
    public struct EnemySyncPacket
    {
        public PacketHeader Header;

        /// <summary>Stable hash of the enemy's scene + hierarchy path.</summary>
        public ulong EnemyKey;

        /// <summary>Enemy HP after the hit (0 for Death).</summary>
        public int Hp;

        /// <summary>Damage dealt, used for hit effects/statistics.</summary>
        public short Damage;

        public float PositionX;
        public float PositionY;

        public EnemySyncEventType EventType;
        public byte AttackType;

        /// <summary>
        /// Serializes this packet to the specified writer.
        /// </summary>
        public void Serialize(ref PacketWriter writer)
        {
            writer.WriteHeader(Header);
            writer.WriteUInt64(EnemyKey);
            writer.WriteInt32(Hp);
            writer.WriteInt16(Damage);
            writer.WriteFloat(PositionX);
            writer.WriteFloat(PositionY);
            writer.WriteByte((byte)EventType);
            writer.WriteByte(AttackType);
        }

        /// <summary>
        /// Deserializes an enemy sync packet from the specified reader.
        /// </summary>
        public static EnemySyncPacket Deserialize(ref PacketReader reader)
        {
            return new EnemySyncPacket
            {
                Header = reader.ReadHeader(),
                EnemyKey = reader.ReadUInt64(),
                Hp = reader.ReadInt32(),
                Damage = reader.ReadInt16(),
                PositionX = reader.ReadFloat(),
                PositionY = reader.ReadFloat(),
                EventType = (EnemySyncEventType)reader.ReadByte(),
                AttackType = reader.ReadByte()
            };
        }
    }
}
