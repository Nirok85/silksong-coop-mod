using System;

namespace SilksongCoop.Core.Protocol.Packets
{
    /// <summary>
    /// One enemy state inside an <see cref="EnemySnapshotPacket"/>.
    /// </summary>
    public struct EnemySnapshotEntry
    {
        public ulong EnemyKey;
        public int Hp;
        public byte Flags;
        public float PositionX;
        public float PositionY;

        public const byte FlagDead = 0x01;

        public bool IsDead
        {
            get => (Flags & FlagDead) != 0;
            set
            {
                if (value) Flags |= FlagDead;
                else Flags &= unchecked((byte)~FlagDead);
            }
        }
    }

    /// <summary>
    /// Periodic authoritative enemy HP snapshot sent by the host. Corrects drift caused by
    /// hits that were rejected locally (invincibility, phase transitions) and by enemies
    /// spawned dynamically on only one machine.
    /// </summary>
    public struct EnemySnapshotPacket
    {
        /// <summary>Maximum entries a single packet may carry (keeps it inside one UDP datagram).</summary>
        public const int MaxEntries = 32;

        public PacketHeader Header;
        public EnemySnapshotEntry[] Entries;
        public int Count;

        /// <summary>
        /// Serializes this packet to the specified writer.
        /// </summary>
        public void Serialize(ref PacketWriter writer)
        {
            writer.WriteHeader(Header);
            int count = Math.Min(Math.Max(Count, 0), byte.MaxValue);
            writer.WriteByte((byte)count);
            for (int i = 0; i < count; i++)
            {
                var entry = Entries[i];
                writer.WriteUInt64(entry.EnemyKey);
                writer.WriteInt32(entry.Hp);
                writer.WriteByte(entry.Flags);
                writer.WriteFloat(entry.PositionX);
                writer.WriteFloat(entry.PositionY);
            }
        }

        /// <summary>
        /// Deserializes an enemy snapshot packet from the specified reader.
        /// </summary>
        public static EnemySnapshotPacket Deserialize(ref PacketReader reader)
        {
            var packet = new EnemySnapshotPacket
            {
                Header = reader.ReadHeader()
            };

            int count = reader.ReadByte();
            if (count > MaxEntries)
            {
                throw new InvalidOperationException($"Enemy snapshot carries too many entries: {count}");
            }

            packet.Count = count;
            packet.Entries = new EnemySnapshotEntry[count];
            for (int i = 0; i < count; i++)
            {
                packet.Entries[i] = new EnemySnapshotEntry
                {
                    EnemyKey = reader.ReadUInt64(),
                    Hp = reader.ReadInt32(),
                    Flags = reader.ReadByte(),
                    PositionX = reader.ReadFloat(),
                    PositionY = reader.ReadFloat()
                };
            }

            return packet;
        }
    }
}
