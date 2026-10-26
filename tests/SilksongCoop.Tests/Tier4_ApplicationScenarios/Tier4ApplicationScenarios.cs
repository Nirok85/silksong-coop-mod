using System;
using SilksongCoop.Core.Interpolation;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.Revive;
using SilksongCoop.Core.State;
using SilksongCoop.Core.Visuals;
using SilksongCoop.Tests.TestHarness;
using Xunit;

namespace SilksongCoop.Tests.Tier4_ApplicationScenarios
{
    /// <summary>
    /// Tier 4: 5 Real-World Application Scenarios.
    /// Simulates realistic end-to-end cooperative gameplay scenarios under authentic session conditions.
    /// </summary>
    public class Tier4ApplicationScenarios
    {
        // -------------------------------------------------------------------------
        // Scenario 1: Dual-Client Boss Encounter Simulation
        // -------------------------------------------------------------------------
        [Fact]
        public void S01_DualClientBossEncounter_FullCombatAndRewardSimulation()
        {
            using var harness = new DualClientTestHarness();

            // Initial setup: Host has 5/5 HP, Client has 5/5 HP, both start with 0 currency, 9 silk
            harness.HostVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 0, initialShards: 0);
            harness.ClientVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 0, initialShards: 0);

            // Phase 1: Combat movement & visual palette differentiation
            // Player 1 (Red) moves to (20, 5), Player 2 (Azure Blue) dashes to (35, 5)
            var p1Trans = new TransformSyncPacket
            {
                Header = new PacketHeader(PacketType.TransformSync, 0, 1, 100),
                PositionX = 20f,
                PositionY = 5f,
                VelocityX = 8f,
                VelocityY = 0f,
                FacingDirection = 1,
                StateFlags = TransformSyncPacket.FlagGrounded
            };
            Span<byte> buf1 = stackalloc byte[64];
            var w1 = new PacketWriter(buf1);
            p1Trans.Serialize(ref w1);
            harness.HostTransport.Send(harness.ClientTransport.LocalPeerId, buf1.ToArray(), 0, w1.Position, DeliveryMethod.Unreliable, 0);

            // Verify Player 2 renders with azure blue cloak
            PaletteSwapMath.ApplyHueShift(0.85f, 0.15f, 0.15f, 0.58f, out float bR, out float bG, out float bB);
            Assert.True(bB > 0.70f);

            // Phase 2: Host takes 2 damage from boss attack; Client takes 0 damage
            harness.HostVitals.OnLocalDamageTaken(2, 3);
            harness.DeliverAll();

            Assert.Equal(3, harness.HostVitals.LocalHealth);
            Assert.Equal(5, harness.ClientVitals.LocalHealth); // Client gauge untouched

            // Phase 3: Host binds silk to heal 1 HP (uses 3 silk, recovers to 4 HP)
            harness.HostVitals.OnLocalSilkUsed(3, 6);
            harness.HostVitals.OnLocalDamageTaken(-1, 4); // Negative delta = healing
            harness.DeliverAll();

            Assert.Equal(4, harness.HostVitals.LocalHealth);
            Assert.Equal(6, harness.HostVitals.LocalSilk);
            Assert.Equal(5, harness.ClientVitals.LocalHealth);
            Assert.Equal(9, harness.ClientVitals.LocalSilk);

            // Phase 4: Boss defeated! Boss drops 150 Rosaries & 25 Shell Shards
            var txRosaries = Guid.NewGuid();
            var txShards = Guid.NewGuid();
            harness.HostVitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 150, 150, txRosaries);
            harness.ClientVitals.OnLocalCurrencyChanged(CurrencyType.ShellShards, 25, 25, txShards);
            harness.DeliverAll();

            harness.AssertSharedWallet(150, 25);
        }

        // -------------------------------------------------------------------------
        // Scenario 2: Mutual Wipe Bench Reload
        // -------------------------------------------------------------------------
        [Fact]
        public void S02_MutualWipeBenchReload_SimultaneousFatalEncounter()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(initialHealth: 1, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 200, initialShards: 50);
            harness.ClientVitals.InitializeState(initialHealth: 1, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 200, initialShards: 50);

            // Arena hazard strikes both players simultaneously
            harness.HostVitals.OnLocalDamageTaken(1, 0);
            harness.ClientVitals.OnLocalDamageTaken(1, 0);

            // Both players report death
            harness.HostDeath.OnLocalPlayerDied(10f, 5f);
            harness.ClientDeath.OnLocalPlayerDied(15f, 5f);
            harness.DeliverAll();

            // Simultaneous deaths trigger immediate TeamWipe state
            Assert.Equal(DeathState.TeamWipe, harness.HostDeath.CurrentState);
            Assert.Equal(DeathState.TeamWipe, harness.ClientDeath.CurrentState);

            // Simulate bench reload respawn completion
            harness.HostDeath.ResetAfterBenchRespawn();
            harness.ClientDeath.ResetAfterBenchRespawn();

            harness.HostVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 200, initialShards: 50);
            harness.ClientVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 200, initialShards: 50);

            Assert.Equal(DeathState.Alive, harness.HostDeath.CurrentState);
            Assert.Equal(DeathState.Alive, harness.ClientDeath.CurrentState);
            Assert.Equal(5, harness.HostVitals.LocalHealth);
            Assert.Equal(5, harness.ClientVitals.LocalHealth);
        }

        // -------------------------------------------------------------------------
        // Scenario 3: Room Transition Discovery & Loadout Isolation
        // -------------------------------------------------------------------------
        [Fact]
        public void S03_RoomTransitionDiscovery_LoadoutIsolationAndMirroring()
        {
            using var harness = new DualClientTestHarness();

            // Player 1 discovers new Crest and Tool in secret chamber
            harness.HostInventory.OnLocalItemDiscovered("Crest_Architect", ItemCategory.Crest);
            harness.HostInventory.OnLocalItemDiscovered("Tool_FleaBrew", ItemCategory.Tool);
            harness.DeliverAll();

            // Both players now possess the unlocked items
            Assert.True(harness.HostInventory.IsItemUnlocked("Crest_Architect"));
            Assert.True(harness.ClientInventory.IsItemUnlocked("Crest_Architect"));
            Assert.True(harness.HostInventory.IsItemUnlocked("Tool_FleaBrew"));
            Assert.True(harness.ClientInventory.IsItemUnlocked("Tool_FleaBrew"));

            // Player 1 equips Hunter Crest; Player 2 equips Architect Crest
            harness.HostInventory.SetLocalEquippedCrest("Crest_Hunter");
            harness.ClientInventory.SetLocalEquippedCrest("Crest_Architect");
            harness.DeliverAll();

            // Equipped loadouts remain strictly unique per player
            Assert.Equal("Crest_Hunter", harness.HostInventory.LocalEquippedCrest);
            Assert.Equal("Crest_Architect", harness.ClientInventory.LocalEquippedCrest);
            Assert.True(harness.HostInventory.IsEquippedLoadoutIsolated);
        }

        // -------------------------------------------------------------------------
        // Scenario 4: Solo Death, Spectator Camera, and Safe Ground Revival
        // -------------------------------------------------------------------------
        [Fact]
        public void S04_SoloDeathSpectatorAndSafeGroundRevival_Sequence()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(initialHealth: 5, initialMaxHealth: 6, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 0, initialShards: 0);
            harness.ClientVitals.InitializeState(initialHealth: 5, initialMaxHealth: 6, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 0, initialShards: 0);

            // Step 1: Host takes fatal damage
            harness.HostVitals.OnLocalDamageTaken(5, 0);
            bool suppressBench = harness.HostDeath.OnLocalPlayerDied(10f, 5f);
            harness.DeliverAll();

            Assert.True(suppressBench);
            Assert.Equal(DeathState.Spectating, harness.HostDeath.CurrentState);

            // Step 2: Client moves along terrain while host spectates
            var clientTrans = new TransformSyncPacket
            {
                Header = new PacketHeader(PacketType.TransformSync, 0, 1, 500),
                PositionX = 45f,
                PositionY = 10f,
                VelocityX = 5f,
                VelocityY = 0f,
                FacingDirection = 1,
                StateFlags = TransformSyncPacket.FlagGrounded
            };
            Span<byte> buf = stackalloc byte[64];
            var w = new PacketWriter(buf);
            clientTrans.Serialize(ref w);
            harness.ClientTransport.Send(harness.HostTransport.LocalPeerId, buf.ToArray(), 0, w.Position, DeliveryMethod.Unreliable, 0);
            harness.DeliverAll();

            // Step 3: Spectator countdown reaches 20.0s
            harness.HostDeath.UpdateTick(20.5f);
            Assert.Equal(DeathState.RespawningAtPartner, harness.HostDeath.CurrentState);

            // Step 4: Calculate revive health (50% of 6 = 3 HP)
            int reviveHp = SafeGroundSolver.CalculateReviveHealth(harness.HostVitals.SharedMaxHealth);
            Assert.Equal(3, reviveHp);

            // Step 5: Reset host to alive at revived coordinates
            harness.HostDeath.ResetAfterBenchRespawn(); // Returns to alive state
            harness.HostVitals.OnLocalDamageTaken(-3, 3); // Restores 3 HP

            Assert.Equal(DeathState.Alive, harness.HostDeath.CurrentState);
            Assert.Equal(3, harness.HostVitals.LocalHealth);
        }

        // -------------------------------------------------------------------------
        // Scenario 5: High-Latency Network Recovery (120ms RTT, Jitter & 10% Drop)
        // -------------------------------------------------------------------------
        [Fact]
        public void S05_HighLatencyNetworkRecovery_JitterAndPacketLossConvergence()
        {
            // Configure harness with artificial network degradation
            using var harness = new DualClientTestHarness(latencyMs: 60, jitterMs: 15, lossRate: 0.10f, seed: 12345);
            harness.HostVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 100, initialShards: 10);
            harness.ClientVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 100, initialShards: 10);

            // Send multiple reliable currency & discovery packets
            for (int i = 0; i < 5; i++)
            {
                harness.HostVitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 10, 100 + (i + 1) * 10, Guid.NewGuid());
                harness.ClientInventory.OnLocalItemDiscovered($"Tool_Test_{i}", ItemCategory.Tool);
            }

            // Deliver all over virtual time
            harness.DeliverAll(maxSteps: 200);

            // Despite 10% loss and 60ms one-way latency, all reliable items converge
            Assert.Equal(150, harness.HostVitals.SharedRosaries);
            Assert.Equal(150, harness.ClientVitals.SharedRosaries);

            for (int i = 0; i < 5; i++)
            {
                Assert.True(harness.HostInventory.IsItemUnlocked($"Tool_Test_{i}"));
                Assert.True(harness.ClientInventory.IsItemUnlocked($"Tool_Test_{i}"));
            }
        }
    }
}
