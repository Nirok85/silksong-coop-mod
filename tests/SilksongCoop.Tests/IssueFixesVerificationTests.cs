using System;
using System.Collections.Generic;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using SilksongCoop.Tests.TestHarness;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Regression tests for the findings in GitHub issues #1 and #2.
    /// </summary>
    public class IssueFixesVerificationTests
    {
        // Issue #1.6 — a positive delta may advertise a higher snapshot than the local
        // wallet (a previous gain was missed); reconcile upward.
        [Fact]
        public void Currency_PositiveDelta_ReconcilesToSenderSnapshot()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, initialRosaries: 100, initialShards: 0);

            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 1, 1),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 10,
                TotalWalletAmount = 120,
                TransactionId = Guid.NewGuid()
            });

            Assert.Equal(120, service.SharedRosaries);
        }

        // Issue #1.6 — a stale negative packet must not resurrect a balance that was
        // already spent down (the old "max with total" would have restored 15).
        [Fact]
        public void Currency_NegativeDelta_DoesNotResurrectStaleTotal()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, initialRosaries: 10, initialShards: 0);

            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 1, 1),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = -35,
                TotalWalletAmount = 15,
                TransactionId = Guid.NewGuid()
            });

            Assert.Equal(0, service.SharedRosaries);
        }

        // Issue #1.6 — the GUID window is finite (500). A replayed packet must also be
        // rejected by the monotonic per-sender sequence even with a fresh GUID.
        [Fact]
        public void Currency_ReplayedSequence_IsRejectedEvenWithNewGuid()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, initialRosaries: 0, initialShards: 0);

            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 1, 1),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 10,
                TotalWalletAmount = 10,
                PickupUniqueId = 1,
                TransactionId = Guid.NewGuid()
            });

            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 2, 2),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 10,
                TotalWalletAmount = 20,
                PickupUniqueId = 2,
                TransactionId = Guid.NewGuid()
            });

            Assert.Equal(20, service.SharedRosaries);

            // Replay of sequence #1 with a brand-new GUID: rejected.
            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 3, 3),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 10,
                TotalWalletAmount = 30,
                PickupUniqueId = 1,
                TransactionId = Guid.NewGuid()
            });

            Assert.Equal(20, service.SharedRosaries);
        }

        // Issue #2.1 — the two-coordinator scenario from the review: host spectates,
        // partner walks into another room, host's revive timer falls back to a solo bench
        // respawn. The living partner must not be killed and must mark the host alive.
        [Fact]
        public void SoloBenchFallback_OverTransport_DoesNotWipeLivingPartner()
        {
            using var harness = new DualClientTestHarness();

            bool clientWiped = false;
            harness.ClientDeath.OnTeamWipeTriggered += () => clientWiped = true;

            // Host is spectating; partner is alive.
            harness.HostDeath.IsPartnerAlive = true;
            harness.HostDeath.OnLocalPlayerDied(1f, 2f);
            harness.DeliverAll();
            Assert.Equal(DeathState.Spectating, harness.HostDeath.CurrentState);

            // Cross-scene revive fallback (as ReviveAtPartner does).
            harness.HostDeath.ForceBenchRespawn(notifyPartner: false);
            harness.DeliverAll();

            Assert.False(clientWiped);
            Assert.Equal(DeathState.Alive, harness.ClientDeath.CurrentState);
            Assert.True(harness.ClientDeath.IsPartnerAlive);
        }

        // Issue #1.5 — spawn type is independent from the facing flag. A checkpoint with
        // spawn type 2 and facing right used to be rejected by the validator.
        [Fact]
        public void BenchCheckpoint_SpawnTypeTwoAndFacingRight_IsValid()
        {
            var packet = new BenchCheckpointSyncPacket
            {
                Header = new PacketHeader(PacketType.BenchCheckpointSync, 0, 1, 1),
                SceneName = "DeepDocks_01",
                RespawnMarker = "Bench_Docks_Rest",
                SpawnType = 2,
                FacingRight = true
            };

            Assert.True(PacketValidator.ValidateBenchCheckpoint(packet));
            Assert.Equal(2, packet.FacingDirection);
            Assert.True(packet.AtBench);
        }

        // Issue #1.3 — a modified peer must not be able to push MaxHealth = 255 into the
        // partner's save. Honest values still pass.
        [Fact]
        public void Vitals_OutOfRangeProgression_IsRejected()
        {
            Assert.False(PacketValidator.ValidateVitalsSync(new VitalsSyncPacket
            {
                MaxHealth = 255,
                CurrentHealth = 5,
                MaxSilkSpools = 1,
                NeedleUpgradeLevel = 0
            }));

            Assert.False(PacketValidator.ValidateVitalsSync(new VitalsSyncPacket
            {
                MaxHealth = 5,
                CurrentHealth = 5,
                MaxSilkSpools = 200,
                NeedleUpgradeLevel = 0
            }));

            Assert.True(PacketValidator.ValidateVitalsSync(new VitalsSyncPacket
            {
                MaxHealth = 10,
                CurrentHealth = 10,
                MaxSilkSpools = 3,
                CurrentSilk = 27,
                NeedleUpgradeLevel = 4
            }));
        }

        // Issue #1.3 — absurd wallet values are rejected before they reach PlayerData.
        [Fact]
        public void Currency_TotalAboveSaneCeiling_IsRejected()
        {
            Assert.False(PacketValidator.ValidateCurrencySync(new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                TotalWalletAmount = int.MaxValue,
                DeltaAmount = 1,
                TransactionId = Guid.NewGuid()
            }));

            Assert.True(PacketValidator.ValidateCurrencySync(new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                TotalWalletAmount = 100_000_000,
                DeltaAmount = 1,
                TransactionId = Guid.NewGuid()
            }));
        }

        // Issue #2.1 — a solo bench fallback must not broadcast TeamWipe, and the peer
        // must merely mark us alive again instead of killing its own player.
        [Fact]
        public void SoloBenchRespawn_DoesNotWipeLivingPartner()
        {
            var sender = new DeathLifecycleCoordinator();
            DeathLifecyclePacket? outgoing = null;
            sender.OnOutgoingDeathPacket += p => outgoing = p;

            sender.ForceBenchRespawn(notifyPartner: false);

            Assert.NotNull(outgoing);
            Assert.Equal(DeathEventType.SoloBenchRespawn, outgoing!.Value.EventType);

            var receiver = new DeathLifecycleCoordinator();
            bool wiped = false;
            receiver.OnTeamWipeTriggered += () => wiped = true;

            receiver.HandleDeathPacket(new NetworkPeerId(2), outgoing.Value);

            Assert.False(wiped);
            Assert.Equal(DeathState.Alive, receiver.CurrentState);
            Assert.True(receiver.IsPartnerAlive);
        }

        // Issue #2.3 — the new signed-delta counter ids must bypass the once-per-item
        // unlock dedup, otherwise a second pickup of the same value is swallowed.
        [Fact]
        public void CounterDeltaPseudoItems_AreNotDeduplicatedByItemId()
        {
            var inventory = new InventorySyncService();
            var received = new List<string>();
            inventory.OnRemoteItemUnlocked += (id, _) => received.Add(id);

            inventory.HandleItemUnlockPacket(new NetworkPeerId(1), new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 1, 1),
                ItemId = "heartPieces:+1",
                Category = ItemCategory.KeyItem,
                TransactionId = Guid.NewGuid()
            });
            inventory.HandleItemUnlockPacket(new NetworkPeerId(1), new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 1, 2),
                ItemId = "heartPieces:+1",
                Category = ItemCategory.KeyItem,
                TransactionId = Guid.NewGuid()
            });

            Assert.Equal(2, received.Count);
        }
    }
}
