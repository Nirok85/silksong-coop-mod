using System;
using SilksongCoop.Core.Interpolation;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using SilksongCoop.Core.Visuals;
using SilksongCoop.Tests.TestHarness;
using Xunit;

namespace SilksongCoop.Tests.Tier3_PairwiseTests
{
    /// <summary>
    /// Tier 3: 8 Pairwise Cross-Feature Interaction Tests.
    /// Verifies interactions between combat, movement, vitals, death lifecycle, and inventory discovery.
    /// </summary>
    public class Tier3PairwiseTests
    {
        // -------------------------------------------------------------------------
        // Pairwise 1: Combat while moving (ActionTriggerPacket HarpoonDash + TransformSync)
        // -------------------------------------------------------------------------
        [Fact]
        public void P01_CombatWhileMoving_ActionTriggerDuringTransformReplication()
        {
            using var harness = new DualClientTestHarness();

            Span<byte> buf = stackalloc byte[64];

            // Stream continuous motion on Channel 0
            for (int i = 0; i < 5; i++)
            {
                var trans = new TransformSyncPacket
                {
                    Header = new PacketHeader(PacketType.TransformSync, 0, (uint)(i + 1), (uint)(100 + i * 33)),
                    PositionX = 10f + i * 2f,
                    PositionY = 5f,
                    VelocityX = 12f,
                    VelocityY = 0f,
                    FacingDirection = 1,
                    StateFlags = TransformSyncPacket.FlagGrounded
                };
                var w = new PacketWriter(buf);
                trans.Serialize(ref w);
                harness.HostTransport.Send(harness.ClientTransport.LocalPeerId, buf.ToArray(), 0, w.Position, DeliveryMethod.Unreliable, 0);
            }

            // Concurrently fire HarpoonDash action on Channel 1
            var action = new ActionTriggerPacket
            {
                Header = new PacketHeader(PacketType.ActionTrigger, 1, 1, 150),
                Action = ActionType.HarpoonDash,
                OriginX = 14f,
                OriginY = 5f,
                DirectionX = 1f,
                DirectionY = 0f
            };
            Span<byte> bufAct = stackalloc byte[64];
            var wAct = new PacketWriter(bufAct);
            action.Serialize(ref wAct);
            harness.HostTransport.Send(harness.ClientTransport.LocalPeerId, bufAct.ToArray(), 0, wAct.Position, DeliveryMethod.ReliableOrdered, 1);

            harness.DeliverAll();

            Assert.Equal(5, harness.DeliveredTransformsToClient.Count);
            Assert.Single(harness.DeliveredActionsToClient);
            Assert.Equal(ActionType.HarpoonDash, harness.DeliveredActionsToClient[0].Action);
        }

        // -------------------------------------------------------------------------
        // Pairwise 2: Damage during revival (Partner takes damage while reviving player countdown completes)
        // -------------------------------------------------------------------------
        [Fact]
        public void P02_DamageDuringRevival_PartnerTakesDamageWhileSpectatorRevives()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 0, initialShards: 0);
            harness.ClientVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 0, initialShards: 0);

            // Host dies and enters spectator mode
            harness.HostDeath.OnLocalPlayerDied(10f, 5f);
            Assert.Equal(DeathState.Spectating, harness.HostDeath.CurrentState);

            // Living client takes 2 damage during host's spectator countdown
            harness.ClientVitals.OnLocalDamageTaken(2, 3);
            harness.DeliverAll();

            // Advance spectator timer past 20s
            harness.HostDeath.UpdateTick(20.5f);
            Assert.Equal(DeathState.RespawningAtPartner, harness.HostDeath.CurrentState);

            // Client HP remains at 3 (alive, team wipe suppressed)
            Assert.Equal(3, harness.ClientVitals.LocalHealth);
        }

        // -------------------------------------------------------------------------
        // Pairwise 3: Discovery during spectator (Surviving partner discovers new crest while partner is dead)
        // -------------------------------------------------------------------------
        [Fact]
        public void P03_DiscoveryDuringSpectator_ItemDiscoveredWhilePartnerSpectating()
        {
            using var harness = new DualClientTestHarness();

            // Host enters spectator mode
            harness.HostDeath.OnLocalPlayerDied(10f, 5f);
            Assert.Equal(DeathState.Spectating, harness.HostDeath.CurrentState);

            // Surviving client discovers a new Crest
            harness.ClientInventory.OnLocalItemDiscovered("Crest_WeaverMistress", ItemCategory.Crest);
            harness.DeliverAll();

            // Both players possess the unlocked crest despite host being in spectator mode
            Assert.True(harness.ClientInventory.IsItemUnlocked("Crest_WeaverMistress"));
            Assert.True(harness.HostInventory.IsItemUnlocked("Crest_WeaverMistress"));
        }

        // -------------------------------------------------------------------------
        // Pairwise 4: Currency pickup during dash (Wallet increments during active dash trail)
        // -------------------------------------------------------------------------
        [Fact]
        public void P04_CurrencyPickupDuringDash_WalletIncrementsDuringCyanDashTrail()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 50, initialShards: 0);
            harness.ClientVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 50, initialShards: 0);

            var trail = new DashTrailLifecycle();
            trail.TriggerDash(isAirDash: false, isPlayer2: true);
            Assert.Equal(DashTrailPhase.Active, trail.CurrentPhase);

            // Host collects Rosaries while in active dash phase
            harness.HostVitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 30, 80, Guid.NewGuid());
            harness.DeliverAll();

            Assert.Equal(80, harness.HostVitals.SharedRosaries);
            Assert.Equal(80, harness.ClientVitals.SharedRosaries);
            Assert.Equal(DashTrailPhase.Active, trail.CurrentPhase);
        }

        // -------------------------------------------------------------------------
        // Pairwise 5: Death during inventory unlock (Simultaneous discovery and solo death)
        // -------------------------------------------------------------------------
        [Fact]
        public void P05_DeathDuringInventoryUnlock_ItemUnlockDeliveredConcurrentlyWithSoloDeath()
        {
            using var harness = new DualClientTestHarness();

            // Host discovers an item and takes lethal damage simultaneously
            harness.HostInventory.OnLocalItemDiscovered("Ability_SilkBomb", ItemCategory.Ability);
            bool suppress = harness.HostDeath.OnLocalPlayerDied(10f, 5f);
            harness.DeliverAll();

            Assert.True(suppress);
            Assert.Equal(DeathState.Spectating, harness.HostDeath.CurrentState);
            Assert.True(harness.ClientInventory.IsItemUnlocked("Ability_SilkBomb"));
        }

        // -------------------------------------------------------------------------
        // Pairwise 6: Max health upgrade at low health (Max HP increases from 5 to 6 without altering current HP)
        // -------------------------------------------------------------------------
        [Fact]
        public void P06_MaxHealthUpgradeAtLowHealth_IncreasesMaxWithoutAlteringCurrent()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(initialHealth: 1, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 0, initialShards: 0);
            harness.ClientVitals.InitializeState(initialHealth: 1, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 0, initialShards: 0);

            // Host acquires a mask upgrade (Max HP goes from 5 to 6)
            harness.HostVitals.OnLocalMaxHealthUpgrade(6);
            harness.DeliverAll();

            // Shared max HP is 6, but current local health remains strictly at 1
            Assert.Equal(6, harness.HostVitals.SharedMaxHealth);
            Assert.Equal(6, harness.ClientVitals.SharedMaxHealth);
            Assert.Equal(1, harness.HostVitals.LocalHealth);
            Assert.Equal(1, harness.ClientVitals.LocalHealth);
        }

        // -------------------------------------------------------------------------
        // Pairwise 7: Room transition snap during dash trail (Teleport distance > 2.5 units snaps position)
        // -------------------------------------------------------------------------
        [Fact]
        public void P07_RoomTransitionSnapDuringDashTrail_ResetsDashState()
        {
            var trail = new DashTrailLifecycle();
            trail.TriggerDash(isAirDash: false, isPlayer2: true);
            Assert.Equal(DashTrailPhase.Active, trail.CurrentPhase);

            // Teleport across room: current (10, 5), target (150, 20) -> distance > 2.5
            bool snap = DeadReckoning.ShouldSnap(10f, 5f, 150f, 20f);
            Assert.True(snap);

            // Landing on ground in new room and advancing cooldown resets dash trail
            trail.Update(0.26f, isGrounded: false, isWallSliding: false); // Enters cooldown
            trail.Update(0.01f, isGrounded: true, isWallSliding: false);  // Ground touch resets to Idle
            Assert.Equal(DashTrailPhase.Idle, trail.CurrentPhase);
        }

        // -------------------------------------------------------------------------
        // Pairwise 8: Simultaneous cross-action (Host uses Silk while Client takes damage)
        // -------------------------------------------------------------------------
        [Fact]
        public void P08_SimultaneousCrossAction_SilkSkillUsageWhilePartnerTakesDamage()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 0, initialShards: 0);
            harness.ClientVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 0, initialShards: 0);

            // Host uses 3 silk
            harness.HostVitals.OnLocalSilkUsed(3, 6);
            // Client takes 2 damage
            harness.ClientVitals.OnLocalDamageTaken(2, 3);
            harness.DeliverAll();

            // Host state: HP 5, Silk 6
            Assert.Equal(5, harness.HostVitals.LocalHealth);
            Assert.Equal(6, harness.HostVitals.LocalSilk);

            // Client state: HP 3, Silk 9
            Assert.Equal(3, harness.ClientVitals.LocalHealth);
            Assert.Equal(9, harness.ClientVitals.LocalSilk);
        }
    }
}
