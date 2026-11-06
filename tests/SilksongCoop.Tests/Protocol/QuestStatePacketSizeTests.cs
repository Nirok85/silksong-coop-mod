using System;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Progression;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using Xunit;

namespace SilksongCoop.Tests.Protocol
{
    /// <summary>
    /// The quest diff used to be split only by <see cref="QuestStatePacket.MaxEntries"/>.
    /// Sixty-four long PlayerData names exceed the UDP receive buffer (2048 bytes including
    /// the transport envelope), and an oversized reliable datagram is never delivered.
    /// These tests pin the exact encoded size and the size-based split.
    /// </summary>
    public class QuestStatePacketSizeTests
    {
        private const int UdpReceiveBudget = 2048;
        private const int TransportEnvelopeBytes = 16; // 8-byte frame header + 8-byte MAC

        [Theory]
        [InlineData("QuestFlag", QuestFlagKind.Bool)]
        [InlineData("QuestFlag", QuestFlagKind.Int)]
        [InlineData("КвестовыйФлаг", QuestFlagKind.Bool)]
        [InlineData("КвестовыйФлаг", QuestFlagKind.Int)]
        public void EntrySize_MatchesSerializedBytes(string key, QuestFlagKind kind)
        {
            int measured = MeasureEntry(key, kind);
            Assert.Equal(QuestStatePacket.GetEntryEncodedSize(key, kind), measured);
        }

        [Fact]
        public void SixtyFourLongNames_ExceedTheUdpReceiveBuffer()
        {
            string key = new string('Q', 40);
            var packet = BuildPacket(count: QuestStatePacket.MaxEntries, key, QuestFlagKind.Bool);

            // Documents the hazard that motivated the size-based split: the old MaxEntries
            // limit alone did not keep the datagram inside the receive buffer.
            Assert.True(SerializedSize(packet) > UdpReceiveBudget);
        }

        [Fact]
        public void SizeBasedSplit_KeepsEveryDatagramInsideTheReceiveBudget()
        {
            string key = new string('Q', 40);
            int entrySize = QuestStatePacket.GetEntryEncodedSize(key, QuestFlagKind.Int);
            int entriesPerBatch = (QuestStatePacket.MaxEncodedBytes - QuestStatePacket.EncodedOverhead)
                                  / entrySize;

            Assert.True(entriesPerBatch > 0);
            Assert.True(entriesPerBatch < QuestStatePacket.MaxEntries);

            for (int count = 1; count <= entriesPerBatch; count++)
            {
                var packet = BuildPacket(count, key, QuestFlagKind.Int);
                int size = SerializedSize(packet);

                Assert.True(size <= QuestStatePacket.MaxEncodedBytes);
                Assert.True(size + TransportEnvelopeBytes <= UdpReceiveBudget);
            }

            // One entry beyond the batch must be detected by the sender before serializing
            // the oversized packet.
            Assert.True(
                QuestStatePacket.EncodedOverhead + entrySize * (entriesPerBatch + 1) >
                QuestStatePacket.MaxEncodedBytes);
        }

        [Fact]
        public void MixedDiff_RoundTripsWithinTheLimit()
        {
            var entries = new[]
            {
                new QuestFlagEntry { Kind = QuestFlagKind.Bool, Key = "HasSeenQuestBoard", BoolValue = true },
                new QuestFlagEntry { Kind = QuestFlagKind.Int, Key = "QuestBoardProgress", IntValue = 3 },
                new QuestFlagEntry { Kind = QuestFlagKind.Bool, Key = "КвестОткрыт", BoolValue = false }
            };

            var packet = new QuestStatePacket
            {
                Header = new PacketHeader(PacketType.QuestState, NetworkChannels.World, 7, 1234),
                Count = entries.Length,
                Entries = entries
            };

            byte[] buffer = new byte[QuestStatePacket.MaxEncodedBytes];
            var writer = new PacketWriter(buffer);
            packet.Serialize(ref writer);
            Assert.True(writer.Position <= QuestStatePacket.MaxEncodedBytes);

            var reader = new PacketReader(buffer.AsSpan(0, writer.Position));
            var roundTripped = QuestStatePacket.Deserialize(ref reader);

            Assert.Equal(entries.Length, roundTripped.Count);
            Assert.Equal("HasSeenQuestBoard", roundTripped.Entries[0].Key);
            Assert.True(roundTripped.Entries[0].BoolValue);
            Assert.Equal(3, roundTripped.Entries[1].IntValue);
            Assert.Equal("КвестОткрыт", roundTripped.Entries[2].Key);
            Assert.False(roundTripped.Entries[2].BoolValue);
        }

        private static int MeasureEntry(string key, QuestFlagKind kind)
        {
            int empty = SerializedSize(new QuestStatePacket
            {
                Header = new PacketHeader(PacketType.QuestState, NetworkChannels.World, 1, 1),
                Count = 0,
                Entries = Array.Empty<QuestFlagEntry>()
            });

            int single = SerializedSize(new QuestStatePacket
            {
                Header = new PacketHeader(PacketType.QuestState, NetworkChannels.World, 1, 1),
                Count = 1,
                Entries = new[]
                {
                    kind == QuestFlagKind.Int
                        ? new QuestFlagEntry { Kind = kind, Key = key, IntValue = 1 }
                        : new QuestFlagEntry { Kind = kind, Key = key, BoolValue = true }
                }
            });

            return single - empty;
        }

        private static QuestStatePacket BuildPacket(int count, string key, QuestFlagKind kind)
        {
            var entries = new QuestFlagEntry[count];
            for (int i = 0; i < count; i++)
            {
                entries[i] = kind == QuestFlagKind.Int
                    ? new QuestFlagEntry { Kind = kind, Key = key, IntValue = i }
                    : new QuestFlagEntry { Kind = kind, Key = key, BoolValue = i % 2 == 0 };
            }

            return new QuestStatePacket
            {
                Header = new PacketHeader(PacketType.QuestState, NetworkChannels.World, 1, 1),
                Count = count,
                Entries = entries
            };
        }

        private static int SerializedSize(QuestStatePacket packet)
        {
            byte[] buffer = new byte[8192];
            var writer = new PacketWriter(buffer);
            packet.Serialize(ref writer);
            return writer.Position;
        }
    }
}
