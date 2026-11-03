using System;
using System.Collections.Generic;
using SilksongCoop.Core.Progression;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using Xunit;

namespace SilksongCoop.Tests.Progression
{
    public class QuestStateTests
    {
        [Theory]
        [InlineData("fixerQuestBoardConvo")]
        [InlineData("HuntressQuestOffered")]
        [InlineData("MushroomQuestFound1")]
        [InlineData("WoodWitchTalkedPostQuest")]
        [InlineData("HeardMelodyConductorNoQuest")]
        public void Filter_AcceptsQuestRelatedFlags(string name)
        {
            Assert.True(QuestFlagFilter.IsQuestRelated(name));
        }

        [Theory]
        [InlineData("QuestPaneHasNew")]
        [InlineData("seenJournalQuestUpdateMsg")]
        [InlineData("nailUpgrades")]
        [InlineData("hasDash")]
        [InlineData("shellShards")]
        [InlineData("MetSethNPC")]
        [InlineData("BelltownDoctorFixOffered")]
        [InlineData("PinsmithUpg2Offered")]
        [InlineData("enclaveNPC_songKnightFan")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("quest with space")]
        public void Filter_RejectsUnrelatedOrUnsafeFlags(string? name)
        {
            Assert.False(QuestFlagFilter.IsQuestRelated(name));
        }

        [Fact]
        public void Filter_RejectsOverlongKeys()
        {
            Assert.False(QuestFlagFilter.IsQuestRelated(
                "quest_" + new string('a', QuestFlagFilter.MaxKeyLength)));
        }

        [Fact]
        public void Snapshot_IgnoresNonQuestFields()
        {
            var snapshot = new QuestFlagSnapshot();
            snapshot.SetBool("HuntressQuestOffered", true);
            snapshot.SetInt("nailUpgrades", 4);
            snapshot.SetBool("hasDash", true);

            Assert.Equal(1, snapshot.Count);
            Assert.True(snapshot.TryGet("HuntressQuestOffered", out var value));
            Assert.Equal(QuestFlagValue.FromBool(true), value);
        }

        [Fact]
        public void Diff_ReturnsAddedAndChangedFlagsOnly()
        {
            var before = new QuestFlagSnapshot();
            before.SetBool("HuntressQuestOffered", false);
            before.SetInt("HuntressQuestProgress", 1);
            before.SetBool("UnchangedQuestFlag", true);

            var after = new QuestFlagSnapshot();
            after.SetBool("HuntressQuestOffered", true);      // changed
            after.SetInt("HuntressQuestProgress", 1);         // unchanged
            after.SetBool("UnchangedQuestFlag", true);        // unchanged
            after.SetBool("NewQuestFlag", true);              // added

            var changes = QuestFlagDiff.Compute(before, after);

            Assert.Equal(2, changes.Count);
            Assert.Contains(changes, c => c.Key == "HuntressQuestOffered" && c.Value.BoolValue);
            Assert.Contains(changes, c => c.Key == "NewQuestFlag" && c.Value.BoolValue);
        }

        [Fact]
        public void Packet_Roundtrip_PreservesBoolAndIntEntries()
        {
            var original = new QuestStatePacket
            {
                Header = new PacketHeader(PacketType.QuestState, 4, 7, 1234),
                Count = 2,
                Entries = new[]
                {
                    new QuestFlagEntry { Kind = QuestFlagKind.Bool, Key = "HuntressQuestOffered", BoolValue = true },
                    new QuestFlagEntry { Kind = QuestFlagKind.Int, Key = "HuntressQuestProgress", IntValue = 3 }
                }
            };

            Span<byte> buffer = stackalloc byte[256];
            var writer = new PacketWriter(buffer);
            original.Serialize(ref writer);

            var reader = new PacketReader(writer.WrittenSpan);
            var roundtrip = QuestStatePacket.Deserialize(ref reader);

            Assert.Equal(original.Header.PacketId, roundtrip.Header.PacketId);
            Assert.Equal(2, roundtrip.Count);
            Assert.Equal(QuestFlagKind.Bool, roundtrip.Entries[0].Kind);
            Assert.Equal("HuntressQuestOffered", roundtrip.Entries[0].Key);
            Assert.True(roundtrip.Entries[0].BoolValue);
            Assert.Equal(QuestFlagKind.Int, roundtrip.Entries[1].Kind);
            Assert.Equal(3, roundtrip.Entries[1].IntValue);
        }

        [Fact]
        public void Header_RecognizesQuestStatePacket()
        {
            var header = new PacketHeader(PacketType.QuestState, 4, 1, 0);
            Assert.True(header.IsValidPacketType);
        }

        [Fact]
        public void Validator_AcceptsValidPacket()
        {
            var packet = new QuestStatePacket
            {
                Count = 2,
                Entries = new[]
                {
                    new QuestFlagEntry { Kind = QuestFlagKind.Bool, Key = "MushroomQuestFound1", BoolValue = true },
                    new QuestFlagEntry { Kind = QuestFlagKind.Int, Key = "HuntressQuestProgress", IntValue = -1 }
                }
            };

            Assert.True(PacketValidator.ValidateQuestState(packet));
        }

        [Fact]
        public void Validator_RejectsNonQuestKey()
        {
            var packet = new QuestStatePacket
            {
                Count = 1,
                Entries = new[] { new QuestFlagEntry { Kind = QuestFlagKind.Bool, Key = "hasDash", BoolValue = true } }
            };

            Assert.False(PacketValidator.ValidateQuestState(packet));
        }

        [Fact]
        public void Validator_RejectsDuplicateKeys()
        {
            var packet = new QuestStatePacket
            {
                Count = 2,
                Entries = new[]
                {
                    new QuestFlagEntry { Kind = QuestFlagKind.Bool, Key = "MushroomQuestFound1", BoolValue = true },
                    new QuestFlagEntry { Kind = QuestFlagKind.Bool, Key = "MushroomQuestFound1", BoolValue = false }
                }
            };

            Assert.False(PacketValidator.ValidateQuestState(packet));
        }

        [Fact]
        public void Validator_RejectsTooManyEntriesAndBadCounts()
        {
            var tooMany = new QuestStatePacket
            {
                Count = QuestStatePacket.MaxEntries + 1,
                Entries = new QuestFlagEntry[QuestStatePacket.MaxEntries + 1]
            };
            Assert.False(PacketValidator.ValidateQuestState(tooMany));

            var nullEntries = new QuestStatePacket { Count = 1, Entries = null! };
            Assert.False(PacketValidator.ValidateQuestState(nullEntries));

            var tooSmallArray = new QuestStatePacket { Count = 2, Entries = new QuestFlagEntry[1] };
            Assert.False(PacketValidator.ValidateQuestState(tooSmallArray));
        }

        [Fact]
        public void Validator_RejectsOutOfRangeIntAndUnknownKind()
        {
            var badInt = new QuestStatePacket
            {
                Count = 1,
                Entries = new[] { new QuestFlagEntry { Kind = QuestFlagKind.Int, Key = "QuestCounter", IntValue = 1_000_001 } }
            };
            Assert.False(PacketValidator.ValidateQuestState(badInt));

            var badKind = new QuestStatePacket
            {
                Count = 1,
                Entries = new[] { new QuestFlagEntry { Kind = (QuestFlagKind)99, Key = "QuestCounter" } }
            };
            Assert.False(PacketValidator.ValidateQuestState(badKind));
        }
    }
}
