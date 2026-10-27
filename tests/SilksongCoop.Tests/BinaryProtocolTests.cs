using System;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using Xunit;

namespace SilksongCoop.Tests
{
    public class BinaryProtocolTests
    {
        [Fact]
        public void PacketHeader_SerializationRoundtrip_MaintainsAllFields()
        {
            var header = new PacketHeader(PacketType.TransformSync, channelId: 0, tickSequence: 1042, timestamp: 987654);

            Span<byte> buffer = stackalloc byte[PacketHeader.HeaderSize];
            header.WriteTo(buffer);

            Assert.True(PacketHeader.TryReadFrom(buffer, out var deserialized));
            Assert.Equal(header.Magic, deserialized.Magic);
            Assert.Equal(header.PacketId, deserialized.PacketId);
            Assert.Equal(header.ChannelId, deserialized.ChannelId);
            Assert.Equal(header.TickSequence, deserialized.TickSequence);
            Assert.Equal(header.Timestamp, deserialized.Timestamp);
            Assert.True(deserialized.IsValidMagic);
        }

        [Fact]
        public void PacketHeader_InvalidMagic_Rejected()
        {
            Span<byte> buffer = stackalloc byte[PacketHeader.HeaderSize];
            buffer[0] = 0xAA; // Corrupted magic byte
            buffer[1] = 0xBB;

            Assert.False(PacketHeader.TryReadFrom(buffer, out _));
        }

        [Fact]
        public void TransformSyncPacket_SerializationRoundtrip_MatchesExactValues()
        {
            var original = new TransformSyncPacket
            {
                Header = new PacketHeader(PacketType.TransformSync, 0, 42, 1000),
                PositionX = 12.345f,
                PositionY = -67.890f,
                VelocityX = 5.25f,
                VelocityY = -9.81f,
                FacingDirection = 1,
                StateFlags = TransformSyncPacket.FlagGrounded | TransformSyncPacket.FlagDashing,
                AnimationClipHash = 987654321,
                NormalizedTime = 32768,
                SceneNameHash = 12345678
            };

            Span<byte> buffer = stackalloc byte[128];
            var writer = new PacketWriter(buffer);
            original.Serialize(ref writer);

            Assert.Equal(41, writer.Position); // 12 header + 29 payload = 41 bytes

            var reader = new PacketReader(writer.WrittenSpan);
            var deserialized = TransformSyncPacket.Deserialize(ref reader);

            Assert.Equal(original.Header.PacketId, deserialized.Header.PacketId);
            Assert.Equal(original.PositionX, deserialized.PositionX);
            Assert.Equal(original.PositionY, deserialized.PositionY);
            Assert.Equal(original.VelocityX, deserialized.VelocityX);
            Assert.Equal(original.SceneNameHash, deserialized.SceneNameHash);
            Assert.Equal(original.VelocityY, deserialized.VelocityY);
            Assert.Equal(original.FacingDirection, deserialized.FacingDirection);
            Assert.True(deserialized.FacingRight);
            Assert.True(deserialized.IsGrounded);
            Assert.True(deserialized.IsDashing);
            Assert.Equal(original.AnimationClipHash, deserialized.AnimationClipHash);
            Assert.Equal(original.NormalizedTime, deserialized.NormalizedTime);
        }

        [Theory]
        [InlineData(ActionType.SlashNormal)]
        [InlineData(ActionType.SlashDown)]
        [InlineData(ActionType.HarpoonDash)]
        [InlineData(ActionType.NeedleThrow)]
        [InlineData(ActionType.ThreadBindStart)]
        [InlineData(ActionType.ToolDeploy)]
        public void ActionTriggerPacket_SerializationRoundtrip_AllActionTypes(ActionType action)
        {
            var original = new ActionTriggerPacket
            {
                Header = new PacketHeader(PacketType.ActionTrigger, 1, 100, 2000),
                Action = action,
                ActionSequenceId = 555,
                OriginX = 1.0f,
                OriginY = 2.0f,
                DirectionX = 0.707f,
                DirectionY = -0.707f,
                Parameter = 42
            };

            Span<byte> buffer = stackalloc byte[128];
            var writer = new PacketWriter(buffer);
            original.Serialize(ref writer);

            var reader = new PacketReader(writer.WrittenSpan);
            var deserialized = ActionTriggerPacket.Deserialize(ref reader);

            Assert.Equal(original.Action, deserialized.Action);
            Assert.Equal(original.ActionSequenceId, deserialized.ActionSequenceId);
            Assert.Equal(original.OriginX, deserialized.OriginX);
            Assert.Equal(original.OriginY, deserialized.OriginY);
            Assert.Equal(original.DirectionX, deserialized.DirectionX);
            Assert.Equal(original.DirectionY, deserialized.DirectionY);
            Assert.Equal(original.Parameter, deserialized.Parameter);
        }

        [Fact]
        public void VitalsSyncPacket_SerializationRoundtrip_PreservesVitals()
        {
            var original = new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 10, 3000),
                CurrentHealth = 5,
                CurrentSilk = 9,
                MaxHealth = 8,
                MaxSilkSpools = 3,
                NeedleUpgradeLevel = 2
            };

            Span<byte> buffer = stackalloc byte[64];
            var writer = new PacketWriter(buffer);
            original.Serialize(ref writer);

            var reader = new PacketReader(writer.WrittenSpan);
            var deserialized = VitalsSyncPacket.Deserialize(ref reader);

            Assert.Equal(original.CurrentHealth, deserialized.CurrentHealth);
            Assert.Equal(original.CurrentSilk, deserialized.CurrentSilk);
            Assert.Equal(original.MaxHealth, deserialized.MaxHealth);
            Assert.Equal(original.MaxSilkSpools, deserialized.MaxSilkSpools);
            Assert.Equal(original.NeedleUpgradeLevel, deserialized.NeedleUpgradeLevel);
        }

        [Fact]
        public void CurrencySyncPacket_SerializationRoundtrip_WithTransactionGuid()
        {
            var txId = Guid.NewGuid();
            var original = new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 77, 4000),
                Currency = CurrencyType.Rosaries,
                TotalWalletAmount = 1450,
                DeltaAmount = 25,
                PickupUniqueId = 998877,
                TransactionId = txId
            };

            Span<byte> buffer = stackalloc byte[64];
            var writer = new PacketWriter(buffer);
            original.Serialize(ref writer);

            var reader = new PacketReader(writer.WrittenSpan);
            var deserialized = CurrencySyncPacket.Deserialize(ref reader);

            Assert.Equal(original.Currency, deserialized.Currency);
            Assert.Equal(original.TotalWalletAmount, deserialized.TotalWalletAmount);
            Assert.Equal(original.DeltaAmount, deserialized.DeltaAmount);
            Assert.Equal(original.PickupUniqueId, deserialized.PickupUniqueId);
            Assert.Equal(txId, deserialized.TransactionId);
        }

        [Fact]
        public void ItemUnlockPacket_SerializationRoundtrip_AllCategories()
        {
            var txId = Guid.NewGuid();
            var categories = new[]
            {
                ItemCategory.Crest,
                ItemCategory.Tool,
                ItemCategory.Ability,
                ItemCategory.KeyItem
            };

            Span<byte> buffer = stackalloc byte[128];
            foreach (var category in categories)
            {
                var original = new ItemUnlockPacket
                {
                    Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 88, 5000),
                    ItemId = $"ITEM_{category}_01",
                    Category = category,
                    TransactionId = txId,
                    Timestamp = 5000
                };

                var writer = new PacketWriter(buffer);
                original.Serialize(ref writer);

                var reader = new PacketReader(writer.WrittenSpan);
                var deserialized = ItemUnlockPacket.Deserialize(ref reader);

                Assert.Equal(original.ItemId, deserialized.ItemId);
                Assert.Equal(category, deserialized.Category);
                Assert.Equal(txId, deserialized.TransactionId);
                Assert.Equal(5000u, deserialized.Timestamp);
            }
        }

        [Fact]
        public void DeathLifecyclePacket_SerializationRoundtrip_AllEventTypes()
        {
            var original = new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 99, 6000),
                EventType = DeathEventType.SpectatorCountdownSync,
                FallenPlayerIndex = 1,
                CoordinateX = 45.5f,
                CoordinateY = 12.0f,
                RemainingSeconds = 24.5f,
                BenchSceneHash = 12345,
                Epoch = 1
            };

            Span<byte> buffer = stackalloc byte[64];
            var writer = new PacketWriter(buffer);
            original.Serialize(ref writer);

            var reader = new PacketReader(writer.WrittenSpan);
            var deserialized = DeathLifecyclePacket.Deserialize(ref reader);

            Assert.Equal(DeathEventType.SpectatorCountdownSync, deserialized.EventType);
            Assert.Equal(1, deserialized.FallenPlayerIndex);
            Assert.Equal(45.5f, deserialized.CoordinateX);
            Assert.Equal(12.0f, deserialized.CoordinateY);
            Assert.Equal(24.5f, deserialized.RemainingSeconds);
            Assert.Equal(12345, deserialized.BenchSceneHash);
        }

        [Fact]
        public void BenchCheckpointSyncPacket_SerializationRoundtrip_MatchesExactValues()
        {
            var original = new BenchCheckpointSyncPacket
            {
                Header = new PacketHeader(PacketType.BenchCheckpointSync, 0, 10, 5000),
                SceneName = "Bonebottom_01",
                RespawnMarker = "RestBench_01",
                FacingDirection = 1,
                AtBench = true
            };

            Span<byte> buffer = stackalloc byte[128];
            var writer = new PacketWriter(buffer);
            original.Serialize(ref writer);

            var reader = new PacketReader(writer.WrittenSpan);
            var deserialized = BenchCheckpointSyncPacket.Deserialize(ref reader);

            Assert.Equal(original.Header.PacketId, deserialized.Header.PacketId);
            Assert.Equal(original.SceneName, deserialized.SceneName);
            Assert.Equal(original.RespawnMarker, deserialized.RespawnMarker);
            Assert.Equal(original.FacingDirection, deserialized.FacingDirection);
            Assert.Equal(original.AtBench, deserialized.AtBench);
        }

        [Fact]
        public void SceneTransitionPacket_SerializationRoundtrip_MatchesExactValues()
        {
            var original = new SceneTransitionPacket
            {
                Header = new PacketHeader(PacketType.SceneTransition, 0, 11, 5500),
                TargetScene = "MossGrotto_03",
                EntryGate = "Gate_Left",
                SceneNameHash = 98765,
                TransitionPhase = 1
            };

            Span<byte> buffer = stackalloc byte[128];
            var writer = new PacketWriter(buffer);
            original.Serialize(ref writer);

            var reader = new PacketReader(writer.WrittenSpan);
            var deserialized = SceneTransitionPacket.Deserialize(ref reader);

            Assert.Equal(original.Header.PacketId, deserialized.Header.PacketId);
            Assert.Equal(original.TargetScene, deserialized.TargetScene);
            Assert.Equal(original.EntryGate, deserialized.EntryGate);
            Assert.Equal(original.SceneNameHash, deserialized.SceneNameHash);
            Assert.Equal(original.TransitionPhase, deserialized.TransitionPhase);
        }

        [Fact]
        public void PacketWriter_BufferOverflow_ThrowsInvalidOperationException()
        {
            bool threw = false;
            try
            {
                Span<byte> smallBuffer = stackalloc byte[4];
                var writer = new PacketWriter(smallBuffer);
                writer.WriteInt32(100);
                writer.WriteByte(1); // Exceeds 4 bytes
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Assert.True(threw, "PacketWriter must throw InvalidOperationException on overflow.");
        }

        [Fact]
        public void PacketReader_BufferUnderflow_ThrowsInvalidOperationException()
        {
            bool threw = false;
            try
            {
                Span<byte> buffer = stackalloc byte[2];
                var reader = new PacketReader(buffer);
                reader.ReadInt32(); // Requests 4 bytes from 2-byte buffer
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Assert.True(threw, "PacketReader must throw InvalidOperationException on underflow.");
        }
    }
}
