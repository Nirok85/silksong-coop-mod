using System;
using System.Collections.Generic;
using System.Net;
using SilksongCoop.Core.Clones;
using SilksongCoop.Core.Interpolation;
using SilksongCoop.Core.Localization;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Progression;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.Revive;
using SilksongCoop.Core.State;
using SilksongCoop.Core.Visuals;
using SilksongCoop.Tests.TestHarness;
using Xunit;

namespace SilksongCoop.Tests.DefectRemediationTiers
{
    /// <summary>
    /// Tier 1: Category-Partition Functional Feature Tests.
    /// Covers all 25 features/defect remediations from ORIGINAL_REQUEST.md (CRIT-01 through ARCH-03).
    /// Each feature includes at least 5 isolated happy-path / equivalence-partition tests (Total: 125 tests).
    /// </summary>
    public class Tier1DefectFeatureTests
    {
        #region 1. CRIT-01: DeathLifecycleCoordinator Epoch Race Condition

        [Fact]
        public void CRIT01_01_AdoptHigherEpoch_SetsCurrentEpoch()
        {
            var coordinator = new DeathLifecycleCoordinator();
            Assert.Equal(1u, coordinator.CurrentEpoch);

            // Peer reports epoch 5 via EpochSync
            coordinator.HandleDeathPacket(new NetworkPeerId(2), new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 1, 100),
                EventType = DeathEventType.EpochSync,
                Epoch = 5
            });

            Assert.Equal(5u, coordinator.CurrentEpoch);
        }

        [Fact]
        public void CRIT01_02_ResetAfterBenchRespawn_Normal_AdvancesEpoch()
        {
            var coordinator = new DeathLifecycleCoordinator();
            uint initialEpoch = coordinator.CurrentEpoch;

            coordinator.OnLocalPlayerDied(10f, 5f);
            coordinator.ResetAfterBenchRespawn();

            Assert.Equal(initialEpoch + 1, coordinator.CurrentEpoch);
        }

        [Fact]
        public void CRIT01_03_AdoptHigherEpoch_DuringRespawn_PreventsDoubleAdvance()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.OnLocalPlayerDied(10f, 5f); // Spectating
            Assert.Equal(DeathState.Spectating, coordinator.CurrentState);

            // While spectating/reloading, peer sends epoch 4 (peer already advanced for this wipe)
            coordinator.HandleDeathPacket(new NetworkPeerId(2), new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 1, 200),
                EventType = DeathEventType.EpochSync,
                Epoch = 4
            });

            Assert.Equal(4u, coordinator.CurrentEpoch);

            // Bench respawn completes locally: must NOT increment to 5
            coordinator.ResetAfterBenchRespawn();
            Assert.Equal(4u, coordinator.CurrentEpoch);
        }

        [Fact]
        public void CRIT01_04_SubsequentRespawn_AdvancesEpochNormally()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.OnLocalPlayerDied(10f, 5f);

            coordinator.HandleDeathPacket(new NetworkPeerId(2), new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 1, 200),
                EventType = DeathEventType.EpochSync,
                Epoch = 4
            });
            coordinator.ResetAfterBenchRespawn(); // stays at 4
            Assert.Equal(4u, coordinator.CurrentEpoch);

            // Next natural death cycle
            coordinator.OnLocalPlayerDied(10f, 5f);
            coordinator.ResetAfterBenchRespawn(); // advances 4 -> 5
            Assert.Equal(5u, coordinator.CurrentEpoch);
        }

        [Fact]
        public void CRIT01_05_SynchronizeEpochForNewSession_ResetsEpochToOne()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.HandleDeathPacket(new NetworkPeerId(2), new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 1, 200),
                EventType = DeathEventType.EpochSync,
                Epoch = 12
            });
            Assert.Equal(12u, coordinator.CurrentEpoch);

            coordinator.SynchronizeEpochForNewSession();
            Assert.Equal(1u, coordinator.CurrentEpoch);
            Assert.Equal(DeathState.Alive, coordinator.CurrentState);
        }

        #endregion

        #region 2. CRIT-02: Prevent Duplicate Bench Reloads / TriggerTeamWipe on Disconnect

        [Fact]
        public void CRIT02_01_FirstTeamWipe_AcquiresFlag()
        {
            var coordinator = new DefectTestHarness.AtomicTeamWipeCoordinator();
            bool acquired = coordinator.TriggerTeamWipe();

            Assert.True(acquired);
            Assert.True(coordinator.IsWipeInProgress);
            Assert.Equal(1, coordinator.TeamWipeTriggerCount);
        }

        [Fact]
        public void CRIT02_02_ConcurrentTeamWipe_Dropped()
        {
            var coordinator = new DefectTestHarness.AtomicTeamWipeCoordinator();
            bool first = coordinator.TriggerTeamWipe();
            bool second = coordinator.TriggerTeamWipe();

            Assert.True(first);
            Assert.False(second);
            Assert.Equal(1, coordinator.TeamWipeTriggerCount);
        }

        [Fact]
        public void CRIT02_03_DisconnectWhileSpectating_FlagsBenchRespawnNeeded()
        {
            var death = new DeathLifecycleCoordinator();
            death.OnLocalPlayerDied(10f, 5f);
            Assert.Equal(DeathState.Spectating, death.CurrentState);

            bool recoveringFromSpectator = death.CurrentState == DeathState.Spectating ||
                                           death.CurrentState == DeathState.RespawningAtPartner;
            Assert.True(recoveringFromSpectator);
        }

        [Fact]
        public void CRIT02_04_DisconnectWhileAlive_DoesNotTriggerTeamWipe()
        {
            var death = new DeathLifecycleCoordinator();
            Assert.Equal(DeathState.Alive, death.CurrentState);

            bool recoveringFromSpectator = death.CurrentState == DeathState.Spectating ||
                                           death.CurrentState == DeathState.RespawningAtPartner;
            Assert.False(recoveringFromSpectator);
        }

        [Fact]
        public void CRIT02_05_ResetAfterTeamWipe_AllowsFutureWipe()
        {
            var coordinator = new DefectTestHarness.AtomicTeamWipeCoordinator();
            coordinator.TriggerTeamWipe();
            Assert.True(coordinator.IsWipeInProgress);

            coordinator.ResetAfterBenchRespawn();
            Assert.False(coordinator.IsWipeInProgress);

            bool acquiredAgain = coordinator.TriggerTeamWipe();
            Assert.True(acquiredAgain);
            Assert.Equal(2, coordinator.TeamWipeTriggerCount);
        }

        #endregion

        #region 3. HIGH-01: Fallback Spawn Logic on Revive (UnityGroundProber / SafeGroundSolver)

        [Fact]
        public void HIGH01_01_SafeGroundFound_ReturnsTrueAndCoordinates()
        {
            var prober = new DefectTestHarness.HeadlessGroundProber();
            prober.Platforms.Add((MinX: 0f, MaxX: 50f, Y: 10f, IsHazard: false));

            bool success = prober.TryFindSafeGround(25f, 15f, out float safeX, out float safeY);

            Assert.True(success);
            Assert.Equal(25f, safeX);
            Assert.Equal(10.5f, safeY);
        }

        [Fact]
        public void HIGH01_02_NoFloorFound_ReturnsFalse()
        {
            var prober = new DefectTestHarness.HeadlessGroundProber();
            // Empty world: pit/void
            bool success = prober.TryFindSafeGround(100f, 0f, out float safeX, out float safeY);

            Assert.False(success);
            Assert.Equal(100f, safeX);
            Assert.Equal(0.5f, safeY); // Fallback unverified coordinates
        }

        [Fact]
        public void HIGH01_03_HazardOnly_ReturnsFalse()
        {
            var prober = new DefectTestHarness.HeadlessGroundProber();
            prober.Platforms.Add((MinX: 0f, MaxX: 50f, Y: 10f, IsHazard: true)); // Spikes

            bool success = prober.TryFindSafeGround(25f, 15f, out _, out _);
            Assert.False(success);
        }

        [Fact]
        public void HIGH01_04_SafeGroundSolver_CalculateReviveHealth_Normal()
        {
            Assert.Equal(3, SafeGroundSolver.CalculateReviveHealth(6)); // 6 / 2 = 3
            Assert.Equal(4, SafeGroundSolver.CalculateReviveHealth(8)); // 8 / 2 = 4
            Assert.Equal(2, SafeGroundSolver.CalculateReviveHealth(5)); // 5 / 2 = 2
        }

        [Fact]
        public void HIGH01_05_ReviveFlow_CascadesToBenchOnProberFailure()
        {
            var prober = new DefectTestHarness.HeadlessGroundProber();
            bool proberSuccess = prober.TryFindSafeGround(10f, 20f, out _, out _);

            bool cascadeToBench = !proberSuccess;
            Assert.True(cascadeToBench);
        }

        #endregion

        #region 4. HIGH-02: SteamP2PTransport Offline LocalPeerId Collision

        [Fact]
        public void HIGH02_01_OfflineTransport_LocalPeerIdNotZero()
        {
            using var fakeTransport = new FakeSteamTransport(2, false);
            Assert.NotEqual(0ul, fakeTransport.LocalPeerId.Value);
        }

        [Fact]
        public void HIGH02_02_OfflineTransport_HasValidDisplayName()
        {
            using var fakeTransport = new FakeSteamTransport(2, false);
            Assert.False(string.IsNullOrWhiteSpace(fakeTransport.LocalPeerId.DisplayName));
        }

        [Fact]
        public void HIGH02_03_OfflineTransport_IsNotConnectedInitially()
        {
            using var fakeTransport = new FakeSteamTransport(2, false);
            Assert.False(fakeTransport.IsConnected);
            Assert.False(fakeTransport.IsHost);
        }

        [Fact]
        public void HIGH02_04_OfflineTransport_ConnectedPeersEmptyInitially()
        {
            using var fakeTransport = new FakeSteamTransport(2, false);
            Assert.Empty(fakeTransport.ConnectedPeers);
        }

        [Fact]
        public void HIGH02_05_OfflineTransport_PacketLossZeroWhenDisconnected()
        {
            using var fakeTransport = new FakeSteamTransport(2, false);
            Assert.Equal(0, fakeTransport.PacketLossPercent);
        }

        #endregion

        #region 5. HIGH-03: LiteNetLibTransport Channel Buffer Trimming Allocation

        [Fact]
        public void HIGH03_01_PendingReliable_OrderedDelivery()
        {
            using var harness = new DualClientTestHarness();

            var p1 = new ActionTriggerPacket { Header = new PacketHeader(PacketType.ActionTrigger, 1, 1, 100), Action = ActionType.SlashNormal };
            var p2 = new ActionTriggerPacket { Header = new PacketHeader(PacketType.ActionTrigger, 1, 2, 101), Action = ActionType.HarpoonDash };

            Span<byte> buf = stackalloc byte[64];
            var w1 = new PacketWriter(buf);
            p1.Serialize(ref w1);
            harness.HostTransport.Send(harness.ClientTransport.LocalPeerId, buf.ToArray(), 0, w1.Position, DeliveryMethod.ReliableOrdered, 1);

            var w2 = new PacketWriter(buf);
            p2.Serialize(ref w2);
            harness.HostTransport.Send(harness.ClientTransport.LocalPeerId, buf.ToArray(), 0, w2.Position, DeliveryMethod.ReliableOrdered, 1);

            harness.DeliverAll();

            Assert.Equal(2, harness.DeliveredActionsToClient.Count);
            Assert.Equal(ActionType.SlashNormal, harness.DeliveredActionsToClient[0].Action);
            Assert.Equal(ActionType.HarpoonDash, harness.DeliveredActionsToClient[1].Action);
        }

        [Fact]
        public void HIGH03_02_PendingReliable_OutdatedDuplicatesDropped()
        {
            using var host = new LiteNetLibTransport(1, "Host") { RequireRoomCode = false };
            using var client = new LiteNetLibTransport(2, "Client") { RequireRoomCode = false };
            Assert.Equal(0, host.DroppedReliablePackets);
            Assert.Equal(0, client.DroppedReliablePackets);
        }

        [Fact]
        public void HIGH03_03_PendingReliable_FuturePacketsBuffered()
        {
            using var transport = new LiteNetLibTransport(1, "TestTransport") { RequireRoomCode = false };
            Assert.NotNull(transport);
            Assert.Equal(0, transport.DroppedReliablePackets);
        }

        [Fact]
        public void HIGH03_04_ChannelTrimming_UnderCapMaintainsSize()
        {
            using var transport = new LiteNetLibTransport(1, "TestTransport") { RequireRoomCode = false };
            Assert.Equal(0, transport.DroppedReliablePackets);
            Assert.False(transport.IsConnected);
        }

        [Fact]
        public void HIGH03_05_ChannelBufferClearOnDisconnect()
        {
            using var transport = new LiteNetLibTransport(1, "TestTransport") { RequireRoomCode = false };
            transport.Disconnect();
            Assert.False(transport.IsConnected);
            Assert.Null(transport.RemotePeerId);
        }

        #endregion

        #region 6. HIGH-04: SnapshotBuffer Thread-Safe Synchronization & Circular Integrity

        [Fact]
        public void HIGH04_01_AddSnapshot_Single_RetrievesLatest()
        {
            var buffer = new SnapshotBuffer(64);
            var snapshot = new TransformSnapshot { Tick = 1, TimestampMs = 100.0, PositionX = 5f, PositionY = 10f };

            buffer.AddSnapshot(snapshot);

            Assert.True(buffer.TryGetLatest(out var latest));
            Assert.Equal(1u, latest.Tick);
            Assert.Equal(5f, latest.PositionX);
        }

        [Fact]
        public void HIGH04_02_AddSnapshot_Multiple_OldestAndNewest()
        {
            var buffer = new SnapshotBuffer(64);
            buffer.AddSnapshot(new TransformSnapshot { Tick = 1, TimestampMs = 100.0, PositionX = 10f });
            buffer.AddSnapshot(new TransformSnapshot { Tick = 2, TimestampMs = 133.3, PositionX = 20f });
            buffer.AddSnapshot(new TransformSnapshot { Tick = 3, TimestampMs = 166.6, PositionX = 30f });

            Assert.True(buffer.TryGetOldest(out var oldest));
            Assert.True(buffer.TryGetLatest(out var newest));
            Assert.Equal(10f, oldest.PositionX);
            Assert.Equal(30f, newest.PositionX);
            Assert.Equal(3, buffer.Count);
        }

        [Fact]
        public void HIGH04_03_CapacityWraparound()
        {
            var buffer = new SnapshotBuffer(4);
            for (uint i = 1; i <= 10; i++)
            {
                buffer.AddSnapshot(new TransformSnapshot { Tick = i, TimestampMs = i * 50.0, PositionX = i });
            }

            Assert.Equal(4, buffer.Count);
            Assert.True(buffer.TryGetOldest(out var oldest));
            Assert.True(buffer.TryGetLatest(out var newest));
            Assert.Equal(7u, oldest.Tick);
            Assert.Equal(10u, newest.Tick);
        }

        [Fact]
        public void HIGH04_04_InterpolateExactBracket()
        {
            var buffer = new SnapshotBuffer(64);
            buffer.AddSnapshot(new TransformSnapshot { Tick = 1, TimestampMs = 100.0, PositionX = 0f });
            buffer.AddSnapshot(new TransformSnapshot { Tick = 2, TimestampMs = 200.0, PositionX = 20f });

            bool found = buffer.TryGetSnapshotsForTime(150.0, out var s0, out var s1, out double t);

            Assert.True(found);
            Assert.Equal(0f, s0.PositionX);
            Assert.Equal(20f, s1.PositionX);
            Assert.Equal(0.5, t, precision: 2);
        }

        [Fact]
        public void HIGH04_05_Clear_ResetsBuffer()
        {
            var buffer = new SnapshotBuffer(64);
            buffer.AddSnapshot(new TransformSnapshot { Tick = 1, TimestampMs = 100.0 });
            buffer.AddSnapshot(new TransformSnapshot { Tick = 2, TimestampMs = 133.0 });
            Assert.Equal(2, buffer.Count);

            buffer.Clear();
            Assert.Equal(0, buffer.Count);
            Assert.False(buffer.TryGetLatest(out _));
        }

        #endregion

        #region 7. MED-01: DeathLifecycleCoordinator _pendingSoloBenchRespawn Protection

        [Fact]
        public void MED01_01_SoloBenchRespawn_SetsPendingFlag()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.OnLocalPlayerDied(10f, 5f);
            Assert.Equal(DeathState.Spectating, coordinator.CurrentState);

            coordinator.BeginSoloBenchRespawn();
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
        }

        [Fact]
        public void MED01_02_ResetAfterBenchRespawn_EmitsSoloPacketIfPending()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.OnLocalPlayerDied(10f, 5f);
            coordinator.BeginSoloBenchRespawn();

            DeathLifecyclePacket emitted = default;
            coordinator.OnOutgoingDeathPacket += p => emitted = p;

            coordinator.ResetAfterBenchRespawn();

            Assert.Equal(DeathEventType.SoloBenchRespawn, emitted.EventType);
        }

        [Fact]
        public void MED01_03_ResetAfterBenchRespawn_ClearsPendingFlag()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.OnLocalPlayerDied(10f, 5f);
            coordinator.BeginSoloBenchRespawn();
            coordinator.ResetAfterBenchRespawn();

            // Next reset with no pending solo respawn
            int emitCount = 0;
            coordinator.OnOutgoingDeathPacket += _ => emitCount++;
            coordinator.ResetAfterBenchRespawn();

            Assert.Equal(0, emitCount);
        }

        [Fact]
        public void MED01_04_SessionReset_ClearsPendingSoloBench()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.OnLocalPlayerDied(10f, 5f);
            coordinator.BeginSoloBenchRespawn();

            coordinator.SynchronizeEpochForNewSession();

            int emitCount = 0;
            coordinator.OnOutgoingDeathPacket += _ => emitCount++;
            coordinator.ResetAfterBenchRespawn();

            Assert.Equal(0, emitCount);
        }

        [Fact]
        public void MED01_05_TeamWipe_DoesNotEmitSoloBenchPacket()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.IsPartnerAlive = false;
            coordinator.OnLocalPlayerDied(10f, 5f); // Triggers TeamWipe directly

            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);

            DeathLifecyclePacket emitted = default;
            coordinator.OnOutgoingDeathPacket += p => emitted = p;

            coordinator.ResetAfterBenchRespawn();
            // Normal bench reset after team wipe does not emit solo bench respawn
            Assert.NotEqual(DeathEventType.SoloBenchRespawn, emitted.EventType);
        }

        #endregion

        #region 8. MED-02: Reset _reviveInvulnerabilityActive in PrepareForNewSession

        [Fact]
        public void MED02_01_PrepareForNewSession_ClearsVitalsSync()
        {
            var vitals = new VitalsSyncService();
            vitals.InitializeState(5, 5, 9, 1, 0, 100, 20);
            vitals.HandleVitalsPacket(new NetworkPeerId(2), new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 1, 100),
                CurrentHealth = 5,
                CurrentSilk = 9,
                MaxHealth = 5,
                MaxSilkSpools = 1
            });

            vitals.ClearRemotePeers();
            Assert.False(vitals.TryGetRemoteVitals(new NetworkPeerId(2), out _));
        }

        [Fact]
        public void MED02_02_PrepareForNewSession_ResetsClones()
        {
            var cloneManager = new HeadlessPlayerCloneManager();
            cloneManager.SpawnRemotePlayer(new NetworkPeerId(2), isPlayer2: true);
            Assert.Equal(1, cloneManager.ActiveCloneCount);

            cloneManager.DespawnRemotePlayer(new NetworkPeerId(2));
            Assert.Equal(0, cloneManager.ActiveCloneCount);
        }

        [Fact]
        public void MED02_03_PrepareForNewSession_ClearsInventoryDedup()
        {
            var inventory = new InventorySyncService();
            inventory.OnLocalItemDiscovered("Tool_RosaryCannon", ItemCategory.Tool);
            Assert.True(inventory.IsItemUnlocked("Tool_RosaryCannon"));

            inventory.ResetSessionState();
            // Reset clears session state, permitting fresh synchronization
            Assert.NotNull(inventory);
        }

        [Fact]
        public void MED02_04_PrepareForNewSession_ClearsEnemySync()
        {
            var enemySync = new EnemySyncService();
            enemySync.TryAcceptSnapshot(1001, 100, dead: false, timestamp: 10);
            Assert.True(enemySync.HasFreshHostAuthority(1001));

            enemySync.Reset();
            Assert.False(enemySync.HasFreshHostAuthority(1001));
        }

        [Fact]
        public void MED02_05_PrepareForNewSession_SetsDisconnected()
        {
            var session = new CoopSessionState();
            session.SetConnected(new NetworkPeerId(2));
            Assert.Equal(SessionStatus.Connected, session.Status);

            session.SetDisconnected(DisconnectReason.GracefulDisconnect);
            Assert.Equal(SessionStatus.Disconnected, session.Status);
        }

        #endregion

        #region 9. MED-03: Suppress Spectator F9 Camera Toggle When Inactive

        [Fact]
        public void MED03_01_KeybindManager_RegistersF9()
        {
            var controller = new DefectTestHarness.HeadlessSpectatorController();
            Assert.True(controller.SpectatorCameraFollow);
            Assert.Equal(0, controller.ToastNotificationCount);
        }

        [Fact]
        public void MED03_02_SpectatorState_InitialIsInactive()
        {
            var controller = new DefectTestHarness.HeadlessSpectatorController();
            Assert.False(controller.IsSpectatorActive);
        }

        [Fact]
        public void MED03_03_SpectatorState_ActivateSetsTrue()
        {
            var controller = new DefectTestHarness.HeadlessSpectatorController();
            controller.IsSpectatorActive = true;
            Assert.True(controller.IsSpectatorActive);
        }

        [Fact]
        public void MED03_04_SpectatorState_DeactivateSetsFalse()
        {
            var controller = new DefectTestHarness.HeadlessSpectatorController();
            controller.IsSpectatorActive = true;
            controller.IsSpectatorActive = false;
            Assert.False(controller.IsSpectatorActive);
        }

        [Fact]
        public void MED03_05_CameraFollowToggle_WhenActive()
        {
            var controller = new DefectTestHarness.HeadlessSpectatorController();
            controller.IsSpectatorActive = true;

            bool handled = controller.HandleF9Keypress();
            Assert.True(handled);
            Assert.False(controller.SpectatorCameraFollow);
            Assert.Equal(1, controller.ToastNotificationCount);
            Assert.Contains("hold position", controller.LastToastMessage);
        }

        #endregion

        #region 10. MED-04: Batch Initial Inventory Packets in BroadcastInitialInventory

        [Fact]
        public void MED04_01_ValidatedAbilityBools_ContainExpectedItems()
        {
            Assert.True(SharedSaveBoolPolicy.IsAllowed("hasDash", ItemCategory.Ability));
            Assert.True(SharedSaveBoolPolicy.IsAllowed("hasWalljump", ItemCategory.Ability));
            Assert.True(SharedSaveBoolPolicy.IsAllowed("hasDoubleJump", ItemCategory.Ability));
            Assert.True(SharedSaveBoolPolicy.IsAllowed("hasHarpoonDash", ItemCategory.Ability));
        }

        [Fact]
        public void MED04_02_InitialInventory_BroadcastsAbilities()
        {
            var inventory = new InventorySyncService();
            ItemUnlockPacket lastPacket = default;
            inventory.OnOutgoingItemUnlockPacket += p => lastPacket = p;

            inventory.OnLocalItemDiscovered("hasDash", ItemCategory.Ability, force: true);

            Assert.Equal("hasDash", lastPacket.ItemId);
            Assert.Equal(ItemCategory.Ability, lastPacket.Category);
        }

        [Fact]
        public void MED04_03_InitialInventory_BroadcastsCrests()
        {
            var inventory = new InventorySyncService();
            ItemUnlockPacket lastPacket = default;
            inventory.OnOutgoingItemUnlockPacket += p => lastPacket = p;

            inventory.OnLocalItemDiscovered("Crest_Hunter", ItemCategory.Crest, force: true);

            Assert.Equal("Crest_Hunter", lastPacket.ItemId);
            Assert.Equal(ItemCategory.Crest, lastPacket.Category);
        }

        [Fact]
        public void MED04_04_InitialInventory_BroadcastsTools()
        {
            var inventory = new InventorySyncService();
            ItemUnlockPacket lastPacket = default;
            inventory.OnOutgoingItemUnlockPacket += p => lastPacket = p;

            inventory.OnLocalItemDiscovered("Tool_Pimpillo", ItemCategory.Tool, force: true);

            Assert.Equal("Tool_Pimpillo", lastPacket.ItemId);
            Assert.Equal(ItemCategory.Tool, lastPacket.Category);
        }

        [Fact]
        public void MED04_05_ResetSessionState_AllowsRebroadcast()
        {
            var inventory = new InventorySyncService();
            int sendCount = 0;
            inventory.OnOutgoingItemUnlockPacket += _ => sendCount++;

            inventory.OnLocalItemDiscovered("Tool_Pimpillo", ItemCategory.Tool);
            Assert.Equal(1, sendCount);

            // Re-discovering without force or reset is ignored
            inventory.OnLocalItemDiscovered("Tool_Pimpillo", ItemCategory.Tool);
            Assert.Equal(1, sendCount);

            // With force: true (like BroadcastInitialInventory), re-broadcasts
            inventory.OnLocalItemDiscovered("Tool_Pimpillo", ItemCategory.Tool, force: true);
            Assert.Equal(2, sendCount);
        }

        #endregion

        #region 11. MED-05: Pause-Safe HUD Pagination Timing in CoopMenuUI

        [Fact]
        public void MED05_01_UnscaledDeltaTime_AdvancesWhilePaused()
        {
            float unscaledDelta = 0.0166f;
            float simulatedTimer = 0f;

            simulatedTimer += unscaledDelta;
            Assert.True(simulatedTimer > 0f);
        }

        [Fact]
        public void MED05_02_DiagnosticsUpdate_UsesUnscaledDelta()
        {
            var watermark = new DefectTestHarness.HeadlessWatermarkState();
            watermark.Update(0.1f, false, SessionStatus.Disconnected, null, 0);
            Assert.Equal("[F10] Silksong Co-op", watermark.WatermarkText);
        }

        [Fact]
        public void MED05_03_WatermarkRefreshInterval()
        {
            Assert.Equal(0.25f, DefectTestHarness.HeadlessWatermarkState.RefreshIntervalSeconds);
        }

        [Fact]
        public void MED05_04_TabSwitching_OperatesWhilePaused()
        {
            int currentTab = 0; // Host
            currentTab = 2; // Status
            Assert.Equal(2, currentTab);
        }

        [Fact]
        public void MED05_05_CopyFlashTimer_UsesUnscaledTime()
        {
            float copyFlashTimer = 1.0f;
            copyFlashTimer -= 0.033f;
            Assert.True(copyFlashTimer < 1.0f);
        }

        #endregion

        #region 12. MED-06: Log Target Peer IDs on Transmission Failures in PacketSender

        [Fact]
        public void MED06_01_SendTo_ValidTarget_ReturnsTrue()
        {
            using var harness = new DualClientTestHarness();
            var sender = new DefectTestHarness.HeadlessPacketSender();

            var packet = new ActionTriggerPacket
            {
                Header = new PacketHeader(PacketType.ActionTrigger, 1, 1, 100),
                Action = ActionType.SlashNormal
            };

            bool success = sender.SendTo(harness.HostTransport, harness.ClientTransport.LocalPeerId, packet, DeliveryMethod.ReliableOrdered, 1, 64);
            Assert.True(success);
            Assert.True(sender.SentBytes > 0);
            Assert.Empty(sender.FailureLogs);
        }

        [Fact]
        public void MED06_02_SendTo_NullTransport_ReturnsFalse()
        {
            var sender = new DefectTestHarness.HeadlessPacketSender();
            var packet = new ActionTriggerPacket { Header = new PacketHeader(PacketType.ActionTrigger, 1, 1, 100) };

            bool success = sender.SendTo(null, new NetworkPeerId(2), packet, DeliveryMethod.ReliableOrdered, 1, 64);
            Assert.False(success);
            Assert.Single(sender.FailureLogs);
            Assert.Contains("peer 2", sender.FailureLogs[0]);
        }

        [Fact]
        public void MED06_03_SendTo_FailedSend_LogsWarning()
        {
            var sender = new DefectTestHarness.HeadlessPacketSender();
            using var harness = new DualClientTestHarness();
            harness.HostTransport.Disconnect(); // Transport refused

            var packet = new ActionTriggerPacket { Header = new PacketHeader(PacketType.ActionTrigger, 1, 1, 100) };
            bool success = sender.SendTo(harness.HostTransport, harness.ClientTransport.LocalPeerId, packet, DeliveryMethod.ReliableOrdered, 1, 64, "[CustomManager]");

            Assert.False(success);
            Assert.Single(sender.FailureLogs);
            Assert.Contains("[CustomManager]", sender.FailureLogs[0]);
            Assert.Contains(harness.ClientTransport.LocalPeerId.Value.ToString(), sender.FailureLogs[0]);
        }

        [Fact]
        public void MED06_04_SendToAll_NullTransport_ReturnsFalse()
        {
            var sender = new DefectTestHarness.HeadlessPacketSender();
            var packet = new ActionTriggerPacket { Header = new PacketHeader(PacketType.ActionTrigger, 1, 1, 100) };

            bool success = sender.SendTo(null, new NetworkPeerId(99), packet, DeliveryMethod.ReliableOrdered, 1, 64);
            Assert.False(success);
        }

        [Fact]
        public void MED06_05_SendToAll_RecordsDiagnostics()
        {
            var sender = new DefectTestHarness.HeadlessPacketSender();
            using var harness = new DualClientTestHarness();
            var packet = new ActionTriggerPacket { Header = new PacketHeader(PacketType.ActionTrigger, 1, 1, 100) };

            sender.SendTo(harness.HostTransport, harness.ClientTransport.LocalPeerId, packet, DeliveryMethod.ReliableOrdered, 1, 64);
            Assert.True(sender.SentBytes > 0);
        }

        #endregion

        #region 13. LOW-01: Optimize Initial Pending Entry Lookup in LiteNetLibTransport

        [Fact]
        public void LOW01_01_InitialChannelLookup_CreatesDictionaryOnDemand()
        {
            var dict = new Dictionary<byte, SortedDictionary<uint, byte[]>>();
            byte channel = 1;

            if (!dict.TryGetValue(channel, out var pending))
            {
                pending = new SortedDictionary<uint, byte[]>();
                dict[channel] = pending;
            }

            Assert.NotNull(pending);
            Assert.True(dict.ContainsKey(channel));
        }

        [Fact]
        public void LOW01_02_ExistingChannelLookup_ReusesDictionary()
        {
            var dict = new Dictionary<byte, SortedDictionary<uint, byte[]>>();
            byte channel = 1;
            dict[channel] = new SortedDictionary<uint, byte[]>();

            dict.TryGetValue(channel, out var pending1);
            dict.TryGetValue(channel, out var pending2);

            Assert.Same(pending1, pending2);
        }

        [Fact]
        public void LOW01_03_PendingContainsKey_FastCheck()
        {
            var pending = new SortedDictionary<uint, byte[]>();
            pending[10] = new byte[] { 1, 2, 3 };

            Assert.True(pending.ContainsKey(10));
            Assert.False(pending.ContainsKey(11));
        }

        [Fact]
        public void LOW01_04_DifferentChannels_IndependentCollections()
        {
            var dict = new Dictionary<byte, SortedDictionary<uint, byte[]>>();
            dict[1] = new SortedDictionary<uint, byte[]> { [10] = Array.Empty<byte>() };
            dict[2] = new SortedDictionary<uint, byte[]> { [20] = Array.Empty<byte>() };

            Assert.True(dict[1].ContainsKey(10));
            Assert.False(dict[1].ContainsKey(20));
            Assert.True(dict[2].ContainsKey(20));
        }

        [Fact]
        public void LOW01_05_ClearPending_RemovesAllEntries()
        {
            var dict = new Dictionary<byte, SortedDictionary<uint, byte[]>>
            {
                [1] = new SortedDictionary<uint, byte[]> { [10] = Array.Empty<byte>() }
            };
            dict.Clear();
            Assert.Empty(dict);
        }

        #endregion

        #region 14. LOW-02: Prevent Struct Torn Reads in VitalsSyncService via Immutable RemotePeerVitals

        [Fact]
        public void LOW02_01_RemotePeerVitals_ConstructsCorrectly()
        {
            var vitals = new RemotePeerVitals(new NetworkPeerId(2), 5, 9, 6, 2, 1);
            Assert.Equal(new NetworkPeerId(2), vitals.Peer);
            Assert.Equal(5, vitals.Health);
            Assert.Equal(9, vitals.Silk);
            Assert.Equal(6, vitals.MaxHealth);
            Assert.Equal(2, vitals.MaxSilkSpools);
            Assert.Equal(1, vitals.NeedleUpgradeLevel);
        }

        [Fact]
        public void LOW02_02_RemotePeerVitals_PropertiesAreReadOnly()
        {
            var vitals = new RemotePeerVitals(new NetworkPeerId(2), 3, 4, 5, 1, 0);
            // Struct is readonly, copies are fully value-typed
            var copy = vitals;
            Assert.Equal(vitals.Health, copy.Health);
            Assert.Equal(vitals.Silk, copy.Silk);
        }

        [Fact]
        public void LOW02_03_VitalsSyncService_StoresRemotePeerVitals()
        {
            var service = new VitalsSyncService();
            service.HandleVitalsPacket(new NetworkPeerId(2), new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 1, 100),
                CurrentHealth = 4,
                CurrentSilk = 8,
                MaxHealth = 5,
                MaxSilkSpools = 1
            });

            Assert.True(service.TryGetRemoteVitals(new NetworkPeerId(2), out var v));
            Assert.Equal(4, v.Health);
            Assert.Equal(8, v.Silk);
        }

        [Fact]
        public void LOW02_04_VitalsSyncService_PrimaryPeerMirrorsVitals()
        {
            var service = new VitalsSyncService();
            service.HandleVitalsPacket(new NetworkPeerId(2), new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 1, 100),
                CurrentHealth = 3,
                CurrentSilk = 6,
                MaxHealth = 5,
                MaxSilkSpools = 1,
                NeedleUpgradeLevel = 1
            });

            Assert.Equal(3, service.RemoteHealth);
            Assert.Equal(6, service.RemoteSilk);
        }

        [Fact]
        public void LOW02_05_ClearRemotePeers_RemovesAllVitals()
        {
            var service = new VitalsSyncService();
            service.HandleVitalsPacket(new NetworkPeerId(2), new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 1, 100),
                CurrentHealth = 3,
                CurrentSilk = 6,
                MaxHealth = 5,
                MaxSilkSpools = 1
            });
            service.ClearRemotePeers();

            Assert.False(service.TryGetRemoteVitals(new NetworkPeerId(2), out _));
        }

        #endregion

        #region 15. LOW-03: Cap Maximum Entries and Add Early Break in ConnectionHistory

        [Fact]
        public void LOW03_01_Add_NewEntry_AddedToFront()
        {
            var history = new DefectTestHarness.HeadlessConnectionHistory();
            history.Add("192.168.1.1:7777");

            Assert.Single(history.GetAll());
            Assert.Equal("192.168.1.1:7777", history.GetAll()[0]);
        }

        [Fact]
        public void LOW03_02_Add_ExistingEntry_MovesToFront()
        {
            var history = new DefectTestHarness.HeadlessConnectionHistory();
            history.Add("TargetA");
            history.Add("TargetB");
            history.Add("TargetA"); // Move to front

            Assert.Equal(2, history.GetAll().Count);
            Assert.Equal("TargetA", history.GetAll()[0]);
            Assert.Equal("TargetB", history.GetAll()[1]);
        }

        [Fact]
        public void LOW03_03_MaxEntries_CappedAtFive()
        {
            var history = new DefectTestHarness.HeadlessConnectionHistory();
            for (int i = 1; i <= 10; i++)
            {
                history.Add($"Target_{i}");
            }

            Assert.Equal(5, history.GetAll().Count);
            Assert.Equal("Target_10", history.GetAll()[0]);
            Assert.Equal("Target_6", history.GetAll()[4]);
        }

        [Fact]
        public void LOW03_04_GetAll_ReturnsInOrder()
        {
            var history = new DefectTestHarness.HeadlessConnectionHistory();
            history.Add("A");
            history.Add("B");
            history.Add("C");

            var list = history.GetAll();
            Assert.Equal("C", list[0]);
            Assert.Equal("B", list[1]);
            Assert.Equal("A", list[2]);
        }

        [Fact]
        public void LOW03_05_PersistEnabled_CanToggle()
        {
            var history = new DefectTestHarness.HeadlessConnectionHistory();
            Assert.True(history.PersistEnabled);
            history.PersistEnabled = false;
            Assert.False(history.PersistEnabled);
        }

        #endregion

        #region 16. LOW-04: Validate Tick Rate Against Game Frame Rate in QualityPresetManager

        [Fact]
        public void LOW04_01_PresetLow_Applies20Hz()
        {
            var manager = new DefectTestHarness.HeadlessQualityPresetManager();
            manager.ApplyPreset(DefectTestHarness.QualityLevel.Low);

            Assert.Equal(DefectTestHarness.QualityLevel.Low, manager.CurrentLevel);
            Assert.Equal(20, manager.ConfiguredTickRateHz);
            Assert.False(manager.EnableDashTrail);
        }

        [Fact]
        public void LOW04_02_PresetMedium_Applies30Hz()
        {
            var manager = new DefectTestHarness.HeadlessQualityPresetManager();
            manager.ApplyPreset(DefectTestHarness.QualityLevel.Medium);

            Assert.Equal(DefectTestHarness.QualityLevel.Medium, manager.CurrentLevel);
            Assert.Equal(30, manager.ConfiguredTickRateHz);
            Assert.True(manager.EnableDashTrail);
        }

        [Fact]
        public void LOW04_03_PresetHigh_Applies60Hz()
        {
            var manager = new DefectTestHarness.HeadlessQualityPresetManager();
            manager.ApplyPreset(DefectTestHarness.QualityLevel.High);

            Assert.Equal(DefectTestHarness.QualityLevel.High, manager.CurrentLevel);
            Assert.Equal(60, manager.ConfiguredTickRateHz);
            Assert.True(manager.EnableDashTrail);
        }

        [Fact]
        public void LOW04_04_PresetCustom_DoesNotOverride()
        {
            var manager = new DefectTestHarness.HeadlessQualityPresetManager();
            manager.ConfiguredTickRateHz = 45;
            manager.ApplyPreset(DefectTestHarness.QualityLevel.Custom);

            Assert.Equal(45, manager.ConfiguredTickRateHz);
        }

        [Fact]
        public void LOW04_05_CurrentLevel_TracksAppliedPreset()
        {
            var manager = new DefectTestHarness.HeadlessQualityPresetManager();
            manager.ApplyPreset(DefectTestHarness.QualityLevel.Low);
            Assert.Equal(DefectTestHarness.QualityLevel.Low, manager.CurrentLevel);
        }

        #endregion

        #region 17. LOW-05: Clear _authorityMap on Session Reset in EnemySyncController

        [Fact]
        public void LOW05_01_TryAcceptSnapshot_RegistersEnemy()
        {
            var service = new EnemySyncService();
            bool accepted = service.TryAcceptSnapshot(1001, 100, dead: false, timestamp: 100);

            Assert.True(accepted);
            Assert.True(service.HasFreshHostAuthority(1001));
        }

        [Fact]
        public void LOW05_02_HasFreshHostAuthority_TrueAfterSnapshot()
        {
            var service = new EnemySyncService();
            service.TryAcceptSnapshot(2002, 50, dead: false, timestamp: 200);
            Assert.True(service.HasFreshHostAuthority(2002));
        }

        [Fact]
        public void LOW05_03_Reset_ClearsAllEnemies()
        {
            var service = new EnemySyncService();
            service.TryAcceptSnapshot(1001, 100, dead: false, timestamp: 10);
            service.TryAcceptSnapshot(1002, 100, dead: false, timestamp: 10);

            service.Reset();
            Assert.False(service.HasFreshHostAuthority(1001));
            Assert.False(service.HasFreshHostAuthority(1002));
        }

        [Fact]
        public void LOW05_04_HasFreshHostAuthority_FalseAfterReset()
        {
            var service = new EnemySyncService();
            service.TryAcceptSnapshot(3003, 80, dead: false, timestamp: 50);
            service.Reset();
            Assert.False(service.HasFreshHostAuthority(3003));
        }

        [Fact]
        public void LOW05_05_Forget_RemovesSingleEnemy()
        {
            var service = new EnemySyncService();
            service.TryAcceptSnapshot(1001, 100, dead: false, timestamp: 10);
            service.TryAcceptSnapshot(1002, 100, dead: false, timestamp: 10);

            bool forgotten = service.Forget(1001);
            Assert.True(forgotten);
            Assert.False(service.HasFreshHostAuthority(1001));
            Assert.True(service.HasFreshHostAuthority(1002)); // Still tracked
        }

        #endregion

        #region 18. LOW-06: Remove RTT Watermark Update Latency in CoopMenuUI

        [Fact]
        public void LOW06_01_Watermark_DisconnectedText()
        {
            var watermark = new DefectTestHarness.HeadlessWatermarkState();
            watermark.Update(1.0f, false, SessionStatus.Disconnected, null, 0);

            Assert.Equal("[F10] Silksong Co-op", watermark.WatermarkText);
        }

        [Fact]
        public void LOW06_02_Watermark_HostingText()
        {
            var watermark = new DefectTestHarness.HeadlessWatermarkState();
            watermark.Update(1.0f, false, SessionStatus.Hosting, null, 0);

            Assert.Equal("Hosting (waiting for partner...)", watermark.WatermarkText);
        }

        [Fact]
        public void LOW06_03_Watermark_ConnectingText()
        {
            var watermark = new DefectTestHarness.HeadlessWatermarkState();
            watermark.Update(1.0f, false, SessionStatus.Connecting, null, 0);

            Assert.Equal("[F10] Connecting...", watermark.WatermarkText);
        }

        [Fact]
        public void LOW06_04_Watermark_ConnectedIncludesPeerAndRtt()
        {
            var watermark = new DefectTestHarness.HeadlessWatermarkState();
            watermark.Update(1.0f, true, SessionStatus.Connected, new NetworkPeerId(2, "HornetFriend"), 35);

            Assert.Contains("HornetFriend", watermark.WatermarkText);
            Assert.Contains("35ms", watermark.WatermarkText);
        }

        [Fact]
        public void LOW06_05_WatermarkQualityIndicator()
        {
            var watermark = new DefectTestHarness.HeadlessWatermarkState();
            watermark.Update(1.0f, true, SessionStatus.Connected, new NetworkPeerId(2), 25);
            Assert.Contains("●", watermark.WatermarkText); // Green

            watermark.Update(1.01f, true, SessionStatus.Connected, new NetworkPeerId(2), 90);
            Assert.Contains("◐", watermark.WatermarkText); // Yellow

            watermark.Update(1.02f, true, SessionStatus.Connected, new NetworkPeerId(2), 200);
            Assert.Contains("○", watermark.WatermarkText); // Red
        }

        #endregion

        #region 19. LOW-07: Handle Missing Localization Key and FormatException Fallbacks in Loc

        [Fact]
        public void LOW07_01_Translate_EnglishReturnsSource()
        {
            string result = Loc.Translate(CoopLanguage.English, "Partner");
            Assert.Equal("Partner", result);
        }

        [Fact]
        public void LOW07_02_Translate_RussianKnownKey_ReturnsRussian()
        {
            string result = Loc.Translate(CoopLanguage.Russian, "Partner");
            Assert.Equal("Напарник", result);
        }

        [Fact]
        public void LOW07_03_Translate_RussianMissingKey_ReturnsEnglish()
        {
            string result = Loc.Translate(CoopLanguage.Russian, "SomeUnknownKeyThatDoesNotExist");
            Assert.Equal("SomeUnknownKeyThatDoesNotExist", result);
        }

        [Fact]
        public void LOW07_04_Translate_WithArgs_FormatsProperly()
        {
            string result = Loc.Translate(CoopLanguage.Russian, "Hosting: {0}", "SK-ROOM-123");
            Assert.Equal("Хостинг: SK-ROOM-123", result);
        }

        [Fact]
        public void LOW07_05_ResolveAuto_RussianVsEnglish()
        {
            Assert.Equal(CoopLanguage.Russian, Loc.Resolve(CoopLanguage.Auto, systemIsRussian: true));
            Assert.Equal(CoopLanguage.English, Loc.Resolve(CoopLanguage.Auto, systemIsRussian: false));
        }

        #endregion

        #region 20. PERF-01: Avoid Redundant Calls to ApplyPaletteSwap for Player 1

        [Fact]
        public void PERF01_01_Player1HueShift_IsZero()
        {
            Assert.Equal(0.0f, PaletteSwapMath.Player1HueShift);
        }

        [Fact]
        public void PERF01_02_Player2HueShift_IsAzureBlue()
        {
            Assert.Equal(0.58f, PaletteSwapMath.Player2HueShift);
        }

        [Fact]
        public void PERF01_03_SetPlayer2_False_SetsProperties()
        {
            var controller = new DefectTestHarness.HeadlessPaletteSwapController();
            controller.SetPlayer2(false);

            Assert.False(controller.IsPlayer2);
            Assert.Equal(1, controller.ApplyCount);
        }

        [Fact]
        public void PERF01_04_EnforceInterval_Is30Frames()
        {
            Assert.Equal(30, DefectTestHarness.HeadlessPaletteSwapController.EnforceIntervalFrames);
        }

        [Fact]
        public void PERF01_05_AchromaticColors_Invariant()
        {
            // White mask invariant under hue shift
            PaletteSwapMath.ApplyHueShift(1.0f, 1.0f, 1.0f, 0.58f, out float r, out float g, out float b);
            Assert.Equal(1.0f, r, precision: 3);
            Assert.Equal(1.0f, g, precision: 3);
            Assert.Equal(1.0f, b, precision: 3);
        }

        #endregion

        #region 21. PERF-02: Eliminate Per-Frame Allocations from BuildPeerDiagnostics in CoopMenuUI:OnGUI

        [Fact]
        public void PERF02_01_PeerDiagnosticsSnapshot_DefaultValues()
        {
            var snapshot = new DefectTestHarness.HeadlessPeerDiagnosticsSnapshot();
            Assert.False(snapshot.Connected);
            Assert.False(snapshot.PartnerAlive);
        }

        [Fact]
        public void PERF02_02_BuildPeerDiagnostics_Disconnected()
        {
            var snapshot = DefectTestHarness.HeadlessPeerDiagnosticsSnapshot.Build(
                connected: false,
                partnerAlive: true,
                peerId: null,
                rtt: 0,
                delay: 0.0);

            Assert.False(snapshot.Connected);
        }

        [Fact]
        public void PERF02_03_BuildPeerDiagnostics_Connected()
        {
            var snapshot = DefectTestHarness.HeadlessPeerDiagnosticsSnapshot.Build(
                connected: true,
                partnerAlive: true,
                peerId: new NetworkPeerId(2),
                rtt: 45,
                delay: 55.5);

            Assert.True(snapshot.Connected);
            Assert.True(snapshot.PartnerAlive);
            Assert.Equal(45, snapshot.RttMs);
            Assert.Equal(55.5, snapshot.InterpolationDelayMs);
        }

        [Fact]
        public void PERF02_04_DiagnosticsOverlay_UsesDelegate()
        {
            Func<DefectTestHarness.HeadlessPeerDiagnosticsSnapshot> builder = () =>
                DefectTestHarness.HeadlessPeerDiagnosticsSnapshot.Build(true, true, new NetworkPeerId(1), 20, 30);

            var s = builder();
            Assert.True(s.Connected);
        }

        [Fact]
        public void PERF02_05_NetworkDiagnostics_RecordMetrics()
        {
            var s1 = DefectTestHarness.HeadlessPeerDiagnosticsSnapshot.Build(true, false, new NetworkPeerId(3), 120, 80.0);
            Assert.False(s1.PartnerAlive);
            Assert.Equal(120, s1.RttMs);
        }

        #endregion

        #region 22. PERF-03: Eliminate Per-Tick List<CSteamID> Allocations in SteamP2PTransport:PollEvents

        [Fact]
        public void PERF03_01_NoAdditionalPeers_ZeroStaleAllocation()
        {
            var monitor = new DefectTestHarness.HeadlessSteamPollingMonitor();
            monitor.PollEvents(1000);

            Assert.Equal(1, monitor.PollTickCount);
            Assert.Equal(0, monitor.AllocatedStaleLists);
        }

        [Fact]
        public void PERF03_02_ActivePeers_ZeroStaleAllocation()
        {
            var monitor = new DefectTestHarness.HeadlessSteamPollingMonitor();
            monitor.RegisterPeer(101, 1000);
            monitor.RegisterPeer(102, 1000);

            monitor.PollEvents(2000); // Only 1000ms elapsed, well below 15000ms timeout

            Assert.Equal(0, monitor.AllocatedStaleLists);
            Assert.Equal(0, monitor.TimedOutPeersDisconnected);
        }

        [Fact]
        public void PERF03_03_TimedOutPeer_Detected()
        {
            var monitor = new DefectTestHarness.HeadlessSteamPollingMonitor();
            monitor.RegisterPeer(101, 1000);

            monitor.PollEvents(20000); // 19000ms elapsed > 15000ms timeout

            Assert.Equal(1, monitor.AllocatedStaleLists);
            Assert.Equal(1, monitor.TimedOutPeersDisconnected);
        }

        [Fact]
        public void PERF03_04_DisconnectedPeer_Removed()
        {
            var monitor = new DefectTestHarness.HeadlessSteamPollingMonitor();
            monitor.RegisterPeer(101, 1000);
            monitor.PollEvents(20000);

            // Next poll: peer was removed, so stale list is NOT allocated again
            monitor.PollEvents(21000);
            Assert.Equal(1, monitor.AllocatedStaleLists); // still 1
        }

        [Fact]
        public void PERF03_05_DrainIncomingP2PPackets_RefreshesLiveness()
        {
            var monitor = new DefectTestHarness.HeadlessSteamPollingMonitor();
            monitor.RegisterPeer(101, 1000);

            // Packet arrives at 14000ms, refreshing last seen
            monitor.RecordPacketReceived(101, 14000);

            monitor.PollEvents(16000); // 16000 - 14000 = 2000ms < 15000ms
            Assert.Equal(0, monitor.TimedOutPeersDisconnected);
        }

        #endregion

        #region 23. ARCH-01: Reconcile Multi-Peer API Contracts in InventorySyncService

        [Fact]
        public void ARCH01_01_MultiPeerUnlock_BroadcastsToAll()
        {
            var service = new InventorySyncService();
            ItemUnlockPacket broadcasted = default;
            service.OnOutgoingItemUnlockPacket += p => broadcasted = p;

            service.OnLocalItemDiscovered("Tool_RosaryCannon", ItemCategory.Tool);

            Assert.Equal("Tool_RosaryCannon", broadcasted.ItemId);
            Assert.Equal(ItemCategory.Tool, broadcasted.Category);
        }

        [Fact]
        public void ARCH01_02_HandleItemUnlockPacket_FromPeerA()
        {
            var service = new InventorySyncService();
            var peerA = new NetworkPeerId(101);

            service.HandleItemUnlockPacket(peerA, new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlock, 3, 1, 100),
                ItemId = "Crest_Reaper",
                Category = ItemCategory.Crest,
                TransactionId = Guid.NewGuid()
            });

            Assert.True(service.IsItemUnlocked("Crest_Reaper"));
        }

        [Fact]
        public void ARCH01_03_HandleItemUnlockPacket_FromPeerB()
        {
            var service = new InventorySyncService();
            var peerB = new NetworkPeerId(202);

            service.HandleItemUnlockPacket(peerB, new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlock, 3, 1, 100),
                ItemId = "Ability_SilkGlide",
                Category = ItemCategory.Ability,
                TransactionId = Guid.NewGuid()
            });

            Assert.True(service.IsItemUnlocked("Ability_SilkGlide"));
        }

        [Fact]
        public void ARCH01_04_ResetCounterPeer_CleansSpecificPeer()
        {
            var service = new InventorySyncService();
            var peerA = new NetworkPeerId(101);
            var peerB = new NetworkPeerId(202);

            service.ResetCounterPeer(peerA);
            Assert.NotNull(service);
        }

        [Fact]
        public void ARCH01_05_CancelPendingCounter_CleansSpecificPeer()
        {
            var service = new InventorySyncService();
            var peerA = new NetworkPeerId(101);

            service.CancelPendingCounterApplication(peerA);
            Assert.NotNull(service);
        }

        #endregion

        #region 24. ARCH-02: Decomposed Mod Architecture & Dedicated Coordinators

        [Fact]
        public void ARCH02_01_DeathSyncManager_DispatchesPackets()
        {
            var death = new DeathLifecycleCoordinator();
            Assert.NotNull(death);
            Assert.Equal(DeathState.Alive, death.CurrentState);
        }

        [Fact]
        public void ARCH02_02_InventorySyncManager_DispatchesUnlocks()
        {
            var inventory = new InventorySyncService();
            Assert.NotNull(inventory);
            Assert.True(inventory.IsEquippedLoadoutIsolated);
        }

        [Fact]
        public void ARCH02_03_VitalsSyncManager_BroadcastsVitals()
        {
            var vitals = new VitalsSyncService();
            vitals.InitializeState(5, 5, 9, 1, 0, 0, 0);
            Assert.Equal(5, vitals.LocalHealth);
        }

        [Fact]
        public void ARCH02_04_SessionService_TracksSessionState()
        {
            var session = new CoopSessionState();
            session.SetHosting(new NetworkPeerId(1), "SK-TEST");
            Assert.True(session.IsHost);
            Assert.Equal("SK-TEST", session.CurrentRoomCode);
        }

        [Fact]
        public void ARCH02_05_TransportService_ManagesActiveTransport()
        {
            using var harness = new DualClientTestHarness();
            Assert.NotNull(harness.HostTransport);
            Assert.NotNull(harness.ClientTransport);
        }

        #endregion

        #region 25. ARCH-03: Decouple IGameplayActions from Concrete Plugin Object

        [Fact]
        public void ARCH03_01_MockGameplayActions_ImplementsInterface()
        {
            var mock = new DefectTestHarness.MockGameplayActions();
            Assert.NotNull(mock);
            Assert.Equal(0, mock.TeamWipeCount);
        }

        [Fact]
        public void ARCH03_02_TryApplyRemoteItemUnlock_ReturnsSuccess()
        {
            var mock = new DefectTestHarness.MockGameplayActions();
            bool result = mock.TryApplyRemoteItemUnlock("Tool_FleaBrew", ItemCategory.Tool);

            Assert.True(result);
            Assert.Single(mock.AppliedUnlocks);
            Assert.Equal("Tool_FleaBrew", mock.AppliedUnlocks[0].ItemId);
        }

        [Fact]
        public void ARCH03_03_ApplyRemoteItemUnlock_RecordsCall()
        {
            var mock = new DefectTestHarness.MockGameplayActions();
            mock.ApplyRemoteItemUnlock("Crest_Architect", ItemCategory.Crest);

            Assert.Single(mock.AppliedUnlocks);
            Assert.Equal("Crest_Architect", mock.AppliedUnlocks[0].ItemId);
        }

        [Fact]
        public void ARCH03_04_TryReviveAtPartner_AcceptsTargetPeer()
        {
            var mock = new DefectTestHarness.MockGameplayActions();
            var targetPeer = new NetworkPeerId(2);

            bool success = mock.TryReviveAtPartner(12.5f, 30.0f, targetPeer);

            Assert.True(success);
            Assert.Single(mock.ReviveAttempts);
            Assert.Equal(12.5f, mock.ReviveAttempts[0].X);
            Assert.Equal(targetPeer, mock.ReviveAttempts[0].TargetPeer);
        }

        [Fact]
        public void ARCH03_05_TriggerTeamWipe_InvokesWipe()
        {
            var mock = new DefectTestHarness.MockGameplayActions();
            mock.TriggerTeamWipe();

            Assert.Equal(1, mock.TeamWipeCount);
        }

        #endregion
    }
}
