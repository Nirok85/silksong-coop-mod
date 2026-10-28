using System;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Tests for enemy/boss HP replication, world events and physical-pickup deduplication.
    /// </summary>
    public class EnemyAndPickupSyncTests
    {
        [Fact]
        public void EnemySyncPacket_Roundtrip_PreservesAllFields()
        {
            var original = new EnemySyncPacket
            {
                Header = new PacketHeader(PacketType.EnemySync, 4, 7, 1234),
                EnemyKey = 0xDEADBEEFCAFEBABEUL,
                Hp = 1234,
                Damage = 12,
                PositionX = 1.5f,
                PositionY = -2.25f,
                EventType = EnemySyncEventType.Death,
                AttackType = 3
            };

            Span<byte> buffer = stackalloc byte[64];
            var writer = new PacketWriter(buffer);
            original.Serialize(ref writer);

            var reader = new PacketReader(writer.WrittenSpan);
            var roundtrip = EnemySyncPacket.Deserialize(ref reader);

            Assert.Equal(original.EnemyKey, roundtrip.EnemyKey);
            Assert.Equal(original.Hp, roundtrip.Hp);
            Assert.Equal(original.Damage, roundtrip.Damage);
            Assert.Equal(original.PositionX, roundtrip.PositionX);
            Assert.Equal(original.PositionY, roundtrip.PositionY);
            Assert.Equal(original.EventType, roundtrip.EventType);
            Assert.Equal(original.AttackType, roundtrip.AttackType);
        }

        [Fact]
        public void EnemySnapshotPacket_Roundtrip_PreservesEntries()
        {
            var original = new EnemySnapshotPacket
            {
                Header = new PacketHeader(PacketType.EnemySnapshot, 4, 8, 5678),
                Entries = new[]
                {
                    new EnemySnapshotEntry
                    {
                        EnemyKey = 11,
                        Hp = 90,
                        Flags = 0,
                        PositionX = 3f,
                        PositionY = 4f
                    },
                    new EnemySnapshotEntry
                    {
                        EnemyKey = 22,
                        Hp = 0,
                        Flags = EnemySnapshotEntry.FlagDead,
                        PositionX = -3f,
                        PositionY = -4f
                    }
                },
                Count = 2
            };

            Span<byte> buffer = stackalloc byte[128];
            var writer = new PacketWriter(buffer);
            original.Serialize(ref writer);

            var reader = new PacketReader(writer.WrittenSpan);
            var roundtrip = EnemySnapshotPacket.Deserialize(ref reader);

            Assert.Equal(2, roundtrip.Count);
            Assert.Equal(11UL, roundtrip.Entries[0].EnemyKey);
            Assert.Equal(90, roundtrip.Entries[0].Hp);
            Assert.False(roundtrip.Entries[0].IsDead);
            Assert.Equal(22UL, roundtrip.Entries[1].EnemyKey);
            Assert.True(roundtrip.Entries[1].IsDead);
        }

        [Fact]
        public void WorldEventPacket_Roundtrip_PreservesFields()
        {
            var original = new WorldEventPacket
            {
                Header = new PacketHeader(PacketType.WorldEvent, 4, 9, 42),
                ObjectKey = 987654321,
                EventType = WorldEventType.GateOpened,
                PositionX = 10f,
                PositionY = 20f
            };

            Span<byte> buffer = stackalloc byte[64];
            var writer = new PacketWriter(buffer);
            original.Serialize(ref writer);

            var reader = new PacketReader(writer.WrittenSpan);
            var roundtrip = WorldEventPacket.Deserialize(ref reader);

            Assert.Equal(original.ObjectKey, roundtrip.ObjectKey);
            Assert.Equal(original.EventType, roundtrip.EventType);
            Assert.Equal(original.PositionX, roundtrip.PositionX);
            Assert.Equal(original.PositionY, roundtrip.PositionY);
        }

        [Fact]
        public void EnemySyncValidator_RejectsMalformedPackets()
        {
            Assert.False(PacketValidator.ValidateEnemySync(new EnemySyncPacket
            {
                EnemyKey = 0,
                Hp = 10,
                EventType = EnemySyncEventType.Hit,
                PositionX = 0,
                PositionY = 0
            }));

            Assert.False(PacketValidator.ValidateEnemySync(new EnemySyncPacket
            {
                EnemyKey = 1,
                Hp = 5,
                EventType = EnemySyncEventType.Death,
                PositionX = 0,
                PositionY = 0
            }));

            Assert.False(PacketValidator.ValidateEnemySync(new EnemySyncPacket
            {
                EnemyKey = 1,
                Hp = -1,
                EventType = EnemySyncEventType.Hit,
                PositionX = 0,
                PositionY = 0
            }));

            Assert.False(PacketValidator.ValidateEnemySync(new EnemySyncPacket
            {
                EnemyKey = 1,
                Hp = 5,
                EventType = EnemySyncEventType.Hit,
                PositionX = float.NaN,
                PositionY = 0
            }));

            Assert.True(PacketValidator.ValidateEnemySync(new EnemySyncPacket
            {
                EnemyKey = 1,
                Hp = 5,
                EventType = EnemySyncEventType.Hit,
                PositionX = 0,
                PositionY = 0
            }));
        }

        [Fact]
        public void EnemySnapshotValidator_RejectsMalformedEntries()
        {
            Assert.False(PacketValidator.ValidateEnemySnapshot(new EnemySnapshotPacket
            {
                Entries = new[]
                {
                    new EnemySnapshotEntry { EnemyKey = 1, Hp = 5, Flags = 2 }
                },
                Count = 1
            }));

            Assert.False(PacketValidator.ValidateEnemySnapshot(new EnemySnapshotPacket
            {
                Entries = new[]
                {
                    new EnemySnapshotEntry { EnemyKey = 1, Hp = PacketValidator.MaxEnemyHp + 1 }
                },
                Count = 1
            }));

            Assert.True(PacketValidator.ValidateEnemySnapshot(new EnemySnapshotPacket
            {
                Entries = new[]
                {
                    new EnemySnapshotEntry { EnemyKey = 1, Hp = 5, Flags = EnemySnapshotEntry.FlagDead }
                },
                Count = 1
            }));
        }

        [Fact]
        public void EnemySyncService_AppliesOnlyMonotonicDamage()
        {
            var service = new EnemySyncService();

            Assert.True(service.TryAcceptHit(1, 90, dead: false));
            Assert.False(service.TryAcceptHit(1, 90, dead: false)); // duplicate
            Assert.True(service.TryAcceptHit(1, 80, dead: false));
            Assert.False(service.TryAcceptHit(1, 85, dead: false)); // must not heal
            Assert.True(service.TryAcceptHit(1, 0, dead: true));
            Assert.False(service.TryAcceptHit(1, 0, dead: true)); // death only once
        }

        [Fact]
        public void EnemySyncService_RejectsStaleSnapshotsAndResurrection()
        {
            var service = new EnemySyncService();

            Assert.True(service.TryAcceptSnapshot(1, 90, dead: false, timestamp: 100));
            Assert.False(service.TryAcceptSnapshot(1, 89, dead: false, timestamp: 100));
            Assert.True(service.TryAcceptSnapshot(1, 80, dead: false, timestamp: 101));
            Assert.True(service.TryAcceptSnapshot(1, 0, dead: true, timestamp: 102));
            Assert.False(service.TryAcceptSnapshot(1, 80, dead: false, timestamp: 103));
        }

        [Fact]
        public void PersistentStatePacket_Roundtrip_PreservesFields()
        {
            var original = new PersistentStatePacket
            {
                Header = new PacketHeader(PacketType.PersistentState, 4, 11, 999),
                ItemKey = 0xABCDEF,
                ValueType = PersistentValueType.Int,
                Value = 7
            };

            Span<byte> buffer = stackalloc byte[64];
            var writer = new PacketWriter(buffer);
            original.Serialize(ref writer);

            var reader = new PacketReader(writer.WrittenSpan);
            var roundtrip = PersistentStatePacket.Deserialize(ref reader);

            Assert.Equal(original.ItemKey, roundtrip.ItemKey);
            Assert.Equal(original.ValueType, roundtrip.ValueType);
            Assert.Equal(original.Value, roundtrip.Value);
        }

        [Fact]
        public void PersistentStateValidator_RejectsMalformedPackets()
        {
            Assert.False(PacketValidator.ValidatePersistentState(new PersistentStatePacket
            {
                ItemKey = 0,
                ValueType = PersistentValueType.Bool,
                Value = 1
            }));

            Assert.False(PacketValidator.ValidatePersistentState(new PersistentStatePacket
            {
                ItemKey = 1,
                ValueType = PersistentValueType.Bool,
                Value = 2
            }));

            Assert.True(PacketValidator.ValidatePersistentState(new PersistentStatePacket
            {
                ItemKey = 1,
                ValueType = PersistentValueType.Int,
                Value = -5
            }));
        }

        [Fact]
        public void Currency_QueuedGameChange_DerivesTotalFromDelta()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, initialRosaries: 100, initialShards: 0);

            // CurrencyManager.ChangeCurrency queues the amount; the hook passes 0 as the
            // total because PlayerData.geo has not been updated yet.
            service.OnLocalCurrencyChanged(CurrencyType.Rosaries, 10, 0, Guid.NewGuid());

            Assert.Equal(110, service.SharedRosaries);
        }

        [Fact]
        public void Currency_ClaimedPickupsReplayAsDeltaZero()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, initialRosaries: 100, initialShards: 0);

            service.OnLocalCurrencyChanged(
                CurrencyType.Rosaries, 10, 0, Guid.NewGuid(), pickupKey: 77, pickupX: 5f, pickupY: 5f);
            Assert.Equal(110, service.SharedRosaries);

            CurrencySyncPacket? replay = null;
            service.OnOutgoingCurrencyBroadcast += p =>
            {
                if (p.DeltaAmount == 0 && p.PickupKey == 77)
                    replay = p;
            };

            service.BroadcastClaimedPickups();

            Assert.NotNull(replay);
            Assert.Equal(77UL, replay!.Value.PickupKey);
            Assert.Equal(0, replay.Value.DeltaAmount);
            Assert.Equal(110, replay.Value.TotalWalletAmount);

            var receiver = new VitalsSyncService();
            receiver.InitializeState(5, 5, 9, 1, 0, initialRosaries: 110, initialShards: 0);
            CurrencyType claimedCurrency = CurrencyType.ShellShards;
            ulong claimedKey = 0;
            receiver.OnRemotePickupClaimed += (currency, key, _, _) =>
            {
                claimedCurrency = currency;
                claimedKey = key;
            };

            receiver.HandleCurrencyPacket(new NetworkPeerId(1), replay.Value);

            Assert.Equal(110, receiver.SharedRosaries);
            Assert.Equal(CurrencyType.Rosaries, claimedCurrency);
            Assert.Equal(77UL, claimedKey);
        }

        [Fact]
        public void Currency_PickupKeyIsBroadcast()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, initialRosaries: 100, initialShards: 0);

            CurrencySyncPacket? sent = null;
            service.OnOutgoingCurrencyBroadcast += p => sent = p;

            service.OnLocalCurrencyChanged(
                CurrencyType.Rosaries, 10, 110, Guid.NewGuid(), pickupKey: 42, pickupX: 1f, pickupY: 2f);

            Assert.NotNull(sent);
            Assert.Equal(42UL, sent!.Value.PickupKey);
            Assert.Equal(1f, sent.Value.PickupX);
            Assert.Equal(2f, sent.Value.PickupY);
            Assert.Equal(110, service.SharedRosaries);
        }

        [Fact]
        public void Currency_SamePickupKey_IsCreditedOnce()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, initialRosaries: 100, initialShards: 0);

            service.OnLocalCurrencyChanged(
                CurrencyType.Rosaries, 10, 110, Guid.NewGuid(), pickupKey: 42, pickupX: 3f, pickupY: 4f);

            Assert.Equal(110, service.SharedRosaries);

            // The peer picked up its own copy of the same drop.
            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 1, 1),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 10,
                TotalWalletAmount = 120,
                PickupUniqueId = 1,
                PickupKey = 42,
                PickupX = 3f,
                PickupY = 4f,
                TransactionId = Guid.NewGuid()
            });

            Assert.Equal(110, service.SharedRosaries);
        }

        [Fact]
        public void Currency_LocalPickupAfterRemoteClaim_IsReverted()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, initialRosaries: 100, initialShards: 0);

            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 1, 1),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 10,
                TotalWalletAmount = 110,
                PickupUniqueId = 1,
                PickupKey = 7,
                PickupX = 5f,
                PickupY = 5f,
                TransactionId = Guid.NewGuid()
            });
            Assert.Equal(110, service.SharedRosaries);

            int revertedTotal = -1;
            service.OnSharedCurrencyChanged += (_, _, total) => revertedTotal = total;

            bool broadcast = false;
            service.OnOutgoingCurrencyBroadcast += _ => broadcast = true;

            // The local game already added the same drop before the remote packet was handled.
            service.OnLocalCurrencyChanged(
                CurrencyType.Rosaries, 10, 120, Guid.NewGuid(), pickupKey: 7, pickupX: 5f, pickupY: 5f);

            Assert.Equal(110, service.SharedRosaries);
            Assert.Equal(110, revertedTotal);
            Assert.False(broadcast);
        }

        [Fact]
        public void Currency_DifferentPickupKeys_AreBothCredited()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, initialRosaries: 100, initialShards: 0);

            service.OnLocalCurrencyChanged(
                CurrencyType.Rosaries, 10, 110, Guid.NewGuid(), pickupKey: 1, pickupX: 0f, pickupY: 0f);
            service.OnLocalCurrencyChanged(
                CurrencyType.Rosaries, 15, 125, Guid.NewGuid(), pickupKey: 2, pickupX: 10f, pickupY: 10f);

            Assert.Equal(125, service.SharedRosaries);
        }

        [Fact]
        public void Currency_PickupProximity_DeduplicatesPhysicsDrift()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, initialRosaries: 100, initialShards: 0);

            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 1, 1),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 10,
                TotalWalletAmount = 110,
                PickupUniqueId = 1,
                PickupKey = 7,
                PickupX = 5f,
                PickupY = 5f,
                TransactionId = Guid.NewGuid()
            });

            // Different hash (the drop drifted 0.5 units) but still the same physical drop.
            service.OnLocalCurrencyChanged(
                CurrencyType.Rosaries, 10, 120, Guid.NewGuid(), pickupKey: 8, pickupX: 5.4f, pickupY: 5.2f);

            Assert.Equal(110, service.SharedRosaries);
        }
    }
}
