using System;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Regression coverage for the full 2026-11-09 audit fix pass. Existing test files are
    /// intentionally untouched; every case here failed before the corresponding fix.
    /// </summary>
    public class FullFixRegressionTests
    {
        private static VitalsSyncService Wallet(int rosaries)
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, rosaries, 0);
            return service;
        }

        private static DeathLifecyclePacket DeathPacket(DeathEventType type, uint epoch)
        {
            return new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 0, 1),
                EventType = type,
                Epoch = epoch,
                CoordinateX = 1f,
                CoordinateY = 2f
            };
        }

        // ---------------------------------------------------------------------
        // Currency: a positive packet must not resurrect a concurrent spend.
        // ---------------------------------------------------------------------
        [Fact]
        public void Currency_ConcurrentSpendAndGain_ConvergesToSameWallet()
        {
            var a = Wallet(100);
            var b = Wallet(100);

            CurrencySyncPacket aPacket = default;
            CurrencySyncPacket bPacket = default;
            a.OnOutgoingCurrencyBroadcast += packet => aPacket = packet;
            b.OnOutgoingCurrencyBroadcast += packet => bPacket = packet;

            // A spends 80 while B gains 10; neither packet knows about the other change.
            a.OnLocalCurrencyChanged(CurrencyType.Rosaries, -80, 0, Guid.NewGuid());
            b.OnLocalCurrencyChanged(CurrencyType.Rosaries, 10, 0, Guid.NewGuid());

            a.HandleCurrencyPacket(new NetworkPeerId(2), bPacket);
            b.HandleCurrencyPacket(new NetworkPeerId(1), aPacket);

            Assert.Equal(30, a.SharedRosaries);
            Assert.Equal(30, b.SharedRosaries);
        }

        [Fact]
        public void Currency_StaleResync_DoesNotResurrectLocalSpend()
        {
            var service = Wallet(100);
            var peer = new NetworkPeerId(2);

            service.OnLocalCurrencyChanged(CurrencyType.Rosaries, -80, 0, Guid.NewGuid());

            // The peer's periodic resync was generated before it saw the spend.
            service.HandleCurrencyPacket(peer, new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 5, 5),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 0,
                TotalWalletAmount = 110,
                PickupUniqueId = 5,
                TransactionId = Guid.NewGuid()
            });

            Assert.Equal(20, service.SharedRosaries);

            // Once the peer has processed the spend, its resync is allowed to reconcile.
            service.HandleCurrencyPacket(peer, new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 6, 6),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 0,
                TotalWalletAmount = 30,
                PickupUniqueId = 6,
                TransactionId = Guid.NewGuid()
            });

            Assert.Equal(30, service.SharedRosaries);
        }

        // ---------------------------------------------------------------------
        // Currency: proximity dedup only reconciles local<->remote copies.
        // ---------------------------------------------------------------------
        [Fact]
        public void Currency_TwoNearbyLocalPickups_AreBothCredited()
        {
            var service = Wallet(100);

            service.OnLocalCurrencyChanged(
                CurrencyType.Rosaries, 1, 0, Guid.NewGuid(), pickupKey: 101, pickupX: 1f, pickupY: 1f);
            service.OnLocalCurrencyChanged(
                CurrencyType.Rosaries, 1, 0, Guid.NewGuid(), pickupKey: 102, pickupX: 1.1f, pickupY: 1f);

            Assert.Equal(102, service.SharedRosaries);
        }

        [Fact]
        public void Currency_TwoNearbyRemoteClaims_AreBothCredited()
        {
            var service = Wallet(100);

            service.HandleCurrencyPacket(new NetworkPeerId(2), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 1, 1),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 1,
                TotalWalletAmount = 101,
                PickupUniqueId = 1,
                PickupKey = 201,
                PickupX = 5f,
                PickupY = 5f,
                TransactionId = Guid.NewGuid()
            });
            service.HandleCurrencyPacket(new NetworkPeerId(2), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 2, 2),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 1,
                TotalWalletAmount = 102,
                PickupUniqueId = 2,
                PickupKey = 202,
                PickupX = 5.1f,
                PickupY = 5f,
                TransactionId = Guid.NewGuid()
            });

            Assert.Equal(102, service.SharedRosaries);
        }

        // ---------------------------------------------------------------------
        // Death lifecycle: one wipe = one epoch even when respawns are staggered.
        // ---------------------------------------------------------------------
        [Fact]
        public void Death_StaggeredRespawn_DoesNotHidePartnerDeath()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = new NetworkPeerId(2);
            coordinator.RegisterRemotePeer(peer);

            coordinator.ForceBenchRespawn(notifyPartner: false); // local reload starts, epoch 1

            // The partner finished the same wipe first and advanced the shared epoch.
            coordinator.HandleDeathPacket(peer, DeathPacket(DeathEventType.BenchCheckpointUpdate, 2));
            Assert.Equal(2u, coordinator.CurrentEpoch);

            coordinator.ResetAfterBenchRespawn();

            // The local reload must not advance the epoch a second time for the same wipe.
            Assert.Equal(2u, coordinator.CurrentEpoch);

            coordinator.HandleDeathPacket(peer, DeathPacket(DeathEventType.SoloDeathOccurred, 2));

            Assert.False(coordinator.IsRemotePeerAlive(peer));
            Assert.False(coordinator.IsPartnerAlive);
        }

        // ---------------------------------------------------------------------
        // Persistent-state revisions are only comparable within one origin.
        // ---------------------------------------------------------------------
        [Fact]
        public void RevisionLedger_AnotherOriginDoesNotErasePreviousWatermark()
        {
            var ledger = new RevisionLedger();
            ledger.Record(1, 5, origin: 10);
            ledger.Record(1, 1, origin: 20);

            Assert.True(ledger.IsStale(1, 4, origin: 10));
            Assert.True(ledger.IsStale(1, 5, origin: 10));
            Assert.False(ledger.IsStale(1, 2, origin: 20));
            Assert.Equal(1, ledger.Count);
        }

        // ---------------------------------------------------------------------
        // Repeatable boss sequences reuse hierarchy keys and must not stay dead.
        // ---------------------------------------------------------------------
        [Fact]
        public void DeadEnemyRegistry_SequenceBossesAreDropped_PersistentBossesRemain()
        {
            var registry = new DeadEnemyRegistry();
            registry.Register(1, 0f, 0f, isBoss: true);
            registry.Register(2, 0f, 0f, isBoss: true, isSequenceBoss: true);
            registry.Register(3, 0f, 0f, isBoss: false);

            var dropped = registry.DropSequenceBosses();

            Assert.Single(dropped);
            Assert.Equal(2UL, dropped[0]);
            Assert.True(registry.Contains(1));
            Assert.False(registry.Contains(2));
            Assert.True(registry.Contains(3));

            // An ordinary scene change still keeps persistent bosses and drops regular kills.
            Assert.Equal(1, registry.DropRespawnable());
            Assert.True(registry.Contains(1));
            Assert.False(registry.Contains(3));
        }

        [Fact]
        public void EnemySyncService_Forget_AllowsSequenceBossToRespawn()
        {
            var service = new EnemySyncService();
            service.NoteLocalEnemyState(7, 0, dead: true);

            // A dead entry cannot be resurrected by a later snapshot...
            Assert.False(service.TryAcceptSnapshot(7, 100, dead: false, timestamp: 10));

            // ...until the sequence ends and the record is forgotten.
            Assert.True(service.Forget(7));
            Assert.True(service.TryAcceptSnapshot(7, 100, dead: false, timestamp: 11));
        }
    }
}
