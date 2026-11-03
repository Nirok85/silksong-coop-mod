using System;
using SilksongCoop.Core.Progression;

namespace SilksongCoop.Core.Protocol.Packets
{
    /// <summary>
    /// One changed quest flag inside a <see cref="QuestStatePacket"/>.
    /// </summary>
    public struct QuestFlagEntry
    {
        public QuestFlagKind Kind;
        public string Key;
        public bool BoolValue;
        public int IntValue;
    }

    /// <summary>
    /// Replicates the PlayerData bool/int flags that changed during an NPC dialogue.
    /// Only names accepted by <see cref="QuestFlagFilter.IsQuestRelated"/> are applied, so
    /// the packet cannot be used to overwrite arbitrary progression. Older peers drop this
    /// packet type and remain usable; both sides must run 3.2.0 for quest sync.
    /// </summary>
    public struct QuestStatePacket
    {
        public const int MaxEntries = 64;

        public PacketHeader Header;
        public int Count;
        public QuestFlagEntry[] Entries;

        public void Serialize(ref PacketWriter writer)
        {
            writer.WriteHeader(Header);
            int count = Math.Min(Math.Max(Count, 0), MaxEntries);
            writer.WriteByte((byte)count);

            for (int i = 0; i < count; i++)
            {
                var entry = Entries[i];
                writer.WriteByte((byte)entry.Kind);
                writer.WriteString(entry.Key ?? string.Empty);
                if (entry.Kind == QuestFlagKind.Int)
                {
                    writer.WriteInt32(entry.IntValue);
                }
                else
                {
                    writer.WriteByte(entry.BoolValue ? (byte)1 : (byte)0);
                }
            }
        }

        public static QuestStatePacket Deserialize(ref PacketReader reader)
        {
            var header = reader.ReadHeader();
            int count = Math.Min((int)reader.ReadByte(), MaxEntries);
            var entries = new QuestFlagEntry[count];

            for (int i = 0; i < count; i++)
            {
                var kind = (QuestFlagKind)reader.ReadByte();
                string key = reader.ReadString() ?? string.Empty;
                entries[i] = kind == QuestFlagKind.Int
                    ? new QuestFlagEntry { Kind = kind, Key = key, IntValue = reader.ReadInt32() }
                    : new QuestFlagEntry { Kind = kind, Key = key, BoolValue = reader.ReadByte() != 0 };
            }

            return new QuestStatePacket
            {
                Header = header,
                Count = count,
                Entries = entries
            };
        }
    }
}
