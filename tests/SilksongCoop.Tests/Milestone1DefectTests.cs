using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.Revive;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests
{
    public class Milestone1DefectTests
    {
        private static NetworkPeerId Peer(ulong id) => new(id, $"Player{id}");

        private static DeathLifecyclePacket Packet(DeathEventType type, uint epoch = 1) =>
            new()
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 0, NetworkClock.NowMs),
                EventType = type,
                CoordinateX = 10f,
                CoordinateY = 20f,
                RemainingSeconds = 0f,
                Epoch = epoch
            };

        #region CRIT-01: Epoch Race Condition & Atomicity Tests

        [Fact]
        public void CRIT01_ResetAfterBenchRespawn_AtomicallyResetsStateAndAdvancesEpoch()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = Peer(2);
            coordinator.RegisterRemotePeer(peer);

            // Trigger solo bench respawn
            coordinator.BeginSoloBenchRespawn();
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
            uint initialEpoch = coordinator.CurrentEpoch;

            // Reset after bench respawn
            coordinator.ResetAfterBenchRespawn();

            Assert.Equal(DeathState.Alive, coordinator.CurrentState);
            Assert.Equal(initialEpoch + 1, coordinator.CurrentEpoch);
            Assert.Equal(0f, coordinator.RemainingSpectatorTime);
            Assert.Null(coordinator.ReviveTargetPeer);
        }

        [Fact]
        public void CRIT01_AdoptHigherEpoch_DuringRespawn_PreventsDuplicateEpochIncrement()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = Peer(2);
            coordinator.RegisterRemotePeer(peer);

            coordinator.BeginSoloBenchRespawn();
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);

            // Peer announces higher epoch 5 while we are reloading
            coordinator.HandleDeathPacket(peer, Packet(DeathEventType.BenchCheckpointUpdate, epoch: 5));
            Assert.Equal(5u, coordinator.CurrentEpoch);

            // Reset after bench respawn: since epoch 5 was adopted during respawn, epoch must NOT advance to 6
            coordinator.ResetAfterBenchRespawn();

            Assert.Equal(DeathState.Alive, coordinator.CurrentState);
            Assert.Equal(5u, coordinator.CurrentEpoch);
        }

        [Fact]
        public async Task CRIT01_EpochRaceCondition_ConcurrentResetAndAdopt_MaintainsMonotonicity()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = Peer(2);
            coordinator.RegisterRemotePeer(peer);

            uint lastObservedEpoch = coordinator.CurrentEpoch;
            bool failedMonotonicity = false;

            var tasks = new List<Task>();
            for (int t = 0; t < 8; t++)
            {
                int threadId = t;
                tasks.Add(Task.Run(() =>
                {
                    var rng = new Random(42 + threadId);
                    for (int i = 0; i < 500; i++)
                    {
                        if (threadId % 2 == 0)
                        {
                            coordinator.BeginSoloBenchRespawn();
                            coordinator.ResetAfterBenchRespawn();
                        }
                        else
                        {
                            uint current = coordinator.CurrentEpoch;
                            uint newEpoch = current + (uint)rng.Next(1, 4);
                            coordinator.HandleDeathPacket(peer, Packet(DeathEventType.BenchCheckpointUpdate, epoch: newEpoch));
                        }

                        uint observed = coordinator.CurrentEpoch;
                        if (unchecked((int)(observed - lastObservedEpoch)) < 0)
                        {
                            failedMonotonicity = true;
                        }
                    }
                }));
            }

            await Task.WhenAll(tasks);
            Assert.False(failedMonotonicity, "Epoch monotonicity violated during concurrent reset and packet handling");
            Assert.True(coordinator.CurrentEpoch >= lastObservedEpoch);
        }

        #endregion

        #region MED-01: Protect _pendingSoloBenchRespawn During Repeated Deaths & Escalation

        [Fact]
        public void MED01_SoloBenchRespawn_EscalatesToTeamWipe_WhenIncomingTeamWipeOccurs()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = Peer(2);
            coordinator.RegisterRemotePeer(peer);

            var outgoing = new List<DeathLifecyclePacket>();
            coordinator.OnOutgoingDeathPacket += p => outgoing.Add(p);

            // Start solo bench respawn: sets _pendingSoloBenchRespawn = true
            coordinator.BeginSoloBenchRespawn();
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
            Assert.Empty(outgoing);

            // Incoming team wipe arrives from peer while we are reloading
            coordinator.HandleDeathPacket(peer, Packet(DeathEventType.TeamWipeOccurred, epoch: coordinator.CurrentEpoch));

            // Reload finishes: ResetAfterBenchRespawn must NOT emit SoloBenchRespawn because TeamWipe occurred!
            coordinator.ResetAfterBenchRespawn();

            Assert.DoesNotContain(outgoing, p => p.EventType == DeathEventType.SoloBenchRespawn);
        }

        [Fact]
        public void MED01_BeginBenchRespawn_EscalatesFromSoloToTeamWipe_WhenNotifyPartnerRequested()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = Peer(2);
            coordinator.RegisterRemotePeer(peer);

            var outgoing = new List<DeathLifecyclePacket>();
            coordinator.OnOutgoingDeathPacket += p => outgoing.Add(p);

            // Start solo bench respawn
            coordinator.BeginSoloBenchRespawn();
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
            Assert.Empty(outgoing);

            // Now escalate to full team wipe with immediate notification
            coordinator.ForceBenchRespawn(notifyPartner: true);

            // Must have immediately emitted TeamWipeOccurred
            var wipePacket = Assert.Single(outgoing, p => p.EventType == DeathEventType.TeamWipeOccurred);
            Assert.Equal(coordinator.CurrentEpoch, wipePacket.Epoch);

            // Reload completes: must NOT emit SoloBenchRespawn
            coordinator.ResetAfterBenchRespawn();

            Assert.DoesNotContain(outgoing, p => p.EventType == DeathEventType.SoloBenchRespawn);
        }

        #endregion

        #region CRIT-02: Prevent Duplicate Bench Reloads on Disconnect

        [Fact]
        public void CRIT02_Disconnect_WhenWipeOrRecoveryActive_SuppressesTriggerTeamWipe()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = Peer(2);
            coordinator.RegisterRemotePeer(peer);
            coordinator.IsPartnerAlive = true;

            int wipeCount = 0;
            coordinator.OnTeamWipeTriggered += () => wipeCount++;

            // Local player begins solo bench respawn (wipe/recovery active)
            coordinator.BeginSoloBenchRespawn();
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
            Assert.Equal(1, wipeCount);

            // Peer disconnects while wipe is already active
            coordinator.RemoveRemotePeer(peer);

            // Stale/concurrent partner death and incoming team wipe packets must not trigger duplicate wipe
            coordinator.OnRemotePartnerDied(peer, 0f, 0f);
            coordinator.HandleDeathPacket(peer, Packet(DeathEventType.TeamWipeOccurred, coordinator.CurrentEpoch));
            coordinator.BeginSoloBenchRespawn();

            Assert.Equal(1, wipeCount);
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
        }

        [Fact]
        public void CRIT02_Disconnect_WhenSpectating_TriggersTeamWipeOnlyOnce()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = Peer(2);
            coordinator.RegisterRemotePeer(peer);
            coordinator.IsPartnerAlive = true;

            int wipeCount = 0;
            coordinator.OnTeamWipeTriggered += () => wipeCount++;

            // Local player dies while partner alive -> transitions to Spectating
            bool suppressed = coordinator.OnLocalPlayerDied(10f, 20f);
            Assert.True(suppressed);
            Assert.Equal(DeathState.Spectating, coordinator.CurrentState);
            Assert.Equal(0, wipeCount);

            // Partner dies / disconnect wipe triggers while spectating
            coordinator.OnRemotePartnerDied(peer, 10f, 20f);
            Assert.Equal(1, wipeCount);
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);

            // Duplicate disconnect or incoming team wipe packets while in TeamWipe
            coordinator.RemoveRemotePeer(peer);
            coordinator.OnRemotePartnerDied(peer, 10f, 20f);
            coordinator.HandleDeathPacket(peer, Packet(DeathEventType.TeamWipeOccurred, coordinator.CurrentEpoch));

            Assert.Equal(1, wipeCount);
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
        }

        #endregion

        #region HIGH-01: Prober Fallback to Hazard Respawn Location

        [Fact]
        public void HIGH01_TryFindSafeGround_FallsBackToHazardLocation_WhenAllRaycastsFail()
        {
            var prober = new SafeGroundReviveTests.SimulatedGroundProber();
            // Empty platforms = bottomless abyss
            prober.Platforms.Clear();
            prober.FallbackHazardLocation = (45.5f, 12.0f);

            bool success = prober.TryFindSafeGround(10f, 50f, out float safeX, out float safeY);

            Assert.True(success, "Prober must succeed by falling back to hazard location.");
            Assert.Equal(45.5f, safeX);
            Assert.Equal(12.0f, safeY);
        }

        [Fact]
        public void HIGH01_TryFindSafeGround_Fails_WhenBothRaycastsAndFallbackFail()
        {
            var prober = new SafeGroundReviveTests.SimulatedGroundProber();
            prober.Platforms.Clear();
            prober.FallbackHazardLocation = null;

            bool success = prober.TryFindSafeGround(10f, 50f, out float safeX, out float safeY);

            Assert.False(success, "Prober must return false when both raycasts and fallback are absent.");
        }

        #endregion

        #region MED-02: PrepareForNewSession Resets Invulnerability and State

        [Fact]
        public void MED02_PrepareForNewSession_ResetsDeathLifecycleStateAndReviveTarget()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = Peer(2);
            coordinator.RegisterRemotePeer(peer);
            coordinator.IsPartnerAlive = true;

            // Player dies and enters spectating with a selected revive target
            coordinator.OnLocalPlayerDied(10f, 20f);
            coordinator.UpdatePartnerPosition(peer, 50f, 60f);
            coordinator.UpdateTick(coordinator.ReviveCooldownDuration + 1f); // timer expires -> selects revive target

            Assert.Equal(DeathState.RespawningAtPartner, coordinator.CurrentState);
            Assert.Equal(peer, coordinator.ReviveTargetPeer);

            // New session / disconnect cleanup executes SynchronizeEpochForNewSession
            coordinator.SynchronizeEpochForNewSession();

            Assert.Equal(DeathState.Alive, coordinator.CurrentState);
            Assert.Null(coordinator.ReviveTargetPeer);
            Assert.Equal(1u, coordinator.CurrentEpoch);
            Assert.Equal(0f, coordinator.RemainingSpectatorTime);
            Assert.True(coordinator.IsPartnerAlive);
        }

        [Fact]
        public void MED02_ClearRemotePeers_ClearsPendingSoloBenchRespawnAndReviveTargets()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var peer = Peer(2);
            coordinator.RegisterRemotePeer(peer);

            coordinator.BeginSoloBenchRespawn();
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);

            coordinator.ClearRemotePeers();

            Assert.Null(coordinator.ReviveTargetPeer);
            Assert.True(coordinator.IsPartnerAlive);

            // Resetting bench respawn after clearing remote peers must not emit stale solo packets
            var outgoing = new List<DeathLifecyclePacket>();
            coordinator.OnOutgoingDeathPacket += p => outgoing.Add(p);

            coordinator.ResetAfterBenchRespawn();
            Assert.DoesNotContain(outgoing, p => p.EventType == DeathEventType.SoloBenchRespawn);
        }

        #endregion

        #region MED-03: Suppress Spectator F9 Keybind When Inactive

        [Fact]
        public void MED03_HandleF9Keypress_SuppressedWhenSpectatorInactive()
        {
            var controller = new DefectRemediationTiers.DefectTestHarness.HeadlessSpectatorController
            {
                IsSpectatorActive = false,
                SpectatorCameraFollow = true
            };

            bool handled = controller.HandleF9Keypress();

            Assert.False(handled, "F9 keypress must be ignored when spectator is inactive.");
            Assert.True(controller.SpectatorCameraFollow, "Camera follow state must not change.");
            Assert.Equal(0, controller.ToastNotificationCount);
        }

        [Fact]
        public void MED03_HandleF9Keypress_ExecutesWhenSpectatorActive()
        {
            var controller = new DefectRemediationTiers.DefectTestHarness.HeadlessSpectatorController
            {
                IsSpectatorActive = true,
                SpectatorCameraFollow = true
            };

            bool handled = controller.HandleF9Keypress();

            Assert.True(handled, "F9 keypress must be handled when spectator is active.");
            Assert.False(controller.SpectatorCameraFollow, "Camera follow state must toggle.");
            Assert.Equal(1, controller.ToastNotificationCount);
        }

        #endregion

        #region Concurrency: Stress Testing Race Conditions

        [Fact]
        public void Concurrency_SoloBenchRespawn_And_IncomingTeamWipe_NeverEmitsRoguePacket_AndTriggersOnce()
        {
            const int cycles = 10000;
            var peer1 = Peer(2);
            var peer2 = Peer(3);
            var pendingField = typeof(DeathLifecycleCoordinator).GetField(
                "_pendingSoloBenchRespawn",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(pendingField);

            Parallel.For(0, cycles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, _ =>
            {
                var coordinator = new DeathLifecycleCoordinator();
                coordinator.RegisterRemotePeer(peer1);
                coordinator.RegisterRemotePeer(peer2);

                int wipeTriggeredCount = 0;
                coordinator.OnTeamWipeTriggered += () => Interlocked.Increment(ref wipeTriggeredCount);

                var outgoingPackets = new ConcurrentBag<DeathLifecyclePacket>();
                coordinator.OnOutgoingDeathPacket += p => outgoingPackets.Add(p);

                using var barrier = new Barrier(3);

                var t1 = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    coordinator.BeginSoloBenchRespawn();
                });

                var t2 = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    coordinator.HandleDeathPacket(peer1, Packet(DeathEventType.TeamWipeOccurred, coordinator.CurrentEpoch));
                });

                var t3 = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    coordinator.HandleDeathPacket(peer2, Packet(DeathEventType.TeamWipeOccurred, coordinator.CurrentEpoch));
                });

                Task.WaitAll(t1, t2, t3);

                // _pendingSoloBenchRespawn must already be cleanly reset to false prior to ResetAfterBenchRespawn
                bool pendingBeforeReset = (bool)pendingField.GetValue(coordinator)!;
                Assert.False(pendingBeforeReset, "_pendingSoloBenchRespawn must be false after concurrent wipe and solo respawn");

                // Simulate bench reload completion
                coordinator.ResetAfterBenchRespawn();

                // 1. Rogue SoloBenchRespawn must NEVER be emitted
                foreach (var packet in outgoingPackets)
                {
                    Assert.NotEqual(DeathEventType.SoloBenchRespawn, packet.EventType);
                }

                // 2. OnTeamWipeTriggered must be invoked exactly once across the concurrent operations
                Assert.Equal(1, wipeTriggeredCount);

                // 3. Coordinator must safely return to Alive and _pendingSoloBenchRespawn remains false
                Assert.Equal(DeathState.Alive, coordinator.CurrentState);
                bool pendingAfterReset = (bool)pendingField.GetValue(coordinator)!;
                Assert.False(pendingAfterReset, "_pendingSoloBenchRespawn must remain false after reset");
            });
        }

        [Fact]
        public void Concurrency_AdversarialMultiThread_InterleavedSoloAndMultipleTeamWipes_StressTest()
        {
            const int cycles = 10000;
            var peer1 = Peer(2);
            var peer2 = Peer(3);
            var peer3 = Peer(4);
            var pendingField = typeof(DeathLifecycleCoordinator).GetField(
                "_pendingSoloBenchRespawn",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(pendingField);

            Parallel.For(0, cycles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, iter =>
            {
                var coordinator = new DeathLifecycleCoordinator();
                coordinator.RegisterRemotePeer(peer1);
                coordinator.RegisterRemotePeer(peer2);
                coordinator.RegisterRemotePeer(peer3);

                int wipeTriggeredCount = 0;
                coordinator.OnTeamWipeTriggered += () => Interlocked.Increment(ref wipeTriggeredCount);

                var outgoingPackets = new ConcurrentBag<DeathLifecyclePacket>();
                coordinator.OnOutgoingDeathPacket += p => outgoingPackets.Add(p);

                using var barrier = new Barrier(5);

                var t1 = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    Thread.SpinWait((iter * 7) % 50);
                    coordinator.BeginSoloBenchRespawn();
                });

                var t2 = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    Thread.SpinWait((iter * 13) % 50);
                    coordinator.HandleDeathPacket(peer1, Packet(DeathEventType.TeamWipeOccurred, coordinator.CurrentEpoch));
                });

                var t3 = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    Thread.SpinWait((iter * 19) % 50);
                    coordinator.HandleDeathPacket(peer2, Packet(DeathEventType.TeamWipeOccurred, coordinator.CurrentEpoch));
                });

                var t4 = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    Thread.SpinWait((iter * 23) % 50);
                    coordinator.HandleDeathPacket(peer3, Packet(DeathEventType.TeamWipeOccurred, coordinator.CurrentEpoch));
                });

                var t5 = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    Thread.SpinWait((iter * 29) % 50);
                    coordinator.BeginSoloBenchRespawn();
                });

                Task.WaitAll(t1, t2, t3, t4, t5);

                // _pendingSoloBenchRespawn must be false before ResetAfterBenchRespawn
                bool pendingBeforeReset = (bool)pendingField.GetValue(coordinator)!;
                Assert.False(pendingBeforeReset, "_pendingSoloBenchRespawn must be false prior to reset");

                // Bench reload completes
                coordinator.ResetAfterBenchRespawn();

                // 1. Rogue SoloBenchRespawn must NEVER be emitted
                foreach (var packet in outgoingPackets)
                {
                    Assert.NotEqual(DeathEventType.SoloBenchRespawn, packet.EventType);
                }

                // 2. OnTeamWipeTriggered must be invoked exactly once across all 5 racing threads
                Assert.Equal(1, wipeTriggeredCount);

                // 3. Coordinator safely resets to Alive
                Assert.Equal(DeathState.Alive, coordinator.CurrentState);
                bool pendingAfterReset = (bool)pendingField.GetValue(coordinator)!;
                Assert.False(pendingAfterReset, "_pendingSoloBenchRespawn must be false after reset");
            });
        }

        #endregion
    }
}
