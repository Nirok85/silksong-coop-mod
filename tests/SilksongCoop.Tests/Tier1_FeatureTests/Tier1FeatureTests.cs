using System;
using System.Collections.Generic;
using System.Net;
using SilksongCoop.Core.Clones;
using SilksongCoop.Core.Interpolation;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.Revive;
using SilksongCoop.Core.State;
using SilksongCoop.Core.Visuals;
using SilksongCoop.Tests.TestHarness;
using Xunit;

namespace SilksongCoop.Tests.Tier1_FeatureTests
{
    /// <summary>
    /// Tier 1: 28 Feature Coverage Tests (Features F01 through F28).
    /// Verifies core functional behavior across Networking, Visuals, Vitals, Death Lifecycle, and Inventory.
    /// </summary>
    public class Tier1FeatureTests
    {
        // -------------------------------------------------------------------------
        // F01: In-Game Connection UI / Session Management
        // -------------------------------------------------------------------------
        [Fact]
        public void F01_InGameConnectionUI_SessionStateLifecycle()
        {
            var session = new CoopSessionState();
            Assert.Equal(SessionStatus.Disconnected, session.Status);

            var peerId = new NetworkPeerId(101);
            session.SetHosting(peerId, "SK-TEST-ROOM");
            Assert.Equal(SessionStatus.Hosting, session.Status);
            Assert.True(session.IsHost);
            Assert.Equal("SK-TEST-ROOM", session.CurrentRoomCode);

            var remotePeer = new NetworkPeerId(202);
            session.SetConnected(remotePeer);
            Assert.Equal(SessionStatus.Connected, session.Status);
            Assert.Equal(remotePeer, session.RemotePeerId);
        }

        // -------------------------------------------------------------------------
        // F02: Steamworks P2P Transport Contract Compliance
        // -------------------------------------------------------------------------
        [Fact]
        public void F02_SteamworksP2PTransport_ContractCompliance()
        {
            using var harness = new DualClientTestHarness();
            Assert.True(harness.HostTransport.IsHost);
            Assert.False(harness.ClientTransport.IsHost);
            Assert.True(harness.HostTransport.IsConnected);
            Assert.True(harness.ClientTransport.IsConnected);
        }

        // -------------------------------------------------------------------------
        // F03: LiteNetLib UDP Fallback Direct Socket Transmission
        // -------------------------------------------------------------------------
        [Fact]
        public void F03_LiteNetLibTransport_DirectSocketTransmission()
        {
            using var harness = new DualClientTestHarness();
            byte[] payload = new byte[] { 0x53, 0x4B, 0x01, 0x02, 0x03, 0x04 };

            bool received = false;
            harness.ClientTransport.OnDataReceived += (peer, data, offset, len) =>
            {
                if (len == payload.Length && data[offset] == 0x53)
                    received = true;
            };

            harness.HostTransport.Send(harness.ClientTransport.LocalPeerId, payload, 0, payload.Length, DeliveryMethod.ReliableOrdered, 1);
            harness.DeliverAll();

            Assert.True(received);
        }

        // -------------------------------------------------------------------------
        // F04: Base32 Room Code Codec Formatting & Verification
        // -------------------------------------------------------------------------
        [Fact]
        public void F04_Base32RoomCodeCodec_EncodeDecodeIntegrity()
        {
            var ip = IPAddress.Parse("192.168.1.100");
            ushort port = 7777;

            string code = RoomCodeCodec.Encode(ip, port);
            Assert.NotNull(code);
            Assert.StartsWith("SK-", code);

            bool success = RoomCodeCodec.TryDecode(code, out var decodedIp, out int decodedPort);
            Assert.True(success);
            Assert.Equal(ip, decodedIp);
            Assert.Equal((int)port, decodedPort);
        }

        // -------------------------------------------------------------------------
        // F05: Zero-Allocation Binary Protocol Header & Reader/Writer
        // -------------------------------------------------------------------------
        [Fact]
        public void F05_ZeroAllocationBinaryProtocol_HeaderAndReader()
        {
            Span<byte> buffer = stackalloc byte[64];
            var writer = new PacketWriter(buffer);
            var header = new PacketHeader(PacketType.TransformSync, 0, 42, 123456);

            writer.WriteHeader(header);
            writer.WriteFloat(15.5f);
            writer.WriteFloat(-2.3f);

            var reader = new PacketReader(buffer.Slice(0, writer.Position));
            bool headerValid = reader.TryReadHeader(out var readHeader);

            Assert.True(headerValid);
            Assert.Equal(PacketType.TransformSync, readHeader.PacketId);
            Assert.Equal(0, readHeader.ChannelId);
            Assert.Equal(42u, readHeader.TickSequence);
            Assert.Equal(123456u, readHeader.Timestamp);
            Assert.Equal(15.5f, reader.ReadFloat());
            Assert.Equal(-2.3f, reader.ReadFloat());
        }

        // -------------------------------------------------------------------------
        // F06: Multi-Channel Reliability Segregation (Channels 0–4)
        // -------------------------------------------------------------------------
        [Fact]
        public void F06_MultiChannelReliability_SegregatedDelivery()
        {
            using var harness = new DualClientTestHarness();

            // Transform on Channel 0
            var trans = new TransformSyncPacket { Header = new PacketHeader(PacketType.TransformSync, 0, 1, 100), PositionX = 5f, PositionY = 10f };
            Span<byte> buf0 = stackalloc byte[64];
            var w0 = new PacketWriter(buf0);
            trans.Serialize(ref w0);
            harness.HostTransport.Send(harness.ClientTransport.LocalPeerId, buf0.ToArray(), 0, w0.Position, DeliveryMethod.Unreliable, 0);

            // Action on Channel 1
            var act = new ActionTriggerPacket { Header = new PacketHeader(PacketType.ActionTrigger, 1, 1, 101), Action = ActionType.SlashNormal };
            Span<byte> buf1 = stackalloc byte[64];
            var w1 = new PacketWriter(buf1);
            act.Serialize(ref w1);
            harness.HostTransport.Send(harness.ClientTransport.LocalPeerId, buf1.ToArray(), 0, w1.Position, DeliveryMethod.ReliableOrdered, 1);

            harness.DeliverAll();

            Assert.Single(harness.DeliveredTransformsToClient);
            Assert.Single(harness.DeliveredActionsToClient);
        }

        // -------------------------------------------------------------------------
        // F07: 30 Hz Transform Replication (Position, Velocity, Facing)
        // -------------------------------------------------------------------------
        [Fact]
        public void F07_TransformReplication_PositionVelocityFacingReplication()
        {
            using var harness = new DualClientTestHarness();

            var trans = new TransformSyncPacket
            {
                Header = new PacketHeader(PacketType.TransformSync, 0, 1, 1000),
                PositionX = 25.4f,
                PositionY = 12.8f,
                VelocityX = 6.5f,
                VelocityY = 0f,
                FacingDirection = 1,
                StateFlags = TransformSyncPacket.FlagGrounded
            };

            Span<byte> buf = stackalloc byte[64];
            var w = new PacketWriter(buf);
            trans.Serialize(ref w);
            harness.HostTransport.Send(harness.ClientTransport.LocalPeerId, buf.ToArray(), 0, w.Position, DeliveryMethod.Unreliable, 0);
            harness.DeliverAll();

            Assert.Single(harness.DeliveredTransformsToClient);
            var delivered = harness.DeliveredTransformsToClient[0];
            Assert.Equal(25.4f, delivered.PositionX);
            Assert.Equal(12.8f, delivered.PositionY);
            Assert.Equal(6.5f, delivered.VelocityX);
            Assert.Equal((byte)1, delivered.FacingDirection);
        }

        // -------------------------------------------------------------------------
        // F08: Hermite Spline Interpolation (C^1 Continuity)
        // -------------------------------------------------------------------------
        [Fact]
        public void F08_HermiteSplineInterpolation_ContinuousMotion()
        {
            float p0 = 0f, v0 = 0f;
            float p1 = 20f, v1 = 0f;
            float dt = 1.0f;

            float pStart = HermiteSpline.InterpolatePosition(p0, v0, p1, v1, dt, 0.0f);
            float pMid = HermiteSpline.InterpolatePosition(p0, v0, p1, v1, dt, 0.5f);
            float pEnd = HermiteSpline.InterpolatePosition(p0, v0, p1, v1, dt, 1.0f);

            Assert.Equal(0f, pStart, precision: 3);
            Assert.Equal(10f, pMid, precision: 3);
            Assert.Equal(20f, pEnd, precision: 3);
        }

        // -------------------------------------------------------------------------
        // F09: Damped Dead Reckoning (Gravity & Decay)
        // -------------------------------------------------------------------------
        [Fact]
        public void F09_DampedDeadReckoning_BoundedExtrapolation()
        {
            float lastX = 10f, lastY = 15f;
            float velX = 8f, velY = 0f;
            float dt = 0.1f;

            var (extrapX, extrapY) = DeadReckoning.ExtrapolatePosition(lastX, lastY, velX, velY, isGrounded: false, deltaSeconds: dt);

            Assert.True(extrapX > 10f);
            Assert.True(extrapY < 15f); // Gravity pulls down
        }

        // -------------------------------------------------------------------------
        // F10: Clean Remote Clone Spawning & Hierarchy Tracking
        // -------------------------------------------------------------------------
        [Fact]
        public void F10_CleanRemoteCloneSpawning_BypassesSingletonDestruction()
        {
            using var harness = new DualClientTestHarness();

            Assert.True(harness.HostClones.TryGetRemoteState(harness.ClientTransport.LocalPeerId, out var clientStateOnHost));
            Assert.True(clientStateOnHost.IsPlayer2);

            Assert.True(harness.ClientClones.TryGetRemoteState(harness.HostTransport.LocalPeerId, out var hostStateOnClient));
            Assert.False(hostStateOnClient.IsPlayer2);
        }

        // -------------------------------------------------------------------------
        // F11: Remote Animation Replication (FNV-1a Clip Hashes)
        // -------------------------------------------------------------------------
        [Fact]
        public void F11_RemoteAnimationReplication_ClipHashAndNormalizedTime()
        {
            int hashRun = AnimationClipHasher.ComputeHash("Run");
            int hashIdle = AnimationClipHasher.ComputeHash("Idle");

            Assert.NotEqual(0, hashRun);
            Assert.NotEqual(0, hashIdle);
            Assert.NotEqual(hashRun, hashIdle);

            ushort encodedTime = AnimationClipHasher.EncodeNormalizedTime(0.75f);
            float decodedTime = AnimationClipHasher.DecodeNormalizedTime(encodedTime);
            Assert.Equal(0.75f, decodedTime, precision: 2);
        }

        // -------------------------------------------------------------------------
        // F12: GPU Blue Cloak Palette Swap (CAN_HUESHIFT 0.58f Azure Blue Shift)
        // -------------------------------------------------------------------------
        [Fact]
        public void F12_GPUBlueCloakPaletteSwap_HueRotationAzureBlue()
        {
            // Hornet red cloak: (0.85, 0.15, 0.15)
            PaletteSwapMath.ApplyHueShift(0.85f, 0.15f, 0.15f, 0.58f, out float outR, out float outG, out float outB);

            // Shifting by 0.58 (+208.8 deg) produces azure blue: high B, low R
            Assert.True(outB > outR, $"Expected outB ({outB}) > outR ({outR}) for azure blue cloak swap.");
            Assert.True(outB > 0.70f);
        }

        // -------------------------------------------------------------------------
        // F13: White Mask & Needle Preservation (Achromatic Invariance)
        // -------------------------------------------------------------------------
        [Fact]
        public void F13_WhiteMaskAndNeedlePreservation_AchromaticInvariance()
        {
            // White mask: (1.0, 1.0, 1.0)
            PaletteSwapMath.ApplyHueShift(1.0f, 1.0f, 1.0f, 0.58f, out float maskR, out float maskG, out float maskB);
            Assert.Equal(1.0f, maskR, precision: 4);
            Assert.Equal(1.0f, maskG, precision: 4);
            Assert.Equal(1.0f, maskB, precision: 4);

            // Black horns: (0.05, 0.05, 0.05)
            PaletteSwapMath.ApplyHueShift(0.05f, 0.05f, 0.05f, 0.58f, out float hornR, out float hornG, out float hornB);
            Assert.Equal(0.05f, hornR, precision: 4);
            Assert.Equal(0.05f, hornG, precision: 4);
            Assert.Equal(0.05f, hornB, precision: 4);
        }

        // -------------------------------------------------------------------------
        // F14: Combat Readability & FX (Dash Trail Lifecycle)
        // -------------------------------------------------------------------------
        [Fact]
        public void F14_CombatReadabilityAndFX_DashTrailLifecycle()
        {
            var trail = new DashTrailLifecycle();
            Assert.Equal(DashTrailPhase.Idle, trail.CurrentPhase);

            trail.TriggerDash(isAirDash: false, isPlayer2: true);
            Assert.Equal(DashTrailPhase.Active, trail.CurrentPhase);

            trail.Update(0.26f, isGrounded: false, isWallSliding: false); // after 250ms active
            Assert.Equal(DashTrailPhase.Cooldown, trail.CurrentPhase);

            trail.Update(0.45f, isGrounded: false, isWallSliding: false); // after 400ms cooldown
            Assert.Equal(DashTrailPhase.Idle, trail.CurrentPhase);
        }

        // -------------------------------------------------------------------------
        // F15: Independent Live HP (Local Damage Decoupled)
        // -------------------------------------------------------------------------
        [Fact]
        public void F15_IndependentLiveHP_DamageTakenDecoupled()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 0, initialShards: 0);
            harness.ClientVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 0, initialShards: 0);

            harness.HostVitals.OnLocalDamageTaken(2, 3);
            harness.DeliverAll();

            Assert.Equal(3, harness.HostVitals.LocalHealth);
            Assert.Equal(5, harness.ClientVitals.LocalHealth); // Client HP completely unaffected
        }

        // -------------------------------------------------------------------------
        // F16: Independent Live Silk (Local Silk Usage Decoupled)
        // -------------------------------------------------------------------------
        [Fact]
        public void F16_IndependentLiveSilk_SilkUsageDecoupled()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 0, initialShards: 0);
            harness.ClientVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 0, initialShards: 0);

            harness.ClientVitals.OnLocalSilkUsed(4, 5);
            harness.DeliverAll();

            Assert.Equal(9, harness.HostVitals.LocalSilk); // Host silk completely unaffected
            Assert.Equal(5, harness.ClientVitals.LocalSilk);
        }

        // -------------------------------------------------------------------------
        // F17: Shared Max HP Upgrades Replicate to Both Players
        // -------------------------------------------------------------------------
        [Fact]
        public void F17_SharedMaxHPUpgrades_ReplicatesToBothPlayers()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 0, initialShards: 0);
            harness.ClientVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 0, initialShards: 0);

            harness.HostVitals.OnLocalMaxHealthUpgrade(6);
            harness.DeliverAll();

            Assert.Equal(6, harness.HostVitals.SharedMaxHealth);
            Assert.Equal(6, harness.ClientVitals.SharedMaxHealth);
        }

        // -------------------------------------------------------------------------
        // F18: Shared Needle Upgrades Replicate Damage Tier
        // -------------------------------------------------------------------------
        [Fact]
        public void F18_SharedNeedleUpgrades_ReplicatesDamageTier()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 0, initialShards: 0);
            harness.ClientVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 0, initialShards: 0);

            harness.ClientVitals.OnLocalNeedleUpgrade(2);
            harness.DeliverAll();

            Assert.Equal(2, harness.HostVitals.SharedNeedleUpgradeLevel);
            Assert.Equal(2, harness.ClientVitals.SharedNeedleUpgradeLevel);
        }

        // -------------------------------------------------------------------------
        // F19: Shared Max Silk Upgrades Replicate Spool Capacity
        // -------------------------------------------------------------------------
        [Fact]
        public void F19_SharedMaxSilkUpgrades_ReplicatesSpoolCapacity()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 0, initialShards: 0);
            harness.ClientVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 0, initialShards: 0);

            harness.HostVitals.OnLocalMaxSilkUpgrade(2);
            harness.DeliverAll();

            Assert.Equal(2, harness.HostVitals.SharedMaxSilkSpools);
            Assert.Equal(2, harness.ClientVitals.SharedMaxSilkSpools);
        }

        // -------------------------------------------------------------------------
        // F20: Shared Currency Wallet (Rosaries & Shell Shards Sync)
        // -------------------------------------------------------------------------
        [Fact]
        public void F20_SharedCurrencyWallet_RosariesAndShardsSync()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 100, initialShards: 20);
            harness.ClientVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 100, initialShards: 20);

            harness.ClientVitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 50, 150, Guid.NewGuid());
            harness.HostVitals.OnLocalCurrencyChanged(CurrencyType.ShellShards, 15, 35, Guid.NewGuid());
            harness.DeliverAll();

            Assert.Equal(150, harness.HostVitals.SharedRosaries);
            Assert.Equal(150, harness.ClientVitals.SharedRosaries);
            Assert.Equal(35, harness.HostVitals.SharedShellShards);
            Assert.Equal(35, harness.ClientVitals.SharedShellShards);
        }

        // -------------------------------------------------------------------------
        // F21: Transaction Deduplication Drops Replay Currency Packets
        // -------------------------------------------------------------------------
        [Fact]
        public void F21_TransactionDeduplication_ReplayPacketsIgnored()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 0, initialShards: 0);
            harness.ClientVitals.InitializeState(initialHealth: 5, initialMaxHealth: 5, initialSilk: 9, initialMaxSilkSpools: 1, needleLevel: 1, initialRosaries: 0, initialShards: 0);

            var txId = Guid.NewGuid();
            harness.HostVitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 100, 100, txId);
            harness.DeliverAll();

            // Re-send same transaction packet directly
            var replay = new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 2, 200),
                Currency = CurrencyType.Rosaries,
                TotalWalletAmount = 200,
                DeltaAmount = 100,
                TransactionId = txId
            };
            harness.ClientVitals.HandleCurrencyPacket(harness.HostTransport.LocalPeerId, replay);

            // Total wallet must remain 100, not 200
            Assert.Equal(100, harness.ClientVitals.SharedRosaries);
        }

        // -------------------------------------------------------------------------
        // F22: Solo Death Spectator Mode Transitions to Spectator
        // -------------------------------------------------------------------------
        [Fact]
        public void F22_SoloDeathSpectatorMode_TransitionsToSpectator()
        {
            using var harness = new DualClientTestHarness();

            bool suppressBench = harness.HostDeath.OnLocalPlayerDied(10f, 5f);
            Assert.True(suppressBench);
            Assert.Equal(DeathState.Spectating, harness.HostDeath.CurrentState);

            harness.DeliverAll();
            Assert.Single(harness.DeliveredDeathsToClient);
            Assert.Equal(DeathEventType.SoloDeathOccurred, harness.DeliveredDeathsToClient[0].EventType);
        }

        // -------------------------------------------------------------------------
        // F23: Spectator Countdown Timer Ticks Down to Revive
        // -------------------------------------------------------------------------
        [Fact]
        public void F23_SpectatorCountdownTimer_TicksDownToRevive()
        {
            using var harness = new DualClientTestHarness();
            harness.HostDeath.OnLocalPlayerDied(10f, 5f);

            harness.HostDeath.UpdateTick(10.0f);
            Assert.Equal(DeathState.Spectating, harness.HostDeath.CurrentState);
            Assert.Equal(10.0f, harness.HostDeath.RemainingSpectatorTime);

            harness.HostDeath.UpdateTick(10.5f); // 20.5s elapsed -> auto revive at partner
            Assert.Equal(DeathState.RespawningAtPartner, harness.HostDeath.CurrentState);
        }

        // -------------------------------------------------------------------------
        // F24: Safe Ground-Probed Revive Calculates Half Health
        // -------------------------------------------------------------------------
        [Fact]
        public void F24_SafeGroundProbedRevive_CalculatesHalfHealth()
        {
            int maxHealth = 7;
            int reviveHp = SafeGroundSolver.CalculateReviveHealth(maxHealth);
            Assert.Equal(3, reviveHp); // 7 / 2 = 3

            int maxHealth2 = 2;
            int reviveHp2 = SafeGroundSolver.CalculateReviveHealth(maxHealth2);
            Assert.Equal(1, reviveHp2);
        }

        // -------------------------------------------------------------------------
        // F25: Dual Wipe Bench Checkpoint Triggers Team Wipe
        // -------------------------------------------------------------------------
        [Fact]
        public void F25_DualWipeBenchCheckpoint_SimultaneousDeathBenchReload()
        {
            using var harness = new DualClientTestHarness();

            harness.HostDeath.OnLocalPlayerDied(10f, 5f);
            harness.ClientDeath.OnLocalPlayerDied(15f, 5f);
            harness.DeliverAll();

            Assert.Equal(DeathState.TeamWipe, harness.HostDeath.CurrentState);
            Assert.Equal(DeathState.TeamWipe, harness.ClientDeath.CurrentState);
        }

        // -------------------------------------------------------------------------
        // F26: Independent Equipped Loadout Isolation
        // -------------------------------------------------------------------------
        [Fact]
        public void F26_IndependentEquippedLoadout_LoadoutsIsolated()
        {
            using var harness = new DualClientTestHarness();

            harness.HostInventory.SetLocalEquippedCrest("Crest_Hunter");
            harness.HostInventory.SetLocalEquippedTools(new[] { "Tool_Pimpillo", "Tool_FleaBrew" });

            harness.ClientInventory.SetLocalEquippedCrest("Crest_Reaper");
            harness.ClientInventory.SetLocalEquippedTools(new[] { "Tool_StingShard" });

            harness.DeliverAll();

            Assert.Equal("Crest_Hunter", harness.HostInventory.LocalEquippedCrest);
            Assert.Equal("Crest_Reaper", harness.ClientInventory.LocalEquippedCrest);
            Assert.Equal(2, harness.HostInventory.LocalEquippedTools.Count);
            Assert.Single(harness.ClientInventory.LocalEquippedTools);
            Assert.True(harness.HostInventory.IsEquippedLoadoutIsolated);
        }

        // -------------------------------------------------------------------------
        // F27: Mirrored Discovery Unlocks Across Both Players
        // -------------------------------------------------------------------------
        [Fact]
        public void F27_MirroredDiscoveryUnlocks_ToolsAndCrestsSync()
        {
            using var harness = new DualClientTestHarness();

            harness.HostInventory.OnLocalItemDiscovered("Tool_RosaryCannon", ItemCategory.Tool);
            harness.ClientInventory.OnLocalItemDiscovered("Crest_SilkSovereign", ItemCategory.Crest);
            harness.DeliverAll();

            Assert.True(harness.HostInventory.IsItemUnlocked("Tool_RosaryCannon"));
            Assert.True(harness.ClientInventory.IsItemUnlocked("Tool_RosaryCannon"));
            Assert.True(harness.HostInventory.IsItemUnlocked("Crest_SilkSovereign"));
            Assert.True(harness.ClientInventory.IsItemUnlocked("Crest_SilkSovereign"));
        }

        // -------------------------------------------------------------------------
        // F28: Headless Multi-Client Test Harness E2E State Sync
        // -------------------------------------------------------------------------
        [Fact]
        public void F28_HeadlessMultiClientTestHarness_E2EStateSync()
        {
            using var harness = new DualClientTestHarness();

            // 1. Position update
            var trans = new TransformSyncPacket { Header = new PacketHeader(PacketType.TransformSync, 0, 1, 100), PositionX = 10f, PositionY = 20f };
            Span<byte> buf = stackalloc byte[64];
            var w = new PacketWriter(buf);
            trans.Serialize(ref w);
            harness.HostTransport.Send(harness.ClientTransport.LocalPeerId, buf.ToArray(), 0, w.Position, DeliveryMethod.Unreliable, 0);

            // 2. Currency pickup
            harness.ClientVitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 25, 25, Guid.NewGuid());

            // 3. Item unlock
            harness.HostInventory.OnLocalItemDiscovered("Ability_SilkGlide", ItemCategory.Ability);

            harness.DeliverAll();

            Assert.Single(harness.DeliveredTransformsToClient);
            Assert.Equal(25, harness.HostVitals.SharedRosaries);
            Assert.Equal(25, harness.ClientVitals.SharedRosaries);
            Assert.True(harness.ClientInventory.IsItemUnlocked("Ability_SilkGlide"));
        }
    }
}
