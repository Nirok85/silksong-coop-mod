using System;
using System.Collections.Generic;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using SilksongCoop.Tests.E2ERemediationTests;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Comprehensive remediation unit tests for Milestone 4: Combat & Enemy Synchronization:
    /// - Defect 7: Confirming HP snapshots with matching HP extend host authority tick and maintain
    ///             fresh host authority, preventing spurious additive damage fallback.
    /// - Defect 8: Client damage accumulation for unloaded enemies ensures delayed death claims
    ///             in another room are recognized against full enemy HP upon scene entry, with
    ///             proper rejection of insufficient damage and cleanup of cached damage state.
    /// </summary>
    public class Milestone4RemediationTests
    {
        #region Defect 7: Enemy Host Authority Renewal via Confirming Snapshots

        [Fact]
        public void Defect7_ConfirmingSnapshot_WithMatchingHp_ExtendsHostAuthorityTick()
        {
            var service = new EnemySyncService();
            ulong enemyKey = 1001;

            // 1. Initial host snapshot establishes baseline at 100 HP
            bool initialAccepted = service.TryAcceptSnapshot(enemyKey, 100, dead: false, timestamp: 100);
            Assert.True(initialAccepted);
            Assert.True(service.HasFreshHostAuthority(enemyKey));

            // 2. Local player hits enemy for 20 damage, reducing HP to 80
            service.NoteLocalEnemyState(enemyKey, 80, dead: false);

            // 3. Authoritative host snapshot arrives confirming HP is 80 (matching local HP)
            bool confirmingAccepted = service.TryAcceptSnapshot(enemyKey, 80, dead: false, timestamp: 105);
            Assert.True(confirmingAccepted, "Snapshot confirming local hit HP must be accepted");

            // 4. Host authority must be fresh and renewed
            Assert.True(service.HasFreshHostAuthority(enemyKey), "Host authority must remain fresh after confirming snapshot");
        }

        [Fact]
        public void Defect7_ConfirmingSnapshot_PreventsSpuriousAdditiveDamageFallback()
        {
            var service = new EnemySyncService();
            ulong enemyKey = 1002;
            var senderPeer = new NetworkPeerId(2);

            // Initial host snapshot at 100 HP
            service.TryAcceptSnapshot(enemyKey, 100, dead: false, timestamp: 10);

            // Local hit to 80 HP
            service.NoteLocalEnemyState(enemyKey, 80, dead: false);

            // Host confirms 80 HP
            Assert.True(service.TryAcceptSnapshot(enemyKey, 80, dead: false, timestamp: 15));

            // While host authority is fresh, a deferred hit from another client must NOT be added additively
            // (RemoteHitPolicy.Deferred must return false, preventing duplicate damage)
            bool accepted = service.TryAcceptRemoteHit(
                enemyKey, hp: 80, dead: false, damage: 10,
                senderId: senderPeer.Value, sequence: 1,
                policy: RemoteHitPolicy.Deferred, out int resultingHp);

            Assert.False(accepted, "Deferred remote hit while host authority is fresh must not be accepted additively");
            Assert.Equal(0, resultingHp);
        }

        [Fact]
        public void Defect7_ConsecutiveConfirmingSnapshots_ContinuouslyMaintainAuthority()
        {
            var service = new EnemySyncService();
            ulong enemyKey = 1003;

            service.TryAcceptSnapshot(enemyKey, 100, dead: false, timestamp: 1000);
            service.NoteLocalEnemyState(enemyKey, 80, dead: false);

            // Stream of 500 consecutive confirming snapshots with HP 80 at monotonically increasing timestamps
            for (uint i = 1; i <= 500; i++)
            {
                bool accepted = service.TryAcceptSnapshot(enemyKey, 80, dead: false, timestamp: 1000 + i);
                Assert.True(accepted, $"Confirming snapshot iteration {i} must be accepted");
            }

            Assert.True(service.HasFreshHostAuthority(enemyKey), "Consecutive confirming snapshots must continuously maintain fresh authority");
        }

        [Fact]
        public void Defect7_ConfirmingSnapshot_WithLowerHp_AcceptedAndAdvancesState()
        {
            var service = new EnemySyncService();
            ulong enemyKey = 1004;

            service.TryAcceptSnapshot(enemyKey, 100, dead: false, timestamp: 100);
            service.NoteLocalEnemyState(enemyKey, 80, dead: false);

            // Host processed another hit and snapshot reports 60 HP (lower than local 80)
            bool accepted = service.TryAcceptSnapshot(enemyKey, 60, dead: false, timestamp: 110);
            Assert.True(accepted);
            Assert.True(service.HasFreshHostAuthority(enemyKey));
        }

        [Fact]
        public void Defect7_StaleHigherHpSnapshotAfterLocalHit_RejectedToPreventHealing()
        {
            var service = new EnemySyncService();
            ulong enemyKey = 1005;

            service.TryAcceptSnapshot(enemyKey, 100, dead: false, timestamp: 100);
            service.NoteLocalEnemyState(enemyKey, 80, dead: false);

            // Stale snapshot captured before host processed the local hit still claims 100 HP
            bool accepted = service.TryAcceptSnapshot(enemyKey, 100, dead: false, timestamp: 101);
            Assert.False(accepted, "Snapshot with higher HP arriving after local hit must be rejected to prevent visual healing");
        }

        [Fact]
        public void Defect7_DeadSnapshot_ConfirmsKill_AndPreventsSubsequentResurrection()
        {
            var service = new EnemySyncService();
            ulong enemyKey = 1006;

            service.TryAcceptSnapshot(enemyKey, 50, dead: false, timestamp: 100);
            service.NoteLocalEnemyState(enemyKey, 30, dead: false);

            // Authoritative dead snapshot arrives
            bool deadAccepted = service.TryAcceptSnapshot(enemyKey, 0, dead: true, timestamp: 110);
            Assert.True(deadAccepted);
            Assert.True(service.HasFreshHostAuthority(enemyKey));

            // Later snapshot with alive status must be rejected (irreversible death)
            bool resurrectionAttempt = service.TryAcceptSnapshot(enemyKey, 50, dead: false, timestamp: 120);
            Assert.False(resurrectionAttempt, "Dead enemy must never be resurrected by subsequent snapshot");
        }

        [Fact]
        public void Defect7_OutOfOrderTimestamp_SnapshotRejected_EvenIfMatchingHp()
        {
            var service = new EnemySyncService();
            ulong enemyKey = 1007;

            service.TryAcceptSnapshot(enemyKey, 100, dead: false, timestamp: 200);
            service.NoteLocalEnemyState(enemyKey, 80, dead: false);

            // First confirming snapshot at timestamp 210
            Assert.True(service.TryAcceptSnapshot(enemyKey, 80, dead: false, timestamp: 210));

            // Out-of-order snapshot arrives with earlier timestamp 205
            bool outOfOrder = service.TryAcceptSnapshot(enemyKey, 80, dead: false, timestamp: 205);
            Assert.False(outOfOrder, "Snapshot with out-of-order (older) timestamp must be rejected");

            // Equal timestamp snapshot arrives with timestamp 210
            bool equalTimestamp = service.TryAcceptSnapshot(enemyKey, 80, dead: false, timestamp: 210);
            Assert.False(equalTimestamp, "Duplicate timestamp snapshot must be rejected");
        }

        [Fact]
        public void Defect7_LocalHitOrder_ClearedAfterConfirmingSnapshot()
        {
            var service = new EnemySyncService();
            ulong enemyKey = 1008;

            service.TryAcceptSnapshot(enemyKey, 100, dead: false, timestamp: 100);
            service.NoteLocalEnemyState(enemyKey, 80, dead: false);

            // Confirming snapshot arrives and advances LastSnapshotOrder past LastLocalHitOrder
            Assert.True(service.TryAcceptSnapshot(enemyKey, 80, dead: false, timestamp: 110));

            // Authoritative remote hit from host with lower HP (70) should replace absolute HP directly
            bool remoteHitAccepted = service.TryAcceptRemoteHit(
                enemyKey, hp: 70, dead: false, damage: 10,
                senderId: 1, sequence: 1,
                policy: RemoteHitPolicy.Authoritative, out int resultingHp);

            Assert.True(remoteHitAccepted);
            Assert.Equal(70, resultingHp);
        }

        #endregion

        #region Defect 8: Unloaded Enemy Damage Accumulation & Deferred Death Resolution

        /// <summary>
        /// Test simulator reproducing EnemySyncController's deferred unloaded damage tracking
        /// and PumpPendingDeadEnemies evaluation logic in a pure .NET environment.
        /// </summary>
        private sealed class SimulatedEnemyController
        {
            private const int MaxPendingDeadEnemies = 256;
            private const int MaxUnloadedDamageEntries = 512;

            private struct PendingDeadEnemy
            {
                public ulong EnemyKey;
                public int Hp;
                public bool IsDead;
                public uint Timestamp;
                public short Damage;
                public bool FromRemoteClaim;
            }

            private readonly Dictionary<ulong, int> _unloadedEnemyDamage = new();
            private readonly Queue<ulong> _unloadedDamageOrder = new();
            private readonly Dictionary<ulong, PendingDeadEnemy> _pendingDeadEnemies = new();
            private readonly Queue<ulong> _pendingDeadOrder = new();

            public int UnloadedDamageCount => _unloadedEnemyDamage.Count;
            public int PendingDeadCount => _pendingDeadEnemies.Count;

            public int GetUnloadedEnemyDamage(ulong enemyKey)
            {
                return _unloadedEnemyDamage.TryGetValue(enemyKey, out int dmg) ? dmg : 0;
            }

            public void RecordUnloadedEnemyDamage(ulong enemyKey, short damage)
            {
                if (enemyKey == 0 || damage <= 0) return;

                if (!_unloadedEnemyDamage.ContainsKey(enemyKey))
                {
                    while (_unloadedEnemyDamage.Count >= MaxUnloadedDamageEntries &&
                           _unloadedDamageOrder.Count > 0)
                    {
                        ulong oldest = _unloadedDamageOrder.Dequeue();
                        _unloadedEnemyDamage.Remove(oldest);
                    }

                    _unloadedDamageOrder.Enqueue(enemyKey);
                    _unloadedEnemyDamage[enemyKey] = damage;
                }
                else
                {
                    int current = _unloadedEnemyDamage[enemyKey];
                    _unloadedEnemyDamage[enemyKey] = (int)Math.Min((long)current + damage, int.MaxValue);
                }
            }

            public void ApplyRemoteHitOnUnloadedEnemy(ulong enemyKey, short damage, bool isDead, uint timestamp, bool isHost)
            {
                if (enemyKey == 0) return;

                if (damage > 0)
                {
                    RecordUnloadedEnemyDamage(enemyKey, damage);
                }

                if (isDead)
                {
                    _unloadedEnemyDamage.TryGetValue(enemyKey, out int priorDamage);
                    int totalClaimDamage = Math.Max((int)damage, priorDamage);
                    if (isHost && totalClaimDamage <= 0)
                    {
                        // Ignored deferred claim: no damage proof
                        return;
                    }

                    if (!_pendingDeadEnemies.ContainsKey(enemyKey))
                    {
                        while (_pendingDeadEnemies.Count >= MaxPendingDeadEnemies &&
                               _pendingDeadOrder.Count > 0)
                        {
                            ulong oldest = _pendingDeadOrder.Dequeue();
                            if (_pendingDeadEnemies.Remove(oldest)) break;
                        }
                        _pendingDeadOrder.Enqueue(enemyKey);
                    }

                    _pendingDeadEnemies[enemyKey] = new PendingDeadEnemy
                    {
                        EnemyKey = enemyKey,
                        Hp = 0,
                        IsDead = true,
                        Timestamp = timestamp,
                        Damage = (short)Math.Clamp(totalClaimDamage, 0, short.MaxValue),
                        FromRemoteClaim = true
                    };
                }
            }

            public bool PumpPendingDeadEnemy(ulong enemyKey, int loadedHp, bool isHost, out int resolvedDamage)
            {
                resolvedDamage = 0;
                if (!_pendingDeadEnemies.TryGetValue(enemyKey, out var pending))
                    return false;

                if (isHost && pending.FromRemoteClaim)
                {
                    _unloadedEnemyDamage.TryGetValue(pending.EnemyKey, out int accumulated);
                    int totalDamage = Math.Max((int)pending.Damage, accumulated);
                    bool reject = totalDamage <= 0;
                    if (!reject && loadedHp > 0 && totalDamage < loadedHp)
                    {
                        reject = true;
                    }

                    if (reject)
                    {
                        // Drop deferred death claim
                        _pendingDeadEnemies.Remove(enemyKey);
                        _unloadedEnemyDamage.Remove(enemyKey);
                        return false;
                    }

                    resolvedDamage = totalDamage;
                }

                _pendingDeadEnemies.Remove(enemyKey);
                _unloadedEnemyDamage.Remove(enemyKey);
                return true;
            }

            public void ResetSession()
            {
                _unloadedEnemyDamage.Clear();
                _unloadedDamageOrder.Clear();
                _pendingDeadEnemies.Clear();
                _pendingDeadOrder.Clear();
            }
        }

        [Fact]
        public void Defect8_MultipleNonLethalHits_Accumulate_AndResolveDeferredDeathOnLoadedEnemy()
        {
            var controller = new SimulatedEnemyController();
            ulong bossKey = 2001;

            // Client in another room deals 3 non-lethal hits of 30 damage, then a lethal hit of 40 damage
            controller.ApplyRemoteHitOnUnloadedEnemy(bossKey, damage: 30, isDead: false, timestamp: 10, isHost: true);
            controller.ApplyRemoteHitOnUnloadedEnemy(bossKey, damage: 30, isDead: false, timestamp: 20, isHost: true);
            controller.ApplyRemoteHitOnUnloadedEnemy(bossKey, damage: 40, isDead: true, timestamp: 30, isHost: true);

            // Total accumulated damage must be 30 + 30 + 40 = 100
            Assert.Equal(100, controller.GetUnloadedEnemyDamage(bossKey));

            // Host enters room where boss spawns with full 100 HP
            bool resolved = controller.PumpPendingDeadEnemy(bossKey, loadedHp: 100, isHost: true, out int totalDamage);

            Assert.True(resolved, "Deferred death must be resolved against full enemy HP when total damage matches HP");
            Assert.Equal(100, totalDamage);
        }

        [Fact]
        public void Defect8_SingleLethalHit_ResolvesDeferredDeath()
        {
            var controller = new SimulatedEnemyController();
            ulong enemyKey = 2002;

            // Single lethal hit of 150 damage on 120 HP enemy
            controller.ApplyRemoteHitOnUnloadedEnemy(enemyKey, damage: 150, isDead: true, timestamp: 100, isHost: true);

            bool resolved = controller.PumpPendingDeadEnemy(enemyKey, loadedHp: 120, isHost: true, out int totalDamage);
            Assert.True(resolved);
            Assert.Equal(150, totalDamage);
        }

        [Fact]
        public void Defect8_TrulyInsufficientDamage_IsRejected_AndDropsDeferredKill()
        {
            var controller = new SimulatedEnemyController();
            ulong enemyKey = 2003;

            // Client dealt 20 dmg, then claimed kill with 30 dmg = total 50 dmg
            controller.ApplyRemoteHitOnUnloadedEnemy(enemyKey, damage: 20, isDead: false, timestamp: 10, isHost: true);
            controller.ApplyRemoteHitOnUnloadedEnemy(enemyKey, damage: 30, isDead: true, timestamp: 20, isHost: true);

            // Host spawns enemy with 100 HP (50 < 100)
            bool resolved = controller.PumpPendingDeadEnemy(enemyKey, loadedHp: 100, isHost: true, out int totalDamage);

            Assert.False(resolved, "Deferred death claim with insufficient total damage must be rejected");
            Assert.Equal(0, totalDamage);
            Assert.Equal(0, controller.PendingDeadCount);
            Assert.Equal(0, controller.GetUnloadedEnemyDamage(enemyKey));
        }

        [Fact]
        public void Defect8_ResolvedKill_CleansUpAccumulatedDamageAndPendingQueue()
        {
            var controller = new SimulatedEnemyController();
            ulong enemyKey = 2004;

            controller.ApplyRemoteHitOnUnloadedEnemy(enemyKey, damage: 50, isDead: false, timestamp: 10, isHost: true);
            controller.ApplyRemoteHitOnUnloadedEnemy(enemyKey, damage: 50, isDead: true, timestamp: 20, isHost: true);

            // Resolve kill
            bool resolved = controller.PumpPendingDeadEnemy(enemyKey, loadedHp: 100, isHost: true, out int totalDamage);
            Assert.True(resolved);
            Assert.Equal(100, totalDamage);

            // State caches must be completely clean
            Assert.Equal(0, controller.GetUnloadedEnemyDamage(enemyKey));
            Assert.Equal(0, controller.PendingDeadCount);
            Assert.Equal(0, controller.UnloadedDamageCount);
        }

        [Fact]
        public void Defect8_ZeroOrNegativeDamage_IsIgnoredByAccumulator()
        {
            var controller = new SimulatedEnemyController();
            ulong enemyKey = 2005;

            controller.ApplyRemoteHitOnUnloadedEnemy(enemyKey, damage: 0, isDead: false, timestamp: 10, isHost: true);
            controller.ApplyRemoteHitOnUnloadedEnemy(enemyKey, damage: -15, isDead: false, timestamp: 20, isHost: true);

            Assert.Equal(0, controller.GetUnloadedEnemyDamage(enemyKey));
            Assert.Equal(0, controller.UnloadedDamageCount);

            // Zero-damage death claim without prior hits must not create a pending death
            controller.ApplyRemoteHitOnUnloadedEnemy(enemyKey, damage: 0, isDead: true, timestamp: 30, isHost: true);
            Assert.Equal(0, controller.PendingDeadCount);
        }

        [Fact]
        public void Defect8_ExactDamageBoundaries_OneHpShortRejects_ExactMatchResolves()
        {
            var controller = new SimulatedEnemyController();
            ulong enemyOneShort = 2006;
            ulong enemyExact = 2007;

            // 99 damage vs 100 HP -> reject
            controller.ApplyRemoteHitOnUnloadedEnemy(enemyOneShort, damage: 49, isDead: false, timestamp: 10, isHost: true);
            controller.ApplyRemoteHitOnUnloadedEnemy(enemyOneShort, damage: 50, isDead: true, timestamp: 20, isHost: true);
            bool resolvedShort = controller.PumpPendingDeadEnemy(enemyOneShort, loadedHp: 100, isHost: true, out _);
            Assert.False(resolvedShort);

            // Exactly 100 damage vs 100 HP -> resolve
            controller.ApplyRemoteHitOnUnloadedEnemy(enemyExact, damage: 50, isDead: false, timestamp: 10, isHost: true);
            controller.ApplyRemoteHitOnUnloadedEnemy(enemyExact, damage: 50, isDead: true, timestamp: 20, isHost: true);
            bool resolvedExact = controller.PumpPendingDeadEnemy(enemyExact, loadedHp: 100, isHost: true, out int dmg);
            Assert.True(resolvedExact);
            Assert.Equal(100, dmg);
        }

        [Fact]
        public void Defect8_FiftyMicroHits_AccumulateToKillBoss()
        {
            var controller = new SimulatedEnemyController();
            ulong bossKey = 2008;

            for (short i = 0; i < 49; i++)
            {
                controller.ApplyRemoteHitOnUnloadedEnemy(bossKey, damage: 2, isDead: false, timestamp: (uint)(i + 1), isHost: true);
            }
            controller.ApplyRemoteHitOnUnloadedEnemy(bossKey, damage: 2, isDead: true, timestamp: 50, isHost: true);

            Assert.Equal(100, controller.GetUnloadedEnemyDamage(bossKey));

            bool resolved = controller.PumpPendingDeadEnemy(bossKey, loadedHp: 100, isHost: true, out int totalDamage);
            Assert.True(resolved);
            Assert.Equal(100, totalDamage);
        }

        [Fact]
        public void Defect8_OverkillDamage_HandledWithoutOverflow()
        {
            var controller = new SimulatedEnemyController();
            ulong enemyKey = 2009;

            controller.ApplyRemoteHitOnUnloadedEnemy(enemyKey, damage: short.MaxValue, isDead: false, timestamp: 10, isHost: true);
            controller.ApplyRemoteHitOnUnloadedEnemy(enemyKey, damage: short.MaxValue, isDead: true, timestamp: 20, isHost: true);

            int expected = short.MaxValue * 2;
            Assert.Equal(expected, controller.GetUnloadedEnemyDamage(enemyKey));

            bool resolved = controller.PumpPendingDeadEnemy(enemyKey, loadedHp: 1000, isHost: true, out int totalDamage);
            Assert.True(resolved);
            Assert.Equal(expected, totalDamage);
        }

        [Fact]
        public void Defect8_MultipleDistinctEnemies_TrackedIndependentlyWithoutCrosstalk()
        {
            var controller = new SimulatedEnemyController();

            // 50 distinct unloaded enemies receiving different hits
            for (ulong i = 1; i <= 50; i++)
            {
                controller.ApplyRemoteHitOnUnloadedEnemy(i, damage: (short)(i * 2), isDead: false, timestamp: 10, isHost: true);
                controller.ApplyRemoteHitOnUnloadedEnemy(i, damage: (short)(i * 3), isDead: true, timestamp: 20, isHost: true);
            }

            for (ulong i = 1; i <= 50; i++)
            {
                int expectedDmg = (int)(i * 5);
                Assert.Equal(expectedDmg, controller.GetUnloadedEnemyDamage(i));

                bool resolved = controller.PumpPendingDeadEnemy(i, loadedHp: expectedDmg, isHost: true, out int totalDamage);
                Assert.True(resolved);
                Assert.Equal(expectedDmg, totalDamage);
            }

            Assert.Equal(0, controller.UnloadedDamageCount);
            Assert.Equal(0, controller.PendingDeadCount);
        }

        [Fact]
        public void Defect8_SessionReset_ClearsUnloadedDamageCache()
        {
            var controller = new SimulatedEnemyController();
            controller.ApplyRemoteHitOnUnloadedEnemy(3001, damage: 50, isDead: false, timestamp: 10, isHost: true);
            controller.ApplyRemoteHitOnUnloadedEnemy(3002, damage: 80, isDead: true, timestamp: 20, isHost: true);

            Assert.Equal(2, controller.UnloadedDamageCount);
            Assert.Equal(1, controller.PendingDeadCount);

            controller.ResetSession();

            Assert.Equal(0, controller.UnloadedDamageCount);
            Assert.Equal(0, controller.PendingDeadCount);
            Assert.Equal(0, controller.GetUnloadedEnemyDamage(3001));
            Assert.Equal(0, controller.GetUnloadedEnemyDamage(3002));
        }

        #endregion

        #region Cross-Feature Integration: Defect 7 & Defect 8

        [Fact]
        public void Defect7_x_Defect8_HostAuthorityRenewal_With_UnloadedEnemyDamageResolution()
        {
            // Scenario:
            // 1. Client attacks unloaded boss in Room B across 3 hits (30, 30, 40 dmg = 100 dmg, dead: true).
            // 2. Host transitions into Room B: boss spawns with 100 HP.
            // 3. Host resolves deferred death and broadcasts dead snapshot (hp = 0, dead = true).
            // 4. Client accepts authoritative snapshot and confirms host authority is renewed.
            var controller = new SimulatedEnemyController();
            var clientEnemySync = new EnemySyncService();
            ulong bossKey = 9999;

            // Step 1: Client attacks unloaded boss
            controller.ApplyRemoteHitOnUnloadedEnemy(bossKey, damage: 30, isDead: false, timestamp: 10, isHost: true);
            controller.ApplyRemoteHitOnUnloadedEnemy(bossKey, damage: 30, isDead: false, timestamp: 20, isHost: true);
            controller.ApplyRemoteHitOnUnloadedEnemy(bossKey, damage: 40, isDead: true, timestamp: 30, isHost: true);
            Assert.Equal(100, controller.GetUnloadedEnemyDamage(bossKey));

            // Step 2 & 3: Host enters room and resolves kill
            bool hostResolved = controller.PumpPendingDeadEnemy(bossKey, loadedHp: 100, isHost: true, out int totalDamage);
            Assert.True(hostResolved);
            Assert.Equal(100, totalDamage);

            // Step 4: Host sends dead snapshot to client
            clientEnemySync.NoteLocalEnemyState(bossKey, hp: 80, dead: false);
            bool snapshotAccepted = clientEnemySync.TryAcceptSnapshot(bossKey, hp: 0, dead: true, timestamp: 50);
            Assert.True(snapshotAccepted);
            Assert.True(clientEnemySync.HasFreshHostAuthority(bossKey));
        }

        #endregion
    }
}
