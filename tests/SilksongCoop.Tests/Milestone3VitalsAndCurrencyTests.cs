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
    public class Milestone3VitalsAndCurrencyTests
    {
        // =========================================================================
        // Category 1: Independent Live Vitals (Features F15, F16)
        // =========================================================================

        [Fact]
        public void F15_IndependentLiveHP_DamageTakenByHost_DoesNotAffectClient()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, 100, 20);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, 100, 20);

            int observedHostHpByClient = -1;
            harness.ClientVitals.OnRemoteVitalsObserved += (hp, silk) => observedHostHpByClient = hp;

            // Host takes 2 damage -> local health becomes 3
            harness.HostVitals.OnLocalDamageTaken(2, 3);
            harness.DeliverAll();

            // Host health is 3, Client health is strictly untouched (still 5)
            Assert.Equal(3, harness.HostVitals.LocalHealth);
            Assert.Equal(5, harness.ClientVitals.LocalHealth);
            Assert.Equal(3, observedHostHpByClient);

            // Client takes 1 damage -> local health becomes 4
            int observedClientHpByHost = -1;
            harness.HostVitals.OnRemoteVitalsObserved += (hp, silk) => observedClientHpByHost = hp;

            harness.ClientVitals.OnLocalDamageTaken(1, 4);
            harness.DeliverAll();

            Assert.Equal(3, harness.HostVitals.LocalHealth);
            Assert.Equal(4, harness.ClientVitals.LocalHealth);
            Assert.Equal(4, observedClientHpByHost);

            harness.AssertVitalsDecoupled(3, 4);
        }

        [Fact]
        public void F15_IndependentLiveHP_HealingByHost_UpdatesLocalOnly()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(2, 5, 9, 1, 0, 0, 0);
            harness.ClientVitals.InitializeState(4, 5, 9, 1, 0, 0, 0);

            int observedHp = -1;
            harness.ClientVitals.OnRemoteVitalsObserved += (hp, silk) => observedHp = hp;

            // Host heals 2 HP (bind / bench)
            harness.HostVitals.OnLocalHealthGained(2, 4);
            harness.DeliverAll();

            Assert.Equal(4, harness.HostVitals.LocalHealth);
            Assert.Equal(4, harness.ClientVitals.LocalHealth); // Client HP unchanged
            Assert.Equal(4, observedHp);
        }

        [Fact]
        public void F16_IndependentLiveSilk_SkillCastByClient_DoesNotDrainHostSilk()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, 0, 0);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, 0, 0);

            int observedSilk = -1;
            harness.HostVitals.OnRemoteVitalsObserved += (hp, silk) => observedSilk = silk;

            // Client uses 4 silk for Harpoon Dash
            harness.ClientVitals.OnLocalSilkUsed(4, 5);
            harness.DeliverAll();

            Assert.Equal(5, harness.ClientVitals.LocalSilk);
            Assert.Equal(9, harness.HostVitals.LocalSilk); // Host Silk is untouched
            Assert.Equal(5, observedSilk);
        }

        [Fact]
        public void F16_IndependentLiveSilk_SilkGainByHost_DoesNotGrantSilkToClient()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 2, 1, 0, 0, 0);
            harness.ClientVitals.InitializeState(5, 5, 5, 1, 0, 0, 0);

            // Host strikes enemy and gains 4 silk
            harness.HostVitals.OnLocalSilkGained(4, 6);
            harness.DeliverAll();

            Assert.Equal(6, harness.HostVitals.LocalSilk);
            Assert.Equal(5, harness.ClientVitals.LocalSilk); // Client Silk not granted
        }

        [Fact]
        public void F15_F16_SimultaneousCrossDamageAndSilkConsumption()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, 0, 0);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, 0, 0);

            // Host takes damage and uses silk simultaneously
            harness.HostVitals.OnLocalDamageTaken(3, 2);
            harness.HostVitals.OnLocalSilkUsed(3, 6);

            // Client takes damage
            harness.ClientVitals.OnLocalDamageTaken(1, 4);

            harness.DeliverAll();

            harness.AssertVitalsDecoupled(expectedHostHp: 2, expectedClientHp: 4, expectedHostSilk: 6, expectedClientSilk: 9);
        }

        // =========================================================================
        // Category 2: Shared Progression Upgrades (Features F17, F18, F19)
        // =========================================================================

        [Fact]
        public void F17_SharedMaxHealthUpgrade_ReplicatesMonotonically()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, 0, 0);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, 0, 0);

            int clientReportedMaxHp = -1;
            harness.ClientVitals.OnSharedMaxHealthChanged += newMax => clientReportedMaxHp = newMax;

            // Host finds 4th mask shard -> MaxHealth increases from 5 to 6
            harness.HostVitals.OnLocalMaxHealthUpgrade(6);
            harness.DeliverAll();

            Assert.Equal(6, harness.HostVitals.SharedMaxHealth);
            Assert.Equal(6, harness.ClientVitals.SharedMaxHealth);
            Assert.Equal(6, clientReportedMaxHp);

            // Client later finds another mask container -> increases from 6 to 7
            int hostReportedMaxHp = -1;
            harness.HostVitals.OnSharedMaxHealthChanged += newMax => hostReportedMaxHp = newMax;

            harness.ClientVitals.OnLocalMaxHealthUpgrade(7);
            harness.DeliverAll();

            Assert.Equal(7, harness.HostVitals.SharedMaxHealth);
            Assert.Equal(7, harness.ClientVitals.SharedMaxHealth);
            Assert.Equal(7, hostReportedMaxHp);
        }

        [Fact]
        public void F18_SharedNeedleUpgrade_ReplicatesMonotonically()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, 0, 0);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, 0, 0);

            int hostObservedNeedle = -1;
            harness.HostVitals.OnSharedNeedleUpgradeChanged += lvl => hostObservedNeedle = lvl;

            // Client upgrades needle at the forge (level 0 -> 1)
            harness.ClientVitals.OnLocalNeedleUpgrade(1);
            harness.DeliverAll();

            Assert.Equal(1, harness.ClientVitals.SharedNeedleUpgradeLevel);
            Assert.Equal(1, harness.HostVitals.SharedNeedleUpgradeLevel);
            Assert.Equal(1, hostObservedNeedle);

            // Host upgrades needle to level 2
            int clientObservedNeedle = -1;
            harness.ClientVitals.OnSharedNeedleUpgradeChanged += lvl => clientObservedNeedle = lvl;

            harness.HostVitals.OnLocalNeedleUpgrade(2);
            harness.DeliverAll();

            Assert.Equal(2, harness.HostVitals.SharedNeedleUpgradeLevel);
            Assert.Equal(2, harness.ClientVitals.SharedNeedleUpgradeLevel);
            Assert.Equal(2, clientObservedNeedle);
        }

        [Fact]
        public void F19_SharedMaxSilkUpgrade_ReplicatesMonotonically()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, 0, 0);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, 0, 0);

            int clientObservedSpools = -1;
            harness.ClientVitals.OnSharedMaxSilkChanged += spools => clientObservedSpools = spools;

            // Host completes 2nd silk spool (1 spool -> 2 spools)
            harness.HostVitals.OnLocalMaxSilkUpgrade(2);
            harness.DeliverAll();

            Assert.Equal(2, harness.HostVitals.SharedMaxSilkSpools);
            Assert.Equal(2, harness.ClientVitals.SharedMaxSilkSpools);
            Assert.Equal(2, clientObservedSpools);
        }

        [Fact]
        public void F17_F18_F19_MonotonicProgression_StaleOrLowerPacketIgnored()
        {
            using var harness = new DualClientTestHarness();
            harness.ClientVitals.InitializeState(7, 7, 18, 2, 2, 0, 0);

            bool maxHealthFired = false;
            bool maxSilkFired = false;
            bool needleFired = false;
            harness.ClientVitals.OnSharedMaxHealthChanged += _ => maxHealthFired = true;
            harness.ClientVitals.OnSharedMaxSilkChanged += _ => maxSilkFired = true;
            harness.ClientVitals.OnSharedNeedleUpgradeChanged += _ => needleFired = true;

            // Incoming stale packet with lower progression values
            var stalePacket = new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 1, 100),
                CurrentHealth = 5,
                CurrentSilk = 9,
                MaxHealth = 5,
                MaxSilkSpools = 1,
                NeedleUpgradeLevel = 0
            };

            harness.ClientVitals.HandleVitalsPacket(harness.HostTransport.LocalPeerId, stalePacket);

            // Verify Client state has NOT downgraded
            Assert.Equal(7, harness.ClientVitals.SharedMaxHealth);
            Assert.Equal(2, harness.ClientVitals.SharedMaxSilkSpools);
            Assert.Equal(2, harness.ClientVitals.SharedNeedleUpgradeLevel);

            Assert.False(maxHealthFired);
            Assert.False(maxSilkFired);
            Assert.False(needleFired);
        }

        [Fact]
        public void F17_F18_SimultaneousDisjointUpgrades_BothClientsConverge()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, 0, 0);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, 0, 0);

            // Host upgrades MaxHealth to 6, Client upgrades Needle to 1
            harness.HostVitals.OnLocalMaxHealthUpgrade(6);
            harness.ClientVitals.OnLocalNeedleUpgrade(1);

            harness.DeliverAll();

            harness.AssertSharedProgression(expectedMaxHp: 6, expectedMaxSilk: 1, expectedNeedleLevel: 1);
        }

        // =========================================================================
        // Category 3: Shared Economy & Deduplication (Features F20, F21)
        // =========================================================================

        [Fact]
        public void F20_SharedRosariesCollection_DeltaAccumulation()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, 100, 20);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, 100, 20);

            int clientReceivedDelta = 0;
            int clientReceivedTotal = 0;
            harness.ClientVitals.OnSharedCurrencyChanged += (type, delta, total) =>
            {
                if (type == CurrencyType.Rosaries)
                {
                    clientReceivedDelta = delta;
                    clientReceivedTotal = total;
                }
            };

            // Host picks up 45 Rosaries
            var tx1 = Guid.NewGuid();
            harness.HostVitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 45, 145, tx1);
            harness.DeliverAll();

            Assert.Equal(145, harness.HostVitals.SharedRosaries);
            Assert.Equal(145, harness.ClientVitals.SharedRosaries);
            Assert.Equal(45, clientReceivedDelta);
            Assert.Equal(145, clientReceivedTotal);

            // Client picks up 30 Rosaries
            var tx2 = Guid.NewGuid();
            harness.ClientVitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 30, 175, tx2);
            harness.DeliverAll();

            Assert.Equal(175, harness.HostVitals.SharedRosaries);
            Assert.Equal(175, harness.ClientVitals.SharedRosaries);

            harness.AssertSharedWallet(175, 20);
        }

        [Fact]
        public void F20_SharedShellShardsCollection_DeltaAccumulation()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, 50, 15);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, 50, 15);

            var tx = Guid.NewGuid();
            harness.ClientVitals.OnLocalCurrencyChanged(CurrencyType.ShellShards, 20, 35, tx);
            harness.DeliverAll();

            Assert.Equal(35, harness.HostVitals.SharedShellShards);
            Assert.Equal(35, harness.ClientVitals.SharedShellShards);
        }

        [Fact]
        public void F20_SharedCurrencySpending_NegativeDeltaDecrementsWallet()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, 200, 50);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, 200, 50);

            // Host spends 75 Rosaries on a map
            var txSpend1 = Guid.NewGuid();
            harness.HostVitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, -75, 125, txSpend1);
            harness.DeliverAll();

            Assert.Equal(125, harness.HostVitals.SharedRosaries);
            Assert.Equal(125, harness.ClientVitals.SharedRosaries);

            // Client spends 15 Shell Shards to replenish tools
            var txSpend2 = Guid.NewGuid();
            harness.ClientVitals.OnLocalCurrencyChanged(CurrencyType.ShellShards, -15, 35, txSpend2);
            harness.DeliverAll();

            Assert.Equal(35, harness.HostVitals.SharedShellShards);
            Assert.Equal(35, harness.ClientVitals.SharedShellShards);
        }

        [Fact]
        public void F20_WalletFloorClamping_SpendingExceedingBalance_ClampsAtZero()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, 40, 5);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, 40, 5);

            // Host spends 60 Rosaries (more than 40 balance)
            var tx = Guid.NewGuid();
            harness.HostVitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, -60, 0, tx);
            harness.DeliverAll();

            Assert.Equal(0, harness.HostVitals.SharedRosaries);
            Assert.Equal(0, harness.ClientVitals.SharedRosaries);
        }

        [Fact]
        public void F20_SimultaneousDualClientCurrencyPickups_CommutativeZeroDrift()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, 100, 10);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, 100, 10);

            var txHost = Guid.NewGuid();
            var txClient = Guid.NewGuid();

            // Host picks up 30, Client picks up 50 simultaneously
            harness.HostVitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 30, 130, txHost);
            harness.ClientVitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 50, 150, txClient);

            harness.DeliverAll();

            // Both converge to exactly 180 (100 + 30 + 50)
            Assert.Equal(180, harness.HostVitals.SharedRosaries);
            Assert.Equal(180, harness.ClientVitals.SharedRosaries);
        }

        [Fact]
        public void F21_TransactionDeduplication_ReplayPacketsDropped()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, 50, 0);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, 50, 0);

            var txId = Guid.NewGuid();
            harness.HostVitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 25, 75, txId);
            harness.DeliverAll();

            Assert.Equal(75, harness.ClientVitals.SharedRosaries);

            // Replay the exact same packet 5 times (simulated network replay/glitch)
            Assert.NotEmpty(harness.DeliveredCurrenciesToClient);
            var lastPacket = harness.DeliveredCurrenciesToClient[^1];

            for (int i = 0; i < 5; i++)
            {
                harness.ClientVitals.HandleCurrencyPacket(harness.HostTransport.LocalPeerId, lastPacket);
            }

            // Wallet balance must stay 75, NOT 75 + (5 * 25) = 200
            Assert.Equal(75, harness.ClientVitals.SharedRosaries);
        }

        [Fact]
        public void F21_TransactionDeduplication_EmptyGuid_Rejected()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, 50, 10);

            bool broadcastFired = false;
            service.OnOutgoingCurrencyBroadcast += _ => broadcastFired = true;

            // Empty Guid on local change is rejected
            service.OnLocalCurrencyChanged(CurrencyType.Rosaries, 25, 75, Guid.Empty);
            Assert.Equal(50, service.SharedRosaries);
            Assert.False(broadcastFired);

            // Empty Guid on remote packet is rejected
            var emptyGuidPacket = new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 1, 100),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 25,
                TotalWalletAmount = 75,
                TransactionId = Guid.Empty
            };

            service.HandleCurrencyPacket(new NetworkPeerId(2), emptyGuidPacket);
            Assert.Equal(50, service.SharedRosaries);
        }

        [Fact]
        public void F21_TransactionDeduplication_ZeroDelta_Ignored()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, 50, 10);

            bool broadcastFired = false;
            service.OnOutgoingCurrencyBroadcast += _ => broadcastFired = true;

            service.OnLocalCurrencyChanged(CurrencyType.Rosaries, 0, 50, Guid.NewGuid());
            Assert.Equal(50, service.SharedRosaries);
            Assert.False(broadcastFired);
        }

        [Fact]
        public void F21_TransactionDeduplication_FIFO500EvictionWindow()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, 0, 0);

            var guids = new List<Guid>();
            for (int i = 0; i < 500; i++)
            {
                var guid = Guid.NewGuid();
                guids.Add(guid);
                service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
                {
                    Header = new PacketHeader(PacketType.CurrencySync, 3, 1, (uint)i),
                    Currency = CurrencyType.Rosaries,
                    DeltaAmount = 1,
                    TotalWalletAmount = i + 1,
                    TransactionId = guid
                });
            }

            Assert.Equal(500, service.SharedRosaries);

            // Retransmitting transaction #500 is dropped (duplicate)
            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 1, 501),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 1,
                TotalWalletAmount = 501,
                TransactionId = guids[499]
            });
            Assert.Equal(500, service.SharedRosaries);

            // Now inject transaction #501, which causes transaction #1 (guids[0]) to be evicted from FIFO
            var guid501 = Guid.NewGuid();
            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 1, 502),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 1,
                TotalWalletAmount = 501,
                TransactionId = guid501
            });
            Assert.Equal(501, service.SharedRosaries);

            // Retransmitting evicted transaction #1 is now treated as new (FIFO eviction worked)
            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 1, 503),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 1,
                TotalWalletAmount = 502,
                TransactionId = guids[0]
            });
            Assert.Equal(502, service.SharedRosaries);
        }

        // =========================================================================
        // Category 4: Multi-Channel Concurrency & Network Stress
        // =========================================================================

        [Fact]
        public void MultiChannel_Channel2And3Segregation_UnderChannel1Loss()
        {
            // Simulate 50% packet loss on unreliable channels
            using var harness = new DualClientTestHarness(latencyMs: 10, jitterMs: 5, lossRate: 0.5f, seed: 100);
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, 100, 20);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, 100, 20);

            // Send multiple combat action triggers on Channel 1
            Span<byte> buf = stackalloc byte[64];
            for (int i = 0; i < 20; i++)
            {
                var action = new ActionTriggerPacket
                {
                    Header = new PacketHeader(PacketType.ActionTrigger, 1, (uint)i, (uint)i * 16),
                    Action = ActionType.SlashNormal,
                    ActionSequenceId = (uint)i
                };
                var writer = new PacketWriter(buf);
                action.Serialize(ref writer);
                harness.HostTransport.Send(harness.ClientTransport.LocalPeerId, buf.ToArray(), 0, writer.Position, DeliveryMethod.ReliableOrdered, 1);
            }

            // Concurrently send Vitals on Channel 2 and Currency on Channel 3
            harness.HostVitals.OnLocalMaxHealthUpgrade(6);
            harness.HostVitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 50, 150, Guid.NewGuid());

            harness.DeliverAll(150);

            // Both Channel 2 (Vitals) and Channel 3 (Currency) arrive reliably
            Assert.Equal(6, harness.ClientVitals.SharedMaxHealth);
            Assert.Equal(150, harness.ClientVitals.SharedRosaries);
        }

        [Fact]
        public void NetworkImpairments_SevereJitterAndLatency_OrderedCurrencySequence()
        {
            using var harness = new DualClientTestHarness(latencyMs: 50, jitterMs: 25, seed: 999);
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, 100, 0);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, 100, 0);

            // 5 rapid sequential currency deltas over Channel 3
            int[] deltas = { 10, 20, 15, -10, 5 };
            int expectedFinal = 100 + 10 + 20 + 15 - 10 + 5; // 140

            foreach (var d in deltas)
            {
                harness.HostVitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, d, harness.HostVitals.SharedRosaries + d, Guid.NewGuid());
            }

            harness.DeliverAll(200);

            Assert.Equal(expectedFinal, harness.HostVitals.SharedRosaries);
            Assert.Equal(expectedFinal, harness.ClientVitals.SharedRosaries);
        }

        [Fact]
        public void HostInitialStateHandshake_InitializesClientOnConnect()
        {
            using var harness = new DualClientTestHarness();

            // Host has existing game progression
            harness.HostVitals.InitializeState(initialHealth: 7, initialMaxHealth: 7, initialSilk: 18, initialMaxSilkSpools: 2, needleLevel: 3, initialRosaries: 450, initialShards: 60);

            // Client joins with 0 progression
            harness.ClientVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 0, initialRosaries: 0, initialShards: 0);

            // Host broadcasts current vitals and currency
            harness.HostVitals.BroadcastCurrentVitals();
            harness.HostVitals.BroadcastCurrentCurrency();

            harness.DeliverAll();

            // Client now shares Host's max progression and wallet
            Assert.Equal(7, harness.ClientVitals.SharedMaxHealth);
            Assert.Equal(2, harness.ClientVitals.SharedMaxSilkSpools);
            Assert.Equal(3, harness.ClientVitals.SharedNeedleUpgradeLevel);
            Assert.Equal(450, harness.ClientVitals.SharedRosaries);
            Assert.Equal(60, harness.ClientVitals.SharedShellShards);

            // But Client's local HP and Silk remain independent
            Assert.Equal(5, harness.ClientVitals.LocalHealth);
            Assert.Equal(9, harness.ClientVitals.LocalSilk);
        }

        [Fact]
        public void ZeroAllocationSerialization_VitalsAndCurrency_ZeroGcAlloc()
        {
            var vitals = new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 1, 100),
                CurrentHealth = 5,
                CurrentSilk = 9,
                MaxHealth = 7,
                MaxSilkSpools = 2,
                NeedleUpgradeLevel = 2
            };

            var currency = new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 1, 105),
                Currency = CurrencyType.Rosaries,
                TotalWalletAmount = 500,
                DeltaAmount = 25,
                PickupUniqueId = 999,
                TransactionId = Guid.NewGuid()
            };

            Span<byte> vBuf = stackalloc byte[64];
            Span<byte> cBuf = stackalloc byte[64];

            // Warmup JIT
            for (int i = 0; i < 100; i++)
            {
                var w1 = new PacketWriter(vBuf);
                vitals.Serialize(ref w1);
                var r1 = new PacketReader(vBuf);
                _ = VitalsSyncPacket.Deserialize(ref r1);

                var w2 = new PacketWriter(cBuf);
                currency.Serialize(ref w2);
                var r2 = new PacketReader(cBuf);
                _ = CurrencySyncPacket.Deserialize(ref r2);
            }

            long startAlloc = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < 1000; i++)
            {
                var w1 = new PacketWriter(vBuf);
                vitals.Serialize(ref w1);
                var r1 = new PacketReader(vBuf);
                _ = VitalsSyncPacket.Deserialize(ref r1);

                var w2 = new PacketWriter(cBuf);
                currency.Serialize(ref w2);
                var r2 = new PacketReader(cBuf);
                _ = CurrencySyncPacket.Deserialize(ref r2);
            }

            long totalAlloc = GC.GetAllocatedBytesForCurrentThread() - startAlloc;

            // Zero heap bytes allocated during serialization and deserialization
            Assert.Equal(0, totalAlloc);
        }
    }
}
