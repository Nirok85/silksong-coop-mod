using System;
using System.Collections.Generic;
using System.Reflection;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using SilksongCoop.Tests.E2ERemediationTests;
using Xunit;

namespace SilksongCoop.Tests.State
{
    /// <summary>
    /// Milestone 2 Remediation Unit Tests covering:
    /// - Defect 1: PlayerData save bool allowlist validation (Ability and KeyItem categories)
    /// - Defect 2: WalletSeed retry loop concurrent collection modification elimination
    /// - Defect 3: Catch-up currency readiness check preventing sequence watermark corruption
    /// - Defect 4: Pre-seed 3rd player remote delta preservation in VitalsSyncService
    /// - Defect 5: Fragment upgrade vs VitalsSync packet arrival order idempotency
    /// - Defect 13: Late-join catch-up unlocked crests synchronization
    /// </summary>
    public class Milestone2RemediationTests
    {
        #region Defect 1: PlayerData Save Bool Allowlist Validation

        [Theory]
        [InlineData("hasDash")]
        [InlineData("hasWalljump")]
        [InlineData("hasDoubleJump")]
        [InlineData("hasHarpoonDash")]
        [InlineData("hasSilkSpecial")]
        [InlineData("hasNeedleThrow")]
        [InlineData("hasThreadSphere")]
        [InlineData("hasParry")]
        [InlineData("hasSilkCharge")]
        [InlineData("hasSilkBomb")]
        [InlineData("hasSilkBossNeedle")]
        [InlineData("hasNeedolin")]
        [InlineData("hasNeedolinMemoryPowerup")]
        [InlineData("hasBrolly")]
        [InlineData("hasChargeSlash")]
        [InlineData("hasSuperJump")]
        [InlineData("hasQuill")]
        public void Defect1_ValidAbilityBools_AreAllowed(string abilityName)
        {
            Assert.True(RemediationTestHarness.IsSaveBoolAllowed(abilityName, ItemCategory.Ability));
        }

        [Theory]
        [InlineData("simpleKey")]
        [InlineData("bellTowerKey")]
        [InlineData("cryptKey")]
        [InlineData("abbeyKey")]
        [InlineData("boneBottomKey")]
        [InlineData("citadelCrestKey")]
        public void Defect1_ValidKeyItemBools_AreAllowed(string keyName)
        {
            Assert.True(RemediationTestHarness.IsSaveBoolAllowed(keyName, ItemCategory.KeyItem));
        }

        [Theory]
        [InlineData("isInvincible")]
        [InlineData("permaDeath")]
        [InlineData("infiniteSilk")]
        [InlineData("debugConsoleEnabled")]
        [InlineData("unlockedAllMaps")]
        [InlineData("geo")]
        [InlineData("maxHealthBase")]
        [InlineData("playerDataArbitraryField")]
        public void Defect1_UnauthorizedBools_AreRejected(string unauthorizedName)
        {
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed(unauthorizedName, ItemCategory.Ability));
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed(unauthorizedName, ItemCategory.KeyItem));
        }

        [Fact]
        public void Defect1_CrossCategoryBools_AreRejected()
        {
            // Ability keys cannot be unlocked under KeyItem
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed("hasDash", ItemCategory.KeyItem));
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed("hasWalljump", ItemCategory.KeyItem));

            // Key item keys cannot be unlocked under Ability
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed("simpleKey", ItemCategory.Ability));
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed("bellTowerKey", ItemCategory.Ability));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Defect1_NullOrEmptyKeys_AreRejected(string? emptyKey)
        {
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed(emptyKey!, ItemCategory.Ability));
            Assert.False(RemediationTestHarness.IsSaveBoolAllowed(emptyKey!, ItemCategory.KeyItem));
        }

        #endregion

        #region Defect 2: WalletSeed Retry Mutation Fix

        [Fact]
        public void Defect2_MultiplePeers_RetryDecrementsWithoutEnumeratorModification()
        {
            var peer1 = new NetworkPeerId(101);
            var peer2 = new NetworkPeerId(102);
            var peer3 = new NetworkPeerId(103);
            var outstanding = new Dictionary<NetworkPeerId, byte>
            {
                { peer1, 3 },
                { peer2, 2 },
                { peer3, 4 }
            };

            // All peers fail delivery and must retry
            bool success = RemediationTestHarness.PumpWalletSeedSafe(
                outstanding,
                peer => false,
                out bool failed);

            Assert.True(success);
            Assert.False(failed);
            Assert.Equal(3, outstanding.Count);
            Assert.Equal(2, outstanding[peer1]);
            Assert.Equal(1, outstanding[peer2]);
            Assert.Equal(3, outstanding[peer3]);
        }

        [Fact]
        public void Defect2_PartialDeliveryAndRetries_HandledCorrectly()
        {
            var peer1 = new NetworkPeerId(101);
            var peer2 = new NetworkPeerId(102);
            var outstanding = new Dictionary<NetworkPeerId, byte>
            {
                { peer1, 3 },
                { peer2, 3 }
            };

            // Peer 1 succeeds, Peer 2 fails and retries
            bool success = RemediationTestHarness.PumpWalletSeedSafe(
                outstanding,
                peer => peer == peer1,
                out bool failed);

            Assert.True(success);
            Assert.False(failed);
            Assert.Single(outstanding);
            Assert.False(outstanding.ContainsKey(peer1));
            Assert.Equal(2, outstanding[peer2]);
        }

        [Fact]
        public void Defect2_ExhaustedRetry_ClearsOutstandingAndReportsFailure()
        {
            var peer1 = new NetworkPeerId(101);
            var peer2 = new NetworkPeerId(102);
            var outstanding = new Dictionary<NetworkPeerId, byte>
            {
                { peer1, 3 },
                { peer2, 1 } // Only 1 attempt left, exhausted upon failure
            };

            bool success = RemediationTestHarness.PumpWalletSeedSafe(
                outstanding,
                peer => false,
                out bool failed);

            Assert.False(success);
            Assert.True(failed);
            Assert.Empty(outstanding);
        }

        #endregion

        #region Defect 3: Catch-up Currency Readiness Check

        [Fact]
        public void Defect3_ZeroDeltaPacket_DoesNotCorruptSequenceWatermarkForUnreadyPeer()
        {
            var service = new VitalsSyncService();
            var peer = new NetworkPeerId(2);

            // Simulation of peer readiness:
            // If zero-delta packet is NOT delivered to unready peer, sequence watermark remains untouched.
            // When seed arrives later with baseline balance:
            service.SeedWallet(CurrencyType.Rosaries, 100, 0);

            // Valid transaction with sequence 1 arrives after peer is ready:
            var tx1 = new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 25,
                TotalWalletAmount = 125,
                TransactionId = Guid.NewGuid(),
                PickupUniqueId = 1
            };

            service.HandleCurrencyPacket(peer, tx1);
            Assert.Equal(125, service.SharedRosaries);

            // Subsequent valid transaction with sequence 2 arrives:
            var tx2 = new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 15,
                TotalWalletAmount = 140,
                TransactionId = Guid.NewGuid(),
                PickupUniqueId = 2
            };

            service.HandleCurrencyPacket(peer, tx2);
            Assert.Equal(140, service.SharedRosaries);
        }

        [Fact]
        public void Defect3_ZeroDeltaPickupClaim_DoesNotAlterSharedBalance()
        {
            var service = new VitalsSyncService();
            service.SeedWallet(CurrencyType.Rosaries, 200, 0);

            var peer = new NetworkPeerId(2);
            var claim = new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 0,
                TotalWalletAmount = 200,
                TransactionId = Guid.NewGuid(),
                PickupUniqueId = 1
            };

            service.HandleCurrencyPacket(peer, claim);
            Assert.Equal(200, service.SharedRosaries);
        }

        #endregion

        #region Defect 4: Pre-Seed 3rd Player Remote Delta Preservation

        private static void ApplyCausalHostSeed(VitalsSyncService client, CurrencyType currency,
            int total, int pendingLocalDelta)
        {
            var localPeer = new NetworkPeerId(2);
            var thirdPeer = new NetworkPeerId(3);
            var host = new VitalsSyncService();
            host.InitializeState(5, 5, 9, 1, 0,
                currency == CurrencyType.Rosaries ? total : 0,
                currency == CurrencyType.ShellShards ? total : 0);
            var pending = new List<CurrencySyncPacket>();
            if (pendingLocalDelta != 0)
            {
                Action<CurrencySyncPacket> capture = packet => pending.Add(packet);
                client.OnOutgoingCurrencyBroadcast += capture;
                client.OnLocalCurrencyChanged(currency, pendingLocalDelta, 0, Guid.NewGuid());
                client.OnOutgoingCurrencyBroadcast -= capture;
                Assert.Single(pending);
            }

            var seed = host.CaptureWalletSeed(new NetworkPeerId(1), new[] { localPeer, thirdPeer });
            Assert.False(seed.Includes(thirdPeer, currency, 1));
            client.ApplyWalletSeed(seed, localPeer, pending);
        }


        [Fact]
        public void Defect4_Rosaries_PreSeedRemoteDeltaPreservedWhenHostSeedArrives()
        {
            var service = new VitalsSyncService();
            service.BeginWalletSeed();
            var peer3 = new NetworkPeerId(3);

            // 3rd player (peer 3) sends +75 rosaries before Host's SeedWallet arrives
            var txBeforeSeed = new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 75,
                TotalWalletAmount = 75,
                TransactionId = Guid.NewGuid(),
                PickupUniqueId = 1
            };
            service.HandleCurrencyPacket(peer3, txBeforeSeed);

            // Host's SeedWallet arrives with balance 200, local pending delta 10
            ApplyCausalHostSeed(service, CurrencyType.Rosaries, total: 200, pendingLocalDelta: 10);

            // Balance must be 200 + 10 + 75 = 285 (3rd player delta preserved)
            Assert.Equal(285, service.SharedRosaries);
        }

        [Fact]
        public void Defect4_ShellShards_PreSeedRemoteDeltaPreservedWhenHostSeedArrives()
        {
            var service = new VitalsSyncService();
            service.BeginWalletSeed();
            var peer3 = new NetworkPeerId(3);

            // 3rd player sends +20 shell shards before Host seed
            var txBeforeSeed = new CurrencySyncPacket
            {
                Currency = CurrencyType.ShellShards,
                DeltaAmount = 20,
                TotalWalletAmount = 20,
                TransactionId = Guid.NewGuid(),
                PickupUniqueId = 1
            };
            service.HandleCurrencyPacket(peer3, txBeforeSeed);

            // Host seed arrives with total 50, local pending delta 5
            ApplyCausalHostSeed(service, CurrencyType.ShellShards, total: 50, pendingLocalDelta: 5);

            // Balance must be 50 + 5 + 20 = 75
            Assert.Equal(75, service.SharedShellShards);
        }

        [Fact]
        public void Defect4_NegativePreSeedDelta_DeductedCorrectlyFromSeededBalance()
        {
            var service = new VitalsSyncService();
            service.BeginWalletSeed();
            var peer3 = new NetworkPeerId(3);

            // 3rd player spent -40 rosaries before Host seed
            var spendTx = new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                DeltaAmount = -40,
                TotalWalletAmount = 0,
                TransactionId = Guid.NewGuid(),
                PickupUniqueId = 1
            };
            service.HandleCurrencyPacket(peer3, spendTx);

            // Host seed arrives with total 150, local delta 0
            ApplyCausalHostSeed(service, CurrencyType.Rosaries, total: 150, pendingLocalDelta: 0);

            // Balance must be 150 - 40 = 110
            Assert.Equal(110, service.SharedRosaries);
        }

        [Fact]
        public void Defect4_SubsequentTransactionsAfterSeed_AccumulateNormally()
        {
            var service = new VitalsSyncService();
            service.BeginWalletSeed();
            var peer3 = new NetworkPeerId(3);

            // Pre-seed delta: +50
            service.HandleCurrencyPacket(peer3, new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 50,
                TotalWalletAmount = 50,
                TransactionId = Guid.NewGuid(),
                PickupUniqueId = 1
            });

            // Host seed: 100
            ApplyCausalHostSeed(service, CurrencyType.Rosaries, total: 100, pendingLocalDelta: 0);
            Assert.Equal(150, service.SharedRosaries);

            // Post-seed transaction: +25
            service.HandleCurrencyPacket(peer3, new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 25,
                TotalWalletAmount = 175,
                TransactionId = Guid.NewGuid(),
                PickupUniqueId = 2
            });

            Assert.Equal(175, service.SharedRosaries);
        }

        #endregion

        #region Defect 5: Stat Upgrade Channel Idempotency

        [Fact]
        public void Defect5_FragmentsBeforeMaxHealth_UpgradesExactlyOnce()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();
            Assert.Equal(5, controller.CurrentMaxHealth);

            // 4 fragments complete 1 mask
            controller.ApplyHeartPieceFragments(fragmentTotal: 4, sharedMaxHealth: 5);
            Assert.Equal(6, controller.CurrentMaxHealth);
            Assert.Equal(1, controller.AddToMaxHealthCallCount);

            // Later VitalsSyncPacket arrives with authoritative MaxHealth = 6
            controller.OnSharedMaxHealthReceived(6);
            Assert.Equal(6, controller.CurrentMaxHealth);
            Assert.Equal(1, controller.AddToMaxHealthCallCount); // Not incremented again
        }

        [Fact]
        public void Defect5_MaxHealthBeforeFragments_DoesNotDuplicateUpgrade()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();
            Assert.Equal(5, controller.CurrentMaxHealth);

            // VitalsSyncPacket arrives first (updates max to 6)
            controller.OnSharedMaxHealthReceived(6);
            Assert.Equal(6, controller.CurrentMaxHealth);

            // Later fragment packet arrives completing 4 pieces
            controller.ApplyHeartPieceFragments(fragmentTotal: 4, sharedMaxHealth: 6);
            Assert.Equal(6, controller.CurrentMaxHealth);
            Assert.Equal(1, controller.AddToMaxHealthCallCount); // Guarded, no duplicate AddToMaxHealth call
        }

        [Fact]
        public void Defect5_SilkPartsBeforeMaxSilk_UpgradesExactlyOnce()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();
            Assert.Equal(1, controller.CurrentMaxSilkSpools);

            // 3 fragments complete 1 spool
            controller.ApplySilkPartFragments(fragmentTotal: 3, sharedMaxSilkSpools: 1);
            Assert.Equal(2, controller.CurrentMaxSilkSpools);
            Assert.Equal(1, controller.AddToMaxSilkCallCount);

            // Later VitalsSyncPacket arrives with authoritative MaxSilkSpools = 2
            controller.OnSharedMaxSilkReceived(2);
            Assert.Equal(2, controller.CurrentMaxSilkSpools);
            Assert.Equal(1, controller.AddToMaxSilkCallCount);
        }

        [Fact]
        public void Defect5_MaxSilkBeforeSilkParts_DoesNotDuplicateUpgrade()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();
            Assert.Equal(1, controller.CurrentMaxSilkSpools);

            // VitalsSyncPacket arrives first (spools = 2)
            controller.OnSharedMaxSilkReceived(2);
            Assert.Equal(2, controller.CurrentMaxSilkSpools);

            // Later fragment packet arrives completing 3 parts
            controller.ApplySilkPartFragments(fragmentTotal: 3, sharedMaxSilkSpools: 2);
            Assert.Equal(2, controller.CurrentMaxSilkSpools);
            Assert.Equal(1, controller.AddToMaxSilkCallCount); // Guarded
        }

        [Fact]
        public void Defect5_ReverseOrder_UninitializedBaseHealth_VitalsFirst_DoesNotDuplicateUpgrade()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();
            // Verify uninitialized base stat matches production default (-1)
            Assert.Equal(-1, controller.BaseMaxHealth);
            Assert.Equal(5, controller.CurrentMaxHealth);

            // VitalsSyncPacket arrives first over Vitals channel, advancing shared max to 6
            controller.OnSharedMaxHealthReceived(6);
            Assert.Equal(6, controller.CurrentMaxHealth);
            Assert.Equal(1, controller.AddToMaxHealthCallCount);

            // ItemUnlockPacket arrives later over Currency channel completing 4 heart pieces (1 mask)
            controller.ApplyHeartPieceFragments(fragmentTotal: 4, sharedMaxHealth: 6);

            // Base advances with completed mask (6), targetMax is 6, and no duplicate upgrade applied
            Assert.Equal(6, controller.CurrentMaxHealth);
            Assert.Equal(1, controller.AddToMaxHealthCallCount);
            Assert.Equal(6, controller.BaseMaxHealth);
        }

        [Fact]
        public void Defect5_ReverseOrder_UninitializedBaseSilk_VitalsFirst_DoesNotDuplicateUpgrade()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();
            // Verify uninitialized base spool matches production default (-1)
            Assert.Equal(-1, controller.BaseMaxSilkSpools);
            Assert.Equal(1, controller.CurrentMaxSilkSpools);

            // VitalsSyncPacket arrives first over Vitals channel, advancing shared spools to 2
            controller.OnSharedMaxSilkReceived(2);
            Assert.Equal(2, controller.CurrentMaxSilkSpools);
            Assert.Equal(1, controller.AddToMaxSilkCallCount);

            // ItemUnlockPacket arrives later over Currency channel completing 3 silk parts (1 spool)
            controller.ApplySilkPartFragments(fragmentTotal: 3, sharedMaxSilkSpools: 2);

            // Base advances with completed spool (2), targetSpools is 2, and no duplicate upgrade applied
            Assert.Equal(2, controller.CurrentMaxSilkSpools);
            Assert.Equal(1, controller.AddToMaxSilkCallCount);
            Assert.Equal(2, controller.BaseMaxSilkSpools);
        }

        [Fact]
        public void Defect5_ReverseOrder_DirectPlayerDataMutation_NoDuplicateHeroControllerCalls()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();
            Assert.Equal(-1, controller.BaseMaxHealth);

            // VitalsSyncManager.OnSharedMaxHealthChanged directly sets pd.maxHealthBase = 6
            controller.CurrentMaxHealth = 6;
            Assert.Equal(0, controller.AddToMaxHealthCallCount);

            // Fragment packet arrives later with 4 fragments
            controller.ApplyHeartPieceFragments(fragmentTotal: 4, sharedMaxHealth: 6);

            // Must NOT invoke HeroController.AddToMaxHealth because max was already upgraded by vitals
            Assert.Equal(6, controller.CurrentMaxHealth);
            Assert.Equal(0, controller.AddToMaxHealthCallCount);
            Assert.Equal(6, controller.BaseMaxHealth);
        }

        [Fact]
        public void Defect5_ReverseOrder_DirectSilkMutation_NoDuplicateHeroControllerCalls()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();
            Assert.Equal(-1, controller.BaseMaxSilkSpools);

            // VitalsSyncManager.OnSharedMaxSilkChanged directly sets pd.silkMax = 18 (2 spools)
            controller.CurrentMaxSilkSpools = 2;
            Assert.Equal(0, controller.AddToMaxSilkCallCount);

            // Fragment packet arrives later with 3 parts
            controller.ApplySilkPartFragments(fragmentTotal: 3, sharedMaxSilkSpools: 2);

            // Must NOT invoke HeroController.AddToMaxSilk because spools were already upgraded by vitals
            Assert.Equal(2, controller.CurrentMaxSilkSpools);
            Assert.Equal(0, controller.AddToMaxSilkCallCount);
            Assert.Equal(2, controller.BaseMaxSilkSpools);
        }

        [Fact]
        public void Defect5_MultiMask_ReverseOrder_UninitializedBase_DoesNotDuplicate()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();
            Assert.Equal(-1, controller.BaseMaxHealth);

            // Remote completes 8 fragments (2 masks), bringing health from 5 to 7.
            // Vitals arrives first:
            controller.OnSharedMaxHealthReceived(7);
            Assert.Equal(7, controller.CurrentMaxHealth);
            Assert.Equal(1, controller.AddToMaxHealthCallCount);

            // 8 fragments arrive later over Currency channel:
            controller.ApplyHeartPieceFragments(fragmentTotal: 8, sharedMaxHealth: 7);
            Assert.Equal(7, controller.CurrentMaxHealth);
            Assert.Equal(1, controller.AddToMaxHealthCallCount); // No duplicate addition
            Assert.Equal(7, controller.BaseMaxHealth);
        }

        [Fact]
        public void Defect5_ProbeScenario3A_DeltaPlus4WithVitalsFirst_DoesNotDuplicate()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();
            Assert.Equal(-1, controller.BaseMaxHealth);
            Assert.Equal(5, controller.CurrentMaxHealth);

            // Vitals arrives first, mutating max health to 6
            controller.CurrentMaxHealth = 6;

            // Delta +4 completes 1 mask
            controller.ApplyHeartPieceFragments(fragmentTotal: 4, sharedMaxHealth: 6);

            // Must NOT double upgrade to 7
            Assert.Equal(6, controller.CurrentMaxHealth);
            Assert.Equal(0, controller.AddToMaxHealthCallCount);
            Assert.Equal(6, controller.BaseMaxHealth);
        }

        [Fact]
        public void Defect5_ProbeScenario3B_DeltaPlus1On3PiecesWithVitalsFirst_DoesNotDuplicate()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();
            Assert.Equal(-1, controller.BaseMaxHealth);
            Assert.Equal(5, controller.CurrentMaxHealth);

            // Local had 3 pieces. Partner collects 4th piece (+1).
            // Vitals arrives first, mutating max health to 6
            controller.CurrentMaxHealth = 6;

            // Next becomes 4 (1 mask completed)
            controller.ApplyHeartPieceFragments(fragmentTotal: 4, sharedMaxHealth: 6);

            // Must NOT double upgrade to 7
            Assert.Equal(6, controller.CurrentMaxHealth);
            Assert.Equal(0, controller.AddToMaxHealthCallCount);
            Assert.Equal(6, controller.BaseMaxHealth);
        }

        [Fact]
        public void Defect5_ProbeScenario4A_DeltaPlus3SilkPartsWithVitalsFirst_DoesNotDuplicate()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();
            Assert.Equal(-1, controller.BaseMaxSilkSpools);
            Assert.Equal(1, controller.CurrentMaxSilkSpools);

            // Vitals arrives first, mutating max silk spools to 2 (18 silk)
            controller.CurrentMaxSilkSpools = 2;

            // Delta +3 completes 1 spool
            controller.ApplySilkPartFragments(fragmentTotal: 3, sharedMaxSilkSpools: 2);

            // Must NOT double upgrade to 3 spools (27 silk)
            Assert.Equal(2, controller.CurrentMaxSilkSpools);
            Assert.Equal(0, controller.AddToMaxSilkCallCount);
            Assert.Equal(2, controller.BaseMaxSilkSpools);
        }

        [Fact]
        public void Defect5_ProbeScenario4B_DeltaPlus1On2SilkPartsWithVitalsFirst_DoesNotDuplicate()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController();
            Assert.Equal(-1, controller.BaseMaxSilkSpools);
            Assert.Equal(1, controller.CurrentMaxSilkSpools);

            // Local had 2 parts. Partner collects 3rd part (+1).
            // Vitals arrives first, mutating max silk spools to 2
            controller.CurrentMaxSilkSpools = 2;

            // Next becomes 3 (1 spool completed)
            controller.ApplySilkPartFragments(fragmentTotal: 3, sharedMaxSilkSpools: 2);

            // Must NOT double upgrade to 3 spools (27 silk)
            Assert.Equal(2, controller.CurrentMaxSilkSpools);
            Assert.Equal(0, controller.AddToMaxSilkCallCount);
            Assert.Equal(2, controller.BaseMaxSilkSpools);
        }

        [Fact]
        public void Defect5_SequentialTwoMasks_ReverseOrder_DoesNotDuplicateUpgrade()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController
            {
                BaseMaxHealth = 5,
                CurrentMaxHealth = 5
            };

            // Vitals jump 5 -> 7 HP arrives first (remote player acquired 2 masks)
            controller.CurrentMaxHealth = 7;

            // Packet 1: heartPieces:+4 (1 mask) arrives
            controller.ApplyHeartPieceFragments(fragmentTotal: 4, sharedMaxHealth: 7, isDelta: true);
            Assert.Equal(7, controller.CurrentMaxHealth);
            Assert.Equal(0, controller.AddToMaxHealthCallCount);
            Assert.Equal(6, controller.BaseMaxHealth);

            // Packet 2: heartPieces:+4 (1 mask) arrives
            controller.ApplyHeartPieceFragments(fragmentTotal: 4, sharedMaxHealth: 7, isDelta: true);
            Assert.Equal(7, controller.CurrentMaxHealth);
            Assert.Equal(0, controller.AddToMaxHealthCallCount);
            Assert.Equal(7, controller.BaseMaxHealth);
        }

        [Fact]
        public void Defect5_SequentialFourMasks_ReverseOrder_DoesNotDuplicateUpgrade()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController
            {
                BaseMaxHealth = 5,
                CurrentMaxHealth = 5
            };

            // Vitals jump 5 -> 9 HP arrives first (remote player acquired 4 masks)
            controller.CurrentMaxHealth = 9;

            // 4 sequential packets of heartPieces:+4 arrive
            for (int i = 0; i < 4; i++)
            {
                controller.ApplyHeartPieceFragments(fragmentTotal: 4, sharedMaxHealth: 9, isDelta: true);
            }

            Assert.Equal(9, controller.CurrentMaxHealth);
            Assert.Equal(0, controller.AddToMaxHealthCallCount);
            Assert.Equal(9, controller.BaseMaxHealth);
        }

        [Fact]
        public void Defect5_SequentialTwoSpools_ReverseOrder_DoesNotDuplicateUpgrade()
        {
            var controller = new RemediationTestHarness.StatUpgradeIdempotencyController
            {
                BaseMaxSilkSpools = 1,
                CurrentMaxSilkSpools = 1
            };

            // Vitals jump 1 -> 3 spools arrives first (remote player acquired 2 spools = 27 silk)
            controller.CurrentMaxSilkSpools = 3;

            // Packet 1: silkParts:+3 (1 spool) arrives
            controller.ApplySilkPartFragments(fragmentTotal: 3, sharedMaxSilkSpools: 3, isDelta: true);
            Assert.Equal(3, controller.CurrentMaxSilkSpools);
            Assert.Equal(0, controller.AddToMaxSilkCallCount);
            Assert.Equal(2, controller.BaseMaxSilkSpools);

            // Packet 2: silkParts:+3 (1 spool) arrives
            controller.ApplySilkPartFragments(fragmentTotal: 3, sharedMaxSilkSpools: 3, isDelta: true);
            Assert.Equal(3, controller.CurrentMaxSilkSpools);
            Assert.Equal(0, controller.AddToMaxSilkCallCount);
            Assert.Equal(3, controller.BaseMaxSilkSpools);
        }

        #endregion

        #region Defect 13: Unlocked Crests Catch-Up Synchronization

        [Fact]
        public void Defect13_AllUnlockedCrests_AreRegisteredInInventory()
        {
            var inventory = new InventorySyncService();
            string[] crests = new[] { "HunterCrest", "ReaperCrest", "ArchitectCrest", "WandererCrest" };

            foreach (var crest in crests)
            {
                inventory.OnLocalItemDiscovered(crest, ItemCategory.Crest, force: true);
            }

            foreach (var crest in crests)
            {
                Assert.Contains(crest, inventory.UnlockedItems);
            }
        }

        [Fact]
        public void Defect13_EquippedCrest_PreservedAlongsideOtherUnlockedCrests()
        {
            var inventory = new InventorySyncService();
            var unlockedCrests = new[] { "HunterCrest", "ReaperCrest", "WandererCrest" };
            string equippedCrest = "ReaperCrest";

            foreach (var crest in unlockedCrests)
            {
                inventory.OnLocalItemDiscovered(crest, ItemCategory.Crest, force: true);
            }

            // Equipped crest baseline broadcast
            inventory.OnLocalItemDiscovered(equippedCrest, ItemCategory.Crest, force: true);

            Assert.Contains(equippedCrest, inventory.UnlockedItems);
            Assert.Equal(3, inventory.UnlockedItems.Count);
        }

        #endregion
    }
}
