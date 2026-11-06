using System;
using System.Text;
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

        /// <summary>
        /// Conservative upper bound for one serialized packet. The UDP transport receives
        /// datagrams into a 2048-byte buffer that also holds an 8-byte frame header and an
        /// 8-byte MAC; 1500 bytes leaves headroom for future framing. A packet larger than
        /// the receive buffer is rejected by the OS and retransmitted forever on the
        /// reliable channel, so the sender must split by encoded size, not only by entry
        /// count.
        /// </summary>
        public const int MaxEncodedBytes = 1500;

        /// <summary>Header plus the entry-count byte.</summary>
        public const int EncodedOverhead = PacketHeader.HeaderSize + 1;

        /// <summary>
        /// Exact number of bytes one entry occupies on the wire. Used to split a large
        /// flag diff into datagrams that fit the transport buffer.
        /// </summary>
        public static int GetEntryEncodedSize(string? key, QuestFlagKind kind)
        {
            int keyBytes = string.IsNullOrEmpty(key) ? 0 : Encoding.UTF8.GetByteCount(key);
            // kind (1) + string length prefix (2) + UTF-8 key + int (4) or bool (1)
            return 1 + 2 + keyBytes + (kind == QuestFlagKind.Int ? 4 : 1);
        }

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
