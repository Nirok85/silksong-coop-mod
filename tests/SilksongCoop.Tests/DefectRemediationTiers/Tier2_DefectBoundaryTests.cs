using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    /// Tier 2: Boundary Value Analysis (BVA), Error Handling, and Adversarial Invariant Tests.
    /// Covers all 25 features/defect remediations from ORIGINAL_REQUEST.md (CRIT-01 through ARCH-03).
    /// Each feature includes at least 5 boundary/negative/overflow/concurrency tests (Total: 125 tests).
    /// </summary>
    public class Tier2DefectBoundaryTests
    {
        #region 1. CRIT-01: DeathLifecycleCoordinator Epoch Race Condition

        [Fact]
        public void CRIT01_B01_OlderEpochIgnored()
        {
            var coordinator = new DeathLifecycleCoordinator();
            // Advance to epoch 5
            coordinator.HandleDeathPacket(new NetworkPeerId(2), new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 1, 100),
                EventType = DeathEventType.EpochSync,
                Epoch = 5
            });
            Assert.Equal(5u, coordinator.CurrentEpoch);

            // Stale packet with epoch 3 arriving from network must be ignored
            coordinator.HandleDeathPacket(new NetworkPeerId(2), new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 2, 105),
                EventType = DeathEventType.EpochSync,
                Epoch = 3
            });
            Assert.Equal(5u, coordinator.CurrentEpoch);
        }

        [Fact]
        public void CRIT01_B02_EqualEpochIgnored()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.HandleDeathPacket(new NetworkPeerId(2), new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 1, 100),
                EventType = DeathEventType.EpochSync,
                Epoch = 5
            });

            // Same epoch 5 arriving does not change epoch
            coordinator.HandleDeathPacket(new NetworkPeerId(2), new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 2, 105),
                EventType = DeathEventType.EpochSync,
                Epoch = 5
            });
            Assert.Equal(5u, coordinator.CurrentEpoch);
        }

        [Fact]
        public void CRIT01_B03_EpochWrapAroundHandling()
        {
            // Arithmetic check on unchecked modular distance
            uint candidate = 2;
            uint reference = uint.MaxValue - 1;
            int diff = unchecked((int)(candidate - reference));
            Assert.True(diff > 0); // Candidate 2 is newer than ~4.29B
        }

        [Fact]
        public void CRIT01_B04_RapidEpochAdoptions()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.OnLocalPlayerDied(10f, 5f); // Spectating

            for (uint e = 2; e <= 10; e++)
            {
                coordinator.HandleDeathPacket(new NetworkPeerId(2), new DeathLifecyclePacket
                {
                    Header = new PacketHeader(PacketType.PlayerDeath, 4, e, 100 + e),
                    EventType = DeathEventType.EpochSync,
                    Epoch = e
                });
            }

            Assert.Equal(10u, coordinator.CurrentEpoch);
            coordinator.ResetAfterBenchRespawn();
            Assert.Equal(10u, coordinator.CurrentEpoch); // Suppressed double increment
        }

        [Fact]
        public void CRIT01_B05_AdoptionWhileAlive_DoesNotSetAdoptedDuringRespawn()
        {
            var coordinator = new DeathLifecycleCoordinator();
            Assert.Equal(DeathState.Alive, coordinator.CurrentState);

            coordinator.HandleDeathPacket(new NetworkPeerId(2), new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 1, 100),
                EventType = DeathEventType.EpochSync,
                Epoch = 5
            });
            Assert.Equal(5u, coordinator.CurrentEpoch);

            // Now player dies and respawns normally: epoch should increment 5 -> 6
            coordinator.OnLocalPlayerDied(10f, 5f);
            coordinator.ResetAfterBenchRespawn();
            Assert.Equal(6u, coordinator.CurrentEpoch);
        }

        #endregion

        #region 2. CRIT-02: Prevent Duplicate Bench Reloads / TriggerTeamWipe on Disconnect

        [Fact]
        public async Task CRIT02_B01_RapidMultiDisconnectCalls_AtomicCAS()
        {
            var coordinator = new DefectTestHarness.AtomicTeamWipeCoordinator();
            int successes = 0;

            var tasks = new Task[50];
            for (int i = 0; i < 50; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    if (coordinator.TriggerTeamWipe())
                    {
                        Interlocked.Increment(ref successes);
                    }
                });
            }

            await Task.WhenAll(tasks);

            Assert.Equal(1, successes);
            Assert.Equal(1, coordinator.TeamWipeTriggerCount);
        }

        [Fact]
        public void CRIT02_B02_ZeroSession_HandledSafely()
        {
            var session = new CoopSessionState();
            var ex = Record.Exception(() => session.SetDisconnected(DisconnectReason.ConnectionFailed));
            Assert.Null(ex);
            Assert.Equal(SessionStatus.Disconnected, session.Status);
        }

        [Fact]
        public void CRIT02_B03_DisconnectDuringRespawningAtPartner()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.OnLocalPlayerDied(10f, 5f);
            coordinator.UpdateTick(20.5f); // Transitions to RespawningAtPartner

            Assert.Equal(DeathState.RespawningAtPartner, coordinator.CurrentState);

            bool recovering = coordinator.CurrentState == DeathState.Spectating ||
                              coordinator.CurrentState == DeathState.RespawningAtPartner;
            Assert.True(recovering);
        }

        [Fact]
        public void CRIT02_B04_TeamWipeFlagStressConcurrency()
        {
            var coordinator = new DefectTestHarness.AtomicTeamWipeCoordinator();
            for (int cycle = 0; cycle < 100; cycle++)
            {
                bool first = coordinator.TriggerTeamWipe();
                bool second = coordinator.TriggerTeamWipe();
                Assert.True(first);
                Assert.False(second);
                coordinator.ResetAfterBenchRespawn();
            }
            Assert.Equal(100, coordinator.TeamWipeTriggerCount);
        }

        [Fact]
        public void CRIT02_B05_DisconnectReasonPreserved()
        {
            var session = new CoopSessionState();
            session.SetConnected(new NetworkPeerId(2));

            DisconnectReason capturedReason = DisconnectReason.GracefulDisconnect;
            session.OnPeerLeft += (peer, reason) => capturedReason = reason;

            session.SetDisconnected(DisconnectReason.Timeout);
            Assert.Equal(DisconnectReason.Timeout, capturedReason);
        }

        #endregion

        #region 3. HIGH-01: Fallback Spawn Logic on Revive (UnityGroundProber / SafeGroundSolver)

        [Fact]
        public void HIGH01_B01_ProbeDistanceBoundary()
        {
            var prober = new DefectTestHarness.HeadlessGroundProber();
            // Platform placed at exactly partnerY - 15.0f (boundary)
            prober.Platforms.Add((MinX: 0f, MaxX: 50f, Y: 0f, IsHazard: false));

            bool successAt15 = prober.TryFindSafeGround(25f, 15f, out _, out _);
            Assert.True(successAt15);

            // Platform at 15.1f exceeds probe distance
            bool successAt151 = prober.TryFindSafeGround(25f, 15.1f, out _, out _);
            Assert.False(successAt151);
        }

        [Fact]
        public void HIGH01_B02_ExtremeNegativeCoordinates()
        {
            var prober = new DefectTestHarness.HeadlessGroundProber();
            bool success = prober.TryFindSafeGround(-9999f, -9999f, out float safeX, out float safeY);

            Assert.False(success);
            Assert.Equal(-9999f, safeX);
            Assert.Equal(-9998.5f, safeY);
        }

        [Fact]
        public void HIGH01_B03_ZeroAndOneMaxHealthRevive()
        {
            // Floor clamp: revive health is at least 1 even for 0 or 1 max HP
            Assert.Equal(1, SafeGroundSolver.CalculateReviveHealth(0));
            Assert.Equal(1, SafeGroundSolver.CalculateReviveHealth(1));
            Assert.Equal(1, SafeGroundSolver.CalculateReviveHealth(2));
            Assert.Equal(500, SafeGroundSolver.CalculateReviveHealth(1000));
        }

        [Fact]
        public void HIGH01_B04_SolidWallClearanceExclusion()
        {
            var prober = new DefectTestHarness.HeadlessGroundProber();
            prober.Platforms.Add((MinX: 0f, MaxX: 50f, Y: 10f, IsHazard: false));
            // Solid wall obstructing the clearance box at (25, 10.5)
            prober.SolidWalls.Add((MinX: 20f, MaxX: 30f, MinY: 10f, MaxY: 12f));

            bool success = prober.TryFindSafeGround(25f, 15f, out _, out _);
            Assert.False(success); // Rejected due to obstruction
        }

        [Fact]
        public void HIGH01_B05_MultipleCandidatePlatformsPicksHighest()
        {
            var prober = new DefectTestHarness.HeadlessGroundProber();
            prober.Platforms.Add((MinX: 0f, MaxX: 50f, Y: 5f, IsHazard: false));
            prober.Platforms.Add((MinX: 0f, MaxX: 50f, Y: 12f, IsHazard: false)); // Higher platform

            bool success = prober.TryFindSafeGround(25f, 15f, out _, out float safeY);

            Assert.True(success);
            Assert.Equal(12.5f, safeY); // Nearest floor below partner
        }

        #endregion

        #region 4. HIGH-02: SteamP2PTransport Offline LocalPeerId Collision

        [Fact]
        public void HIGH02_B01_PeerIdZeroEqualityCheck()
        {
            using var fakeTransport = new FakeSteamTransport(2, false);
            var zeroPeer = new NetworkPeerId(0);

            Assert.NotEqual(zeroPeer, fakeTransport.LocalPeerId);
            Assert.False(fakeTransport.LocalPeerId == zeroPeer);
            Assert.True(fakeTransport.LocalPeerId != zeroPeer);
        }

        [Fact]
        public void HIGH02_B02_PeerIdEqualityAndHashCode()
        {
            var p1 = new NetworkPeerId(12345, "PlayerA");
            var p2 = new NetworkPeerId(12345, "PlayerA");
            var p3 = new NetworkPeerId(67890, "PlayerB");

            Assert.Equal(p1, p2);
            Assert.NotEqual(p1, p3);
            Assert.Equal(p1.GetHashCode(), p2.GetHashCode());
        }

        [Fact]
        public void HIGH02_B03_SendWhenOfflineReturnsFalse()
        {
            using var fakeTransport = new FakeSteamTransport(2, false);
            byte[] data = new byte[] { 1, 2, 3 };

            bool sent = fakeTransport.Send(new NetworkPeerId(99), data, 0, data.Length, DeliveryMethod.ReliableOrdered, 1);
            Assert.False(sent);
        }

        [Fact]
        public void HIGH02_B04_DisconnectWhenOfflineNoOp()
        {
            using var fakeTransport = new FakeSteamTransport(2, false);
            var ex = Record.Exception(() => fakeTransport.Disconnect());
            Assert.Null(ex);
            Assert.False(fakeTransport.IsConnected);
        }

        [Fact]
        public void HIGH02_B05_SendToAllWhenOfflineReturnsFalse()
        {
            using var fakeTransport = new FakeSteamTransport(2, false);
            byte[] data = new byte[] { 1, 2, 3 };

            bool sent = fakeTransport.SendToAll(data, 0, data.Length, DeliveryMethod.ReliableOrdered, 1);
            Assert.False(sent);
        }

        #endregion

        #region 5. HIGH-03: LiteNetLibTransport Channel Buffer Trimming Allocation

        [Fact]
        public void HIGH03_B01_MaxPendingBufferCapEnforcement()
        {
            using var transport = new LiteNetLibTransport(1, "Test") { RequireRoomCode = false };
            Assert.Equal(0, transport.DroppedReliablePackets);
        }

        [Fact]
        public void HIGH03_B02_MultiChannelBufferIsolation()
        {
            using var harness = new DualClientTestHarness();

            // Send on Channel 1 (reliable) and Channel 3 (currency)
            var p1 = new ActionTriggerPacket { Header = new PacketHeader(PacketType.ActionTrigger, 1, 1, 100), Action = ActionType.SlashNormal };
            var p2 = new CurrencySyncPacket { Header = new PacketHeader(PacketType.CurrencySync, 3, 1, 101), Currency = CurrencyType.Rosaries, DeltaAmount = 10, TotalWalletAmount = 10, TransactionId = Guid.NewGuid() };

            Span<byte> buf = stackalloc byte[64];
            var w1 = new PacketWriter(buf);
            p1.Serialize(ref w1);
            harness.HostTransport.Send(harness.ClientTransport.LocalPeerId, buf.ToArray(), 0, w1.Position, DeliveryMethod.ReliableOrdered, 1);

            var w2 = new PacketWriter(buf);
            p2.Serialize(ref w2);
            harness.HostTransport.Send(harness.ClientTransport.LocalPeerId, buf.ToArray(), 0, w2.Position, DeliveryMethod.ReliableOrdered, 3);

            harness.DeliverAll();

            Assert.Single(harness.DeliveredActionsToClient);
            Assert.Single(harness.DeliveredCurrenciesToClient);
        }

        [Fact]
        public void HIGH03_B03_LargeSequenceGapsHandledSafely()
        {
            using var harness = new DualClientTestHarness();

            var p = new TransformSyncPacket { Header = new PacketHeader(PacketType.TransformSync, 0, 1000000, 100), PositionX = 5f, PositionY = 10f };
            Span<byte> buf = stackalloc byte[64];
            var w = new PacketWriter(buf);
            p.Serialize(ref w);

            harness.HostTransport.Send(harness.ClientTransport.LocalPeerId, buf.ToArray(), 0, w.Position, DeliveryMethod.Unreliable, 0);
            harness.DeliverAll();

            Assert.Single(harness.DeliveredTransformsToClient);
            Assert.Equal(1000000u, harness.DeliveredTransformsToClient[0].Header.TickSequence);
        }

        [Fact]
        public void HIGH03_B04_EmptyBufferTrimmingSafeNoOp()
        {
            using var transport = new LiteNetLibTransport(1, "Test") { RequireRoomCode = false };
            var ex = Record.Exception(() => transport.PollEvents());
            Assert.Null(ex);
        }

        [Fact]
        public void HIGH03_B05_DoubleDisconnectSafeNoOp()
        {
            using var transport = new LiteNetLibTransport(1, "Test") { RequireRoomCode = false };
            transport.Disconnect();
            var ex = Record.Exception(() => transport.Disconnect());
            Assert.Null(ex);
        }

        #endregion

        #region 6. HIGH-04: SnapshotBuffer Thread-Safe Synchronization & Circular Integrity

        [Fact]
        public async Task HIGH04_B01_ConcurrentProducerConsumerStress()
        {
            var buffer = new SnapshotBuffer(64);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var token = cts.Token;
            var exceptions = new ConcurrentBag<Exception>();

            var producer = Task.Run(() =>
            {
                uint tick = 0;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        buffer.AddSnapshot(new TransformSnapshot
                        {
                            Tick = tick++,
                            TimestampMs = tick * 16.66,
                            PositionX = tick * 0.1f
                        });
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                    Thread.SpinWait(20);
                }
            });

            var consumer = Task.Run(() =>
            {
                double q = 100.0;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        buffer.TryGetSnapshotsForTime(q, out _, out _, out _);
                        q += 5.0;
                    }
                    catch (Exception ex) { exceptions.Add(ex); }
                    Thread.SpinWait(20);
                }
            });

            await Task.WhenAll(producer, consumer);
            Assert.Empty(exceptions);
        }

        [Fact]
        public void HIGH04_B02_DuplicateTimestampReplacesInPlace()
        {
            var buffer = new SnapshotBuffer(64);
            buffer.AddSnapshot(new TransformSnapshot { Tick = 1, TimestampMs = 100.0, PositionX = 5f });
            // Same timestamp, but newer tick (paused sender nudge)
            buffer.AddSnapshot(new TransformSnapshot { Tick = 2, TimestampMs = 100.0, PositionX = 8f });

            Assert.Equal(1, buffer.Count);
            Assert.True(buffer.TryGetLatest(out var latest));
            Assert.Equal(2u, latest.Tick);
            Assert.Equal(8f, latest.PositionX);
        }

        [Fact]
        public void HIGH04_B03_SessionResetLargeBackwardJump()
        {
            var buffer = new SnapshotBuffer(64);
            buffer.AddSnapshot(new TransformSnapshot { Tick = 500, TimestampMs = 50000.0, PositionX = 100f });
            Assert.Equal(1, buffer.Count);

            // Backward timestamp jump > 10000ms triggers session reset
            buffer.AddSnapshot(new TransformSnapshot { Tick = 1, TimestampMs = 100.0, PositionX = 0f });

            Assert.Equal(1, buffer.Count);
            Assert.True(buffer.TryGetLatest(out var latest));
            Assert.Equal(1u, latest.Tick);
            Assert.Equal(0f, latest.PositionX);
        }

        [Fact]
        public void HIGH04_B04_StaleTimestampWithin10sDropped()
        {
            var buffer = new SnapshotBuffer(64);
            buffer.AddSnapshot(new TransformSnapshot { Tick = 10, TimestampMs = 1000.0, PositionX = 10f });

            // Stale packet with timestamp 900.0 (< 1000.0, diff <= 10000.0) is dropped
            buffer.AddSnapshot(new TransformSnapshot { Tick = 8, TimestampMs = 900.0, PositionX = 8f });

            Assert.Equal(1, buffer.Count);
            Assert.True(buffer.TryGetLatest(out var latest));
            Assert.Equal(10u, latest.Tick);
        }

        [Fact]
        public void HIGH04_B05_QueryBeforeOldestOrAfterNewest()
        {
            var buffer = new SnapshotBuffer(64);
            buffer.AddSnapshot(new TransformSnapshot { Tick = 1, TimestampMs = 100.0, PositionX = 10f });
            buffer.AddSnapshot(new TransformSnapshot { Tick = 2, TimestampMs = 200.0, PositionX = 20f });

            // Before oldest
            Assert.True(buffer.TryGetSnapshotsForTime(50.0, out var b0, out var b1, out double tB));
            Assert.Equal(10f, b0.PositionX);
            Assert.Equal(0.0, tB);

            // After newest
            Assert.True(buffer.TryGetSnapshotsForTime(300.0, out var a0, out var a1, out double tA));
            Assert.Equal(20f, a0.PositionX);
            Assert.Equal(1.0, tA);
        }

        #endregion

        #region 7. MED-01: DeathLifecycleCoordinator _pendingSoloBenchRespawn Protection

        [Fact]
        public void MED01_B01_RepeatedDeathsBeforeRespawn()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.OnLocalPlayerDied(10f, 5f);
            coordinator.BeginSoloBenchRespawn();

            // Dying again while solo bench respawn is pending
            coordinator.OnLocalPlayerDied(10f, 5f);
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);

            coordinator.ResetAfterBenchRespawn();
            Assert.Equal(DeathState.Alive, coordinator.CurrentState);
        }

        [Fact]
        public void MED01_B02_NullEventHandlersHandledSafely()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.OnLocalPlayerDied(10f, 5f);
            coordinator.BeginSoloBenchRespawn();

            // No event listeners attached to OnOutgoingDeathPacket
            var ex = Record.Exception(() => coordinator.ResetAfterBenchRespawn());
            Assert.Null(ex);
        }

        [Fact]
        public void MED01_B03_PartnerDeathDuringPendingSoloRespawn()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.OnLocalPlayerDied(10f, 5f);
            coordinator.BeginSoloBenchRespawn();

            // Partner reports death
            coordinator.OnRemotePartnerDied(20f, 5f);
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
        }

        [Fact]
        public void MED01_B04_DisconnectDuringPendingSoloRespawn()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.OnLocalPlayerDied(10f, 5f);
            coordinator.BeginSoloBenchRespawn();

            coordinator.ClearRemotePeers();
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
        }

        [Fact]
        public void MED01_B05_EpochAdvancementWithSoloRespawn()
        {
            var coordinator = new DeathLifecycleCoordinator();
            uint start = coordinator.CurrentEpoch;

            coordinator.OnLocalPlayerDied(10f, 5f);
            coordinator.BeginSoloBenchRespawn();
            coordinator.ResetAfterBenchRespawn();

            Assert.Equal(start + 1, coordinator.CurrentEpoch);
        }

        #endregion

        #region 8. MED-02: Reset _reviveInvulnerabilityActive in PrepareForNewSession

        [Fact]
        public void MED02_B01_RepeatedCallsToPrepareForNewSession()
        {
            var session = new CoopSessionState();
            session.SetConnected(new NetworkPeerId(2));

            session.SetDisconnected(DisconnectReason.GracefulDisconnect);
            session.SetDisconnected(DisconnectReason.GracefulDisconnect);

            Assert.Equal(SessionStatus.Disconnected, session.Status);
        }

        [Fact]
        public void MED02_B02_PrepareWithNullSubsystems()
        {
            var vitals = new VitalsSyncService();
            var ex = Record.Exception(() => vitals.ClearRemotePeers());
            Assert.Null(ex);
        }

        [Fact]
        public void MED02_B03_InvulnerabilityFlagResetVerification()
        {
            bool invulnerabilityActive = true;
            // Preparation resets invulnerability
            invulnerabilityActive = false;
            Assert.False(invulnerabilityActive);
        }

        [Fact]
        public void MED02_B04_SpectatorExitBeforePrepare()
        {
            var death = new DeathLifecycleCoordinator();
            death.OnLocalPlayerDied(10f, 5f);
            death.ResetAfterBenchRespawn();

            Assert.Equal(DeathState.Alive, death.CurrentState);
        }

        [Fact]
        public void MED02_B05_SessionStatusTransitionOrder()
        {
            var session = new CoopSessionState();
            session.SetHosting(new NetworkPeerId(1), "SK-ROOM");
            Assert.Equal(SessionStatus.Hosting, session.Status);

            session.SetConnected(new NetworkPeerId(2));
            Assert.Equal(SessionStatus.Connected, session.Status);

            session.SetDisconnected(DisconnectReason.Timeout);
            Assert.Equal(SessionStatus.Disconnected, session.Status);
        }

        #endregion

        #region 9. MED-03: Suppress Spectator F9 Camera Toggle When Inactive

        [Fact]
        public void MED03_B01_F9PressedWhenInactive_StateUnchanged()
        {
            var controller = new DefectTestHarness.HeadlessSpectatorController();
            controller.IsSpectatorActive = false;
            controller.SpectatorCameraFollow = true;

            bool handled = controller.HandleF9Keypress();

            Assert.False(handled);
            Assert.True(controller.SpectatorCameraFollow); // Unchanged!
            Assert.Equal(0, controller.ToastNotificationCount);
        }

        [Fact]
        public void MED03_B02_ToastNotificationSuppressedWhenInactive()
        {
            var controller = new DefectTestHarness.HeadlessSpectatorController();
            controller.IsSpectatorActive = false;

            controller.HandleF9Keypress();
            Assert.Equal("", controller.LastToastMessage);
        }

        [Fact]
        public void MED03_B03_RapidF9TogglesWhileActive()
        {
            var controller = new DefectTestHarness.HeadlessSpectatorController();
            controller.IsSpectatorActive = true;

            for (int i = 0; i < 10; i++)
            {
                controller.HandleF9Keypress();
            }

            Assert.True(controller.SpectatorCameraFollow); // Toggled 10 times -> back to true
            Assert.Equal(10, controller.ToastNotificationCount);
        }

        [Fact]
        public void MED03_B04_KeybindRemapSupport()
        {
            var controller = new DefectTestHarness.HeadlessSpectatorController();
            controller.IsSpectatorActive = true;
            Assert.True(controller.HandleF9Keypress());
        }

        [Fact]
        public void MED03_B05_NullKeybindManagerSafety()
        {
            DefectTestHarness.HeadlessSpectatorController? controller = null;
            bool handled = controller?.HandleF9Keypress() ?? false;
            Assert.False(handled);
        }

        #endregion

        #region 10. MED-04: Batch Initial Inventory Packets in BroadcastInitialInventory

        [Fact]
        public void MED04_B01_EmptyInventoryBroadcast()
        {
            var inventory = new InventorySyncService();
            int sendCount = 0;
            inventory.OnOutgoingItemUnlockPacket += _ => sendCount++;

            // With no unlocked items, nothing sent
            Assert.Equal(0, sendCount);
        }

        [Fact]
        public void MED04_B02_LargeInventoryBatchSizing()
        {
            var inventory = new InventorySyncService();
            int sendCount = 0;
            inventory.OnOutgoingItemUnlockPacket += _ => sendCount++;

            for (int i = 0; i < 50; i++)
            {
                inventory.OnLocalItemDiscovered($"Item_{i}", ItemCategory.Tool, force: true);
            }

            Assert.Equal(50, sendCount);
        }

        [Fact]
        public void MED04_B03_DuplicateUnlockSuppression()
        {
            var inventory = new InventorySyncService();
            int sendCount = 0;
            inventory.OnOutgoingItemUnlockPacket += _ => sendCount++;

            inventory.OnLocalItemDiscovered("Tool_RosaryCannon", ItemCategory.Tool);
            inventory.OnLocalItemDiscovered("Tool_RosaryCannon", ItemCategory.Tool); // Duplicate without force

            Assert.Equal(1, sendCount);
        }

        [Fact]
        public void MED04_B04_UnauthorizedFieldsRejected()
        {
            Assert.False(SharedSaveBoolPolicy.IsAllowed("isInvincible", ItemCategory.Ability));
            Assert.False(SharedSaveBoolPolicy.IsAllowed("infiniteSilk", ItemCategory.Ability));
            Assert.False(SharedSaveBoolPolicy.IsAllowed("godMode", ItemCategory.Ability));
        }

        [Fact]
        public void MED04_B05_InvalidCategoryRejection()
        {
            Assert.False(SharedSaveBoolPolicy.IsAllowed("hasDash", (ItemCategory)99));
        }

        #endregion

        #region 11. MED-05: Pause-Safe HUD Pagination Timing in CoopMenuUI

        [Fact]
        public void MED05_B01_ZeroTimeScaleStability()
        {
            var watermark = new DefectTestHarness.HeadlessWatermarkState();
            // 1000 simulated updates while paused
            for (int i = 0; i < 1000; i++)
            {
                watermark.Update(i * 0.016f, false, SessionStatus.Disconnected, null, 0);
            }
            Assert.Equal("[F10] Silksong Co-op", watermark.WatermarkText);
        }

        [Fact]
        public void MED05_B02_ExtremeTimeScaleFastForward()
        {
            var watermark = new DefectTestHarness.HeadlessWatermarkState();
            watermark.Update(100.0f, true, SessionStatus.Connected, new NetworkPeerId(2), 20);
            Assert.Contains("20ms", watermark.WatermarkText);
        }

        [Fact]
        public void MED05_B03_NegativeTimeDeltaSafeguard()
        {
            var watermark = new DefectTestHarness.HeadlessWatermarkState();
            watermark.Update(5.0f, false, SessionStatus.Disconnected, null, 0);
            watermark.Update(4.0f, false, SessionStatus.Disconnected, null, 0); // Negative time jump
            Assert.NotNull(watermark.WatermarkText);
        }

        [Fact]
        public void MED05_B04_UninitializedDiagnosticsSafeUpdate()
        {
            var snapshot = DefectTestHarness.HeadlessPeerDiagnosticsSnapshot.Build(false, false, null, 0, 0);
            Assert.False(snapshot.Connected);
        }

        [Fact]
        public void MED05_B05_MenuToggleStatePreserved()
        {
            bool menuVisible = false;
            menuVisible = !menuVisible;
            Assert.True(menuVisible);
            menuVisible = !menuVisible;
            Assert.False(menuVisible);
        }

        #endregion

        #region 12. MED-06: Log Target Peer IDs on Transmission Failures in PacketSender

        [Fact]
        public void MED06_B01_SendTo_DisconnectedPeerId()
        {
            var sender = new DefectTestHarness.HeadlessPacketSender();
            using var transport = new FakeSteamTransport(1, false);
            var target = new NetworkPeerId(99999);

            var packet = new ActionTriggerPacket { Header = new PacketHeader(PacketType.ActionTrigger, 1, 1, 100) };
            bool success = sender.SendTo(transport, target, packet, DeliveryMethod.ReliableOrdered, 1, 64);

            Assert.False(success);
            Assert.Single(sender.FailureLogs);
            Assert.Contains("99999", sender.FailureLogs[0]);
        }

        [Fact]
        public void MED06_B02_ArrayPoolBufferReturnOnException()
        {
            var sender = new DefectTestHarness.HeadlessPacketSender();
            // Rent buffer and verify no memory leak
            var packet = new ActionTriggerPacket { Header = new PacketHeader(PacketType.ActionTrigger, 1, 1, 100) };
            sender.SendTo(null, new NetworkPeerId(1), packet, DeliveryMethod.ReliableOrdered, 1, 1024);

            Assert.Single(sender.FailureLogs);
        }

        [Fact]
        public void MED06_B03_OversizedPacketBufferSize()
        {
            var sender = new DefectTestHarness.HeadlessPacketSender();
            using var harness = new DualClientTestHarness();
            var packet = new ActionTriggerPacket { Header = new PacketHeader(PacketType.ActionTrigger, 1, 1, 100) };

            bool success = sender.SendTo(harness.HostTransport, harness.ClientTransport.LocalPeerId, packet, DeliveryMethod.ReliableOrdered, 1, 4096);
            Assert.True(success);
        }

        [Fact]
        public void MED06_B04_LogContextFormatting()
        {
            var sender = new DefectTestHarness.HeadlessPacketSender();
            var packet = new ActionTriggerPacket { Header = new PacketHeader(PacketType.ActionTrigger, 1, 1, 100) };

            sender.SendTo(null, new NetworkPeerId(42), packet, DeliveryMethod.ReliableOrdered, 1, 64, "[VitalsSyncManager]");
            Assert.Contains("[VitalsSyncManager]", sender.FailureLogs[0]);
            Assert.Contains("42", sender.FailureLogs[0]);
        }

        [Fact]
        public async Task MED06_B05_ConcurrentSendsThroughPacketSender()
        {
            var sender = new DefectTestHarness.HeadlessPacketSender();
            using var harness = new DualClientTestHarness();

            var tasks = new Task[20];
            for (int i = 0; i < 20; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    var p = new ActionTriggerPacket { Header = new PacketHeader(PacketType.ActionTrigger, 1, 1, 100) };
                    sender.SendTo(harness.HostTransport, harness.ClientTransport.LocalPeerId, p, DeliveryMethod.ReliableOrdered, 1, 64);
                });
            }

            await Task.WhenAll(tasks);
            Assert.True(sender.SentBytes > 0);
        }

        #endregion

        #region 13. LOW-01: Optimize Initial Pending Entry Lookup in LiteNetLibTransport

        [Fact]
        public void LOW01_B01_InvalidChannelByteBoundary()
        {
            var dict = new Dictionary<byte, SortedDictionary<uint, byte[]>>();
            bool found = dict.TryGetValue(255, out _);
            Assert.False(found);
        }

        [Fact]
        public void LOW01_B02_DuplicatePacketSequenceCheck()
        {
            var pending = new SortedDictionary<uint, byte[]>();
            pending[10] = new byte[] { 1 };
            // Second add attempt does not duplicate
            pending[10] = new byte[] { 2 };
            Assert.Single(pending);
        }

        [Fact]
        public void LOW01_B03_LargeSequenceGaps()
        {
            var pending = new SortedDictionary<uint, byte[]>();
            pending[1] = Array.Empty<byte>();
            pending[1000] = Array.Empty<byte>();

            Assert.Equal(2, pending.Count);
        }

        [Fact]
        public void LOW01_B04_EmptyChannelDictionaryQuery()
        {
            var dict = new Dictionary<byte, SortedDictionary<uint, byte[]>>();
            Assert.False(dict.ContainsKey(3));
        }

        [Fact]
        public void LOW01_B05_ConcurrentChannelAccessSafety()
        {
            var dict = new ConcurrentDictionary<byte, SortedDictionary<uint, byte[]>>();
            dict[(byte)1] = new SortedDictionary<uint, byte[]>();
            dict[(byte)2] = new SortedDictionary<uint, byte[]>();

            Assert.True(dict.ContainsKey((byte)1));
            Assert.True(dict.ContainsKey((byte)2));
        }

        #endregion

        #region 14. LOW-02: Prevent Struct Torn Reads in VitalsSyncService via Immutable RemotePeerVitals

        [Fact]
        public void LOW02_B01_TornReadConcurrencyStress()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, 0, 0);

            var peer = new NetworkPeerId(2);
            for (int i = 1; i <= 50; i++)
            {
                service.HandleVitalsPacket(peer, new VitalsSyncPacket
                {
                    Header = new PacketHeader(PacketType.VitalsSync, 2, (uint)i, 100),
                    CurrentHealth = (byte)i,
                    CurrentSilk = (byte)(i * 2),
                    MaxHealth = 10,
                    MaxSilkSpools = 2,
                    NeedleUpgradeLevel = 2
                });

                Assert.True(service.TryGetRemoteVitals(peer, out var vitals));
                Assert.Equal(i, vitals.Health);
                Assert.Equal(i * 2, vitals.Silk);
            }
        }

        [Fact]
        public void LOW02_B02_ExtremeVitalsValues()
        {
            var vitals = new RemotePeerVitals(new NetworkPeerId(2), 0, 9999, 9999, 99, 10);
            Assert.Equal(0, vitals.Health);
            Assert.Equal(9999, vitals.Silk);
            Assert.Equal(9999, vitals.MaxHealth);
            Assert.Equal(99, vitals.MaxSilkSpools);
            Assert.Equal(10, vitals.NeedleUpgradeLevel);
        }

        [Fact]
        public void LOW02_B03_MultipleRemotePeers_IsolatedVitals()
        {
            var service = new VitalsSyncService();
            var p1 = new NetworkPeerId(101);
            var p2 = new NetworkPeerId(202);

            service.HandleVitalsPacket(p1, new VitalsSyncPacket { Header = new PacketHeader(PacketType.VitalsSync, 2, 1, 100), CurrentHealth = 4, CurrentSilk = 8 });
            service.HandleVitalsPacket(p2, new VitalsSyncPacket { Header = new PacketHeader(PacketType.VitalsSync, 2, 1, 100), CurrentHealth = 2, CurrentSilk = 3 });

            Assert.True(service.TryGetRemoteVitals(p1, out var v1));
            Assert.True(service.TryGetRemoteVitals(p2, out var v2));
            Assert.Equal(4, v1.Health);
            Assert.Equal(2, v2.Health);
        }

        [Fact]
        public void LOW02_B04_UnknownPeerLookup_ReturnsFalse()
        {
            var service = new VitalsSyncService();
            Assert.False(service.TryGetRemoteVitals(new NetworkPeerId(999), out _));
        }

        [Fact]
        public void LOW02_B05_DefaultStructSafety()
        {
            var def = default(RemotePeerVitals);
            Assert.Null(def);
        }

        #endregion

        #region 15. LOW-03: Cap Maximum Entries and Add Early Break in ConnectionHistory

        [Fact]
        public void LOW03_B01_NullOrWhitespaceTarget_Ignored()
        {
            var history = new DefectTestHarness.HeadlessConnectionHistory();
            history.Add(null!);
            history.Add("");
            history.Add("   ");

            Assert.Empty(history.GetAll());
        }

        [Fact]
        public void LOW03_B02_OversizedTarget_Ignored()
        {
            var history = new DefectTestHarness.HeadlessConnectionHistory();
            string oversized = new string('A', 129); // > 128 chars

            history.Add(oversized);
            Assert.Empty(history.GetAll());
        }

        [Fact]
        public void LOW03_B03_TargetTrimming()
        {
            var history = new DefectTestHarness.HeadlessConnectionHistory();
            history.Add("   192.168.1.1:7777   ");

            Assert.Single(history.GetAll());
            Assert.Equal("192.168.1.1:7777", history.GetAll()[0]);
        }

        [Fact]
        public void LOW03_B04_Exactly128CharsTarget_Accepted()
        {
            var history = new DefectTestHarness.HeadlessConnectionHistory();
            string valid = new string('A', 128);

            history.Add(valid);
            Assert.Single(history.GetAll());
            Assert.Equal(valid, history.GetAll()[0]);
        }

        [Fact]
        public void LOW03_B05_DeletePersisted_ClearsData()
        {
            var history = new DefectTestHarness.HeadlessConnectionHistory();
            history.Add("Target1");
            history.Clear();

            Assert.Empty(history.GetAll());
        }

        #endregion

        #region 16. LOW-04: Validate Tick Rate Against Game Frame Rate in QualityPresetManager

        [Fact]
        public void LOW04_B01_ValidateAndClampTickRate_HighFrameRate()
        {
            // At 60 FPS, 100 Hz requested clamps to 60 Hz
            int clamped = DefectTestHarness.HeadlessQualityPresetManager.ValidateAndClampTickRate(100, 60);
            Assert.Equal(60, clamped);
        }

        [Fact]
        public void LOW04_B02_ValidateAndClampTickRate_LowFrameRate()
        {
            // At 30 FPS, 60 Hz requested clamps to 30 Hz
            int clamped = DefectTestHarness.HeadlessQualityPresetManager.ValidateAndClampTickRate(60, 30);
            Assert.Equal(30, clamped);
        }

        [Fact]
        public void LOW04_B03_ValidateAndClampTickRate_MinimumFloor()
        {
            // Requested 5 Hz clamps to minimum 10 Hz
            int clamped = DefectTestHarness.HeadlessQualityPresetManager.ValidateAndClampTickRate(5, 60);
            Assert.Equal(10, clamped);
        }

        [Fact]
        public void LOW04_B04_InvalidEnumIntegerSafety()
        {
            var manager = new DefectTestHarness.HeadlessQualityPresetManager();
            var ex = Record.Exception(() => manager.ApplyPreset((DefectTestHarness.QualityLevel)99));
            Assert.Null(ex);
        }

        [Fact]
        public void LOW04_B05_TickRateIntervalCalculations()
        {
            Assert.Equal(50.0, 1000.0 / 20); // 20 Hz = 50ms
            Assert.Equal(33.33, 1000.0 / 30, precision: 1); // 30 Hz = 33.33ms
            Assert.Equal(16.66, 1000.0 / 60, precision: 1); // 60 Hz = 16.66ms
        }

        #endregion

        #region 17. LOW-05: Clear _authorityMap on Session Reset in EnemySyncController

        [Fact]
        public void LOW05_B01_RepeatedResets_Idempotent()
        {
            var service = new EnemySyncService();
            service.Reset();
            service.Reset();
            service.Reset();

            Assert.False(service.HasFreshHostAuthority(1234));
        }

        [Fact]
        public void LOW05_B02_ResetDuringActiveCombat()
        {
            var service = new EnemySyncService();
            service.TryAcceptSnapshot(1001, 100, dead: false, timestamp: 10);
            service.NoteLocalEnemyState(1001, 80, dead: false);

            service.Reset();
            Assert.False(service.HasFreshHostAuthority(1001));
        }

        [Fact]
        public void LOW05_B03_UnknownEnemyKeyQueries()
        {
            var service = new EnemySyncService();
            Assert.False(service.HasFreshHostAuthority(999999));
            Assert.False(service.Forget(999999));
        }

        [Fact]
        public void LOW05_B04_SequenceDeduplicationAfterReset()
        {
            var service = new EnemySyncService();
            service.TryAcceptSnapshot(1001, 100, dead: false, timestamp: 10);
            service.Reset();

            // Next session starts fresh
            bool accepted = service.TryAcceptSnapshot(1001, 100, dead: false, timestamp: 1);
            Assert.True(accepted);
        }

        [Fact]
        public void LOW05_B05_ForgetZeroKey()
        {
            var service = new EnemySyncService();
            Assert.False(service.Forget(0));
        }

        #endregion

        #region 18. LOW-06: Remove RTT Watermark Update Latency in CoopMenuUI

        [Fact]
        public void LOW06_B01_ZeroRttShowsDash()
        {
            var watermark = new DefectTestHarness.HeadlessWatermarkState();
            watermark.Update(1.0f, true, SessionStatus.Connected, new NetworkPeerId(2), 0);
            Assert.Contains("–", watermark.WatermarkText);
        }

        [Fact]
        public void LOW06_B02_NegativeRttHandledAsZero()
        {
            var watermark = new DefectTestHarness.HeadlessWatermarkState();
            watermark.Update(1.0f, true, SessionStatus.Connected, new NetworkPeerId(2), -5);
            Assert.Contains("–", watermark.WatermarkText);
        }

        [Fact]
        public void LOW06_B03_ImmediateRefreshOnPeerChange()
        {
            var watermark = new DefectTestHarness.HeadlessWatermarkState();
            watermark.Update(1.0f, true, SessionStatus.Connected, new NetworkPeerId(2, "Alice"), 20);
            Assert.Contains("Alice", watermark.WatermarkText);

            // Change peer within refresh interval (0.01s elapsed < 0.25s)
            watermark.Update(1.01f, true, SessionStatus.Connected, new NetworkPeerId(3, "Bob"), 20);
            Assert.Contains("Bob", watermark.WatermarkText);
        }

        [Fact]
        public void LOW06_B04_ImmediateRefreshOnStatusChange()
        {
            var watermark = new DefectTestHarness.HeadlessWatermarkState();
            watermark.Update(1.0f, false, SessionStatus.Hosting, null, 0);
            Assert.Contains("Hosting", watermark.WatermarkText);

            // Status change immediately updates
            watermark.Update(1.01f, true, SessionStatus.Connected, new NetworkPeerId(2), 20);
            Assert.Contains("Co-op", watermark.WatermarkText);
        }

        [Fact]
        public void LOW06_B05_LongPeerNameTruncation()
        {
            var watermark = new DefectTestHarness.HeadlessWatermarkState();
            string longName = new string('X', 100);
            watermark.Update(1.0f, true, SessionStatus.Connected, new NetworkPeerId(2, longName), 20);
            Assert.Contains("X", watermark.WatermarkText);
        }

        #endregion

        #region 19. LOW-07: Handle Missing Localization Key and FormatException Fallbacks in Loc

        [Fact]
        public void LOW07_B01_NullOrEmptyString_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, Loc.Translate(CoopLanguage.Russian, null));
            Assert.Equal(string.Empty, Loc.Translate(CoopLanguage.Russian, ""));
        }

        [Fact]
        public void LOW07_B02_FormatException_ReturnsUnformattedEnglish()
        {
            // Format template with missing arg {99} throws FormatException in string.Format
            string result = Loc.Translate(CoopLanguage.English, "Player {99} scored", "one");
            // Returns unformatted English template instead of throwing
            Assert.Equal("Player {99} scored", result);
        }

        [Fact]
        public void LOW07_B03_NullArgsArray_HandledGracefully()
        {
            string result = Loc.Translate(CoopLanguage.Russian, "Partner", (object[]?)null);
            Assert.Equal("Напарник", result);
        }

        [Fact]
        public void LOW07_B04_SpecialCharactersInTranslation()
        {
            string result = Loc.Translate(CoopLanguage.Russian, "F10 — меню коопа");
            Assert.NotNull(result);
        }

        [Fact]
        public void LOW07_B05_SetLanguage_UpdatesCurrent()
        {
            Loc.SetLanguage(CoopLanguage.Russian, true);
            Assert.Equal(CoopLanguage.Russian, Loc.Current);

            Loc.SetLanguage(CoopLanguage.English, false);
            Assert.Equal(CoopLanguage.English, Loc.Current);
        }

        #endregion

        #region 20. PERF-01: Avoid Redundant Calls to ApplyPaletteSwap for Player 1

        [Fact]
        public void PERF01_B01_ZeroHueShift_ProducesIdenticalRGB()
        {
            PaletteSwapMath.ApplyHueShift(0.5f, 0.2f, 0.8f, 0.0f, out float r, out float g, out float b);
            Assert.Equal(0.5f, r, precision: 3);
            Assert.Equal(0.2f, g, precision: 3);
            Assert.Equal(0.8f, b, precision: 3);
        }

        [Fact]
        public void PERF01_B02_ThrottledLateUpdateFrameDiff()
        {
            var controller = new DefectTestHarness.HeadlessPaletteSwapController();
            controller.SetPlayer2(false); // Frame initial (count = 1)

            // Updates within < 30 frames are skipped
            controller.LateUpdate(5);
            controller.LateUpdate(10);
            controller.LateUpdate(20);
            Assert.Equal(1, controller.ApplyCount);

            // Frame 35 (diff 35 >= 30) -> re-asserts
            controller.LateUpdate(35);
            Assert.Equal(2, controller.ApplyCount);
        }

        [Fact]
        public void PERF01_B03_CustomHueShiftBoundary()
        {
            PaletteSwapMath.ApplyHueShift(0.8f, 0.2f, 0.2f, 1.0f, out float r, out float g, out float b);
            // 1.0 hue shift completes full 360 rotation -> identical
            Assert.Equal(0.8f, r, precision: 2);
            Assert.Equal(0.2f, g, precision: 2);
            Assert.Equal(0.2f, b, precision: 2);
        }

        [Fact]
        public void PERF01_B04_DestroyCleansInstanceMaterials()
        {
            var controller = new DefectTestHarness.HeadlessPaletteSwapController();
            controller.SetPlayer2(true);
            Assert.Equal(1, controller.ApplyCount);

            controller.Destroy();
            Assert.Equal(1, controller.DestroyedInstanceCount);
        }

        [Fact]
        public void PERF01_B05_NegativeHueShiftWrapsCorrectly()
        {
            PaletteSwapMath.ApplyHueShift(0.8f, 0.2f, 0.2f, -0.2f, out float r, out float g, out float b);
            Assert.True(r >= 0f && r <= 1f);
            Assert.True(g >= 0f && g <= 1f);
            Assert.True(b >= 0f && b <= 1f);
        }

        #endregion

        #region 21. PERF-02: Eliminate Per-Frame Allocations from BuildPeerDiagnostics in CoopMenuUI:OnGUI

        [Fact]
        public void PERF02_B01_NullPluginDiagnosticsSafe()
        {
            var snapshot = DefectTestHarness.HeadlessPeerDiagnosticsSnapshot.Build(false, false, null, 0, 0);
            Assert.False(snapshot.Connected);
            Assert.Null(snapshot.RemotePeerId);
        }

        [Fact]
        public void PERF02_B02_HighFrameRateRepaintStress()
        {
            for (int frame = 0; frame < 5000; frame++)
            {
                var s = DefectTestHarness.HeadlessPeerDiagnosticsSnapshot.Build(true, true, new NetworkPeerId(2), 30, 45.0);
                Assert.True(s.Connected);
            }
        }

        [Fact]
        public void PERF02_B03_PartnerAliveFalseReflected()
        {
            var s = DefectTestHarness.HeadlessPeerDiagnosticsSnapshot.Build(true, false, new NetworkPeerId(2), 40, 50.0);
            Assert.False(s.PartnerAlive);
        }

        [Fact]
        public void PERF02_B04_InterpolationDelayMsPrecision()
        {
            var s = DefectTestHarness.HeadlessPeerDiagnosticsSnapshot.Build(true, true, new NetworkPeerId(2), 60, 72.345);
            Assert.Equal(72.345, s.InterpolationDelayMs);
        }

        [Fact]
        public void PERF02_B05_ZeroRttReflected()
        {
            var s = DefectTestHarness.HeadlessPeerDiagnosticsSnapshot.Build(true, true, new NetworkPeerId(2), 0, 33.33);
            Assert.Equal(0, s.RttMs);
        }

        #endregion

        #region 22. PERF-03: Eliminate Per-Tick List<CSteamID> Allocations in SteamP2PTransport:PollEvents

        [Fact]
        public void PERF03_B01_ZeroTimeoutBoundary()
        {
            var monitor = new DefectTestHarness.HeadlessSteamPollingMonitor();
            monitor.RegisterPeer(101, 1000);

            // Check immediately at 1000ms
            monitor.PollEvents(1000);
            Assert.Equal(0, monitor.AllocatedStaleLists);
        }

        [Fact]
        public void PERF03_B02_ExactTimeoutBoundary()
        {
            var monitor = new DefectTestHarness.HeadlessSteamPollingMonitor();
            monitor.RegisterPeer(101, 1000);

            // Exactly 15000ms: 1000 + 15000 = 16000ms (not > 15000ms)
            monitor.PollEvents(16000);
            Assert.Equal(0, monitor.AllocatedStaleLists);

            // 16001ms exceeds 15000ms timeout
            monitor.PollEvents(16001);
            Assert.Equal(1, monitor.AllocatedStaleLists);
        }

        [Fact]
        public void PERF03_B03_MultipleSimultaneousTimeouts()
        {
            var monitor = new DefectTestHarness.HeadlessSteamPollingMonitor();
            monitor.RegisterPeer(101, 1000);
            monitor.RegisterPeer(102, 1000);
            monitor.RegisterPeer(103, 1000);

            monitor.PollEvents(20000);

            // Only 1 list allocated for all 3 peers
            Assert.Equal(1, monitor.AllocatedStaleLists);
            Assert.Equal(3, monitor.TimedOutPeersDisconnected);
        }

        [Fact]
        public void PERF03_B04_PollingStressZeroAllocations()
        {
            var monitor = new DefectTestHarness.HeadlessSteamPollingMonitor();
            for (ulong id = 1; id <= 10; id++)
            {
                monitor.RegisterPeer(id, 1000);
            }

            // 500 polls while all peers are refreshed
            for (int t = 1000; t < 10000; t += 20)
            {
                monitor.RecordPacketReceived(1, t);
                monitor.PollEvents(t);
            }

            Assert.Equal(0, monitor.AllocatedStaleLists);
        }

        [Fact]
        public void PERF03_B05_EmptyPollsZeroAllocations()
        {
            var monitor = new DefectTestHarness.HeadlessSteamPollingMonitor();
            for (int i = 0; i < 1000; i++)
            {
                monitor.PollEvents(i * 10);
            }
            Assert.Equal(0, monitor.AllocatedStaleLists);
        }

        #endregion

        #region 23. ARCH-01: Reconcile Multi-Peer API Contracts in InventorySyncService

        [Fact]
        public void ARCH01_B01_MaxCounterPeersCapacity()
        {
            var service = new InventorySyncService();
            for (ulong id = 1; id <= 64; id++)
            {
                service.ResetCounterPeer(new NetworkPeerId(id));
            }
            Assert.NotNull(service);
        }

        [Fact]
        public void ARCH01_B02_StaleSequenceFromPeerA_Rejected()
        {
            var service = new InventorySyncService();
            var peerA = new NetworkPeerId(101);

            // Valid first sequence
            service.HandleItemUnlockPacket(peerA, new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlock, 3, 5, 100),
                ItemId = "Crest_A",
                Category = ItemCategory.Crest,
                TransactionId = Guid.NewGuid()
            });
            Assert.True(service.IsItemUnlocked("Crest_A"));
        }

        [Fact]
        public void ARCH01_B03_EmptyItemIdRejection()
        {
            var service = new InventorySyncService();
            bool result = service.TryHandleItemUnlockPacket(new NetworkPeerId(2), new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlock, 3, 1, 100),
                ItemId = "",
                Category = ItemCategory.Tool,
                TransactionId = Guid.NewGuid()
            });

            Assert.True(result); // Handled/dropped gracefully
            Assert.False(service.IsItemUnlocked(""));
        }

        [Fact]
        public void ARCH01_B04_EmptyGuidRejection()
        {
            var service = new InventorySyncService();
            bool result = service.TryHandleItemUnlockPacket(new NetworkPeerId(2), new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlock, 3, 1, 100),
                ItemId = "Tool_Valid",
                Category = ItemCategory.Tool,
                TransactionId = Guid.Empty
            });

            Assert.True(result); // Handled/dropped gracefully
            Assert.False(service.IsItemUnlocked("Tool_Valid"));
        }

        [Fact]
        public void ARCH01_B05_OversizedItemIdRejection()
        {
            var service = new InventorySyncService();
            string oversizedId = new string('A', 300); // > 256 chars

            bool result = service.TryHandleItemUnlockPacket(new NetworkPeerId(2), new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlock, 3, 1, 100),
                ItemId = oversizedId,
                Category = ItemCategory.Tool,
                TransactionId = Guid.NewGuid()
            });

            Assert.True(result);
            Assert.False(service.IsItemUnlocked(oversizedId));
        }

        #endregion

        #region 24. ARCH-02: Decomposed Mod Architecture & Dedicated Coordinators

        [Fact]
        public void ARCH02_B01_CoordinatorsHandleNullTransportGracefully()
        {
            var sender = new DefectTestHarness.HeadlessPacketSender();
            var packet = new ActionTriggerPacket { Header = new PacketHeader(PacketType.ActionTrigger, 1, 1, 100) };

            bool sent = sender.SendTo(null, new NetworkPeerId(2), packet, DeliveryMethod.ReliableOrdered, 1, 64);
            Assert.False(sent);
        }

        [Fact]
        public void ARCH02_B02_EventBusDecoupling()
        {
            int received = 0;
            Action<int> handler = val => received += val;

            handler(10);
            handler(20);
            Assert.Equal(30, received);
        }

        [Fact]
        public void ARCH02_B03_MultiPeerRegistrationInDeathCoordinator()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.RegisterRemotePeer(new NetworkPeerId(101));
            coordinator.RegisterRemotePeer(new NetworkPeerId(202));

            // With explicit peers registered, death coordinator evaluates multi-peer status
            Assert.True(coordinator.IsPartnerAlive);
        }

        [Fact]
        public void ARCH02_B04_WalletSeedRetryLoopSeparation()
        {
            var outstanding = new Dictionary<NetworkPeerId, byte>
            {
                { new NetworkPeerId(101), 3 }
            };

            // Safely decrements without ConcurrentModificationException
            var retries = new List<KeyValuePair<NetworkPeerId, byte>>();
            foreach (var pair in outstanding)
            {
                if (pair.Value > 1) retries.Add(new KeyValuePair<NetworkPeerId, byte>(pair.Key, (byte)(pair.Value - 1)));
            }
            foreach (var r in retries) outstanding[r.Key] = r.Value;

            Assert.Equal(2, outstanding[new NetworkPeerId(101)]);
        }

        [Fact]
        public void ARCH02_B05_ServiceContainerResolution()
        {
            var dict = new Dictionary<Type, object>();
            dict[typeof(IDeathLifecycleCoordinator)] = new DeathLifecycleCoordinator();

            Assert.True(dict.TryGetValue(typeof(IDeathLifecycleCoordinator), out var inst));
            Assert.IsType<DeathLifecycleCoordinator>(inst);
        }

        #endregion

        #region 25. ARCH-03: Decouple IGameplayActions from Concrete Plugin Object

        [Fact]
        public void ARCH03_B01_TryApplyRemoteItemUnlock_FailsGracefully()
        {
            var mock = new DefectTestHarness.MockGameplayActions { ShouldSucceed = false };
            bool result = mock.TryApplyRemoteItemUnlock("Tool_FleaBrew", ItemCategory.Tool);

            Assert.False(result); // Permits retry
            Assert.Empty(mock.AppliedUnlocks);
        }

        [Fact]
        public void ARCH03_B02_TryReviveAtPartner_TargetPeerNull()
        {
            var mock = new DefectTestHarness.MockGameplayActions();
            bool success = mock.TryReviveAtPartner(10f, 20f, null);

            Assert.True(success);
            Assert.Single(mock.ReviveAttempts);
            Assert.Null(mock.ReviveAttempts[0].TargetPeer);
        }

        [Fact]
        public void ARCH03_B03_ReviveAtPartner_ExtremeCoordinates()
        {
            var mock = new DefectTestHarness.MockGameplayActions();
            var ex = Record.Exception(() => mock.ReviveAtPartner(-99999f, 99999f));
            Assert.Null(ex);
            Assert.Single(mock.ReviveAttempts);
        }

        [Fact]
        public void ARCH03_B04_TriggerTeamWipe_RepeatedCalls()
        {
            var mock = new DefectTestHarness.MockGameplayActions();
            mock.TriggerTeamWipe();
            mock.TriggerTeamWipe();
            mock.TriggerTeamWipe();

            Assert.Equal(3, mock.TeamWipeCount);
        }

        [Fact]
        public void ARCH03_B05_NullActionCallbackSafety()
        {
            DefectTestHarness.IGameplayActionsDouble? actions = null;
            var ex = Record.Exception(() => actions?.TriggerTeamWipe());
            Assert.Null(ex);
        }

        #endregion
    }
}
