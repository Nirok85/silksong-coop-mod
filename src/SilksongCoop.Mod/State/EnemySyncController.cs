using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SilksongCoop.Mod.State
{
    /// <summary>
    /// Replicates enemy/boss HP, permanent breakables/gates and physical pickups between the
    /// two clients. Enemy hits are applied locally by the game and then broadcast; the peer
    /// applies the same HP to its own <c>HealthManager</c>, so bosses no longer have
    /// per-player HP pools. The host also sends a 1 Hz snapshot to correct drift.
    /// </summary>
    public class EnemySyncController : MonoBehaviour
    {
        private sealed class KeyHolder
        {
            public ulong Key;
            // Full hierarchy fingerprint: a pooled/reparented/renamed object, or an object
            // under a renamed ancestor, must not keep returning its old key.
            public Transform[] Chain = Array.Empty<Transform>();
            public int[] SiblingIndices = Array.Empty<int>();
            public int SceneHandle = -1;
        }

        private const byte WorldChannel = NetworkChannels.World;

        private struct PendingDeadEnemy
        {
            public EnemySnapshotEntry Entry;
            public uint Timestamp;
            // A client's death claim for an enemy the host had not loaded. It carries no
            // host-simulated HP to check, so at minimum it must prove actual damage.
            public short Damage;
            public bool FromRemoteClaim;
        }

        /// <summary>
        /// A queued world-state packet for the catch-up replay. Replaying in one frame used
        /// to overflow the transport's reliable backlog (512) and silently lose every later
        /// packet; the queue is drained gradually instead.
        /// </summary>
        private struct ReplayItem
        {
            public bool IsPersistent;
            public WorldEventPacket World;
            public PersistentStatePacket Persistent;
        }

        private const int MaxDeadRegistry = DeadEnemyRegistry.DefaultCapacity;
        private const int MaxPendingDeadEnemies = 256;
        private const int ReplayBatchSize = 16;
        private const int ReplayBacklogThreshold = NetworkLimits.ReplayBacklogThreshold;

        private SilksongCoopPlugin? _plugin;
        private readonly EnemySyncService _service = new EnemySyncService();
        private ConditionalWeakTable<HealthManager, KeyHolder> _keyCache = new ConditionalWeakTable<HealthManager, KeyHolder>();
        private struct SnapshotState
        {
            public int Hp;
            public bool Dead;
        }

        private readonly Dictionary<ulong, SnapshotState> _lastSnapshot = new Dictionary<ulong, SnapshotState>();
        // Exact-key lookup index rebuilt on scene changes and at the start of each host
        // snapshot. Proximity fallback still enumerates active enemies because a freshly
        // spawned enemy may not be indexed yet.
        private readonly Dictionary<ulong, HealthManager> _enemyIndex = new Dictionary<ulong, HealthManager>();
        private bool _enemyIndexDirty = true;
        private bool _enemyIndexFailureLogged;
        private float _nextEnemyIndexRebuildAt;
        // World events are rare compared with enemy traffic, so these indexes are built
        // lazily on the first breakable/gate event of a scene. Scene name + handle guard
        // against a reload reusing names.
        private readonly Dictionary<ulong, Breakable> _breakableIndex = new Dictionary<ulong, Breakable>();
        private readonly Dictionary<ulong, Gate> _gateIndex = new Dictionary<ulong, Gate>();
        private string _worldIndexScene = string.Empty;
        private int _worldIndexSceneHandle = -1;
        private bool _worldIndexDirty = true;
        // Remote pickup claims usually arrive in a burst when several drops are collected
        // together. Reuse one scene enumeration for a short window instead of rescanning
        // every CurrencyObjectBase for each claim.
        private CurrencyObjectBase[]? _pickupCache;
        private string _pickupCacheScene = string.Empty;
        private int _pickupCacheSceneHandle = -1;
        private float _pickupCacheExpiresAt;
        private bool _pickupCacheDirty = true;
        private const float PickupCacheTtl = 0.5f;
        // Full key recomputation every 30s catches ancestor renames without paying the
        // string-getter cost on every hit.
        private float _nextKeyCacheRefreshAt;
        private readonly List<EnemySnapshotEntry> _snapshotBatch = new List<EnemySnapshotEntry>(EnemySnapshotPacket.MaxEntries);
        private readonly EnemySnapshotEntry[] _snapshotEntryBuffer = new EnemySnapshotEntry[EnemySnapshotPacket.MaxEntries];
        private readonly DeadEnemyRegistry _deadRegistry = new DeadEnemyRegistry(MaxDeadRegistry);
        // Replay registries survive a reconnect but must not leak across save slots or a
        // save reload. The tracker is seeded by the game's profile id/save singleton.
        private readonly SaveScopeTracker _saveScope = new SaveScopeTracker();
        // Transport peer IDs can change when the same player switches roles or transports.
        // Keep the revision origin stable for the lifetime of the local replay cache.
        private readonly ulong _revisionOrigin = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0) | 1UL;
        // Dead snapshot entries whose scene is not loaded yet. Applying them immediately
        // dropped bosses killed before a late joiner entered the room; retry after a load.
        private readonly Dictionary<ulong, PendingDeadEnemy> _pendingDeadEnemies =
            new Dictionary<ulong, PendingDeadEnemy>();
        private readonly Queue<ulong> _pendingDeadOrder = new Queue<ulong>();
        private float _pendingDeadRetryAt;
        private readonly Dictionary<ulong, WorldEventPacket> _worldEvents = new Dictionary<ulong, WorldEventPacket>();
        private readonly Queue<ulong> _worldEventOrder = new Queue<ulong>();
        private readonly Dictionary<ulong, PersistentStatePacket> _persistentStates = new Dictionary<ulong, PersistentStatePacket>();
        private readonly RevisionLedger _appliedWorldRevisions = new RevisionLedger();
        private readonly RevisionLedger _appliedPersistentRevisions = new RevisionLedger();
        // Reliable packets rejected by transport backpressure are gone for good. Once the
        // backlog drains, the host replays idempotent world state instead of leaving the
        // client permanently behind.
        private long _observedDroppedReliable;
        private bool _resyncAfterBacklog;        private readonly Queue<ulong> _persistentStateOrder = new Queue<ulong>();
        private readonly Dictionary<ulong, PersistentStatePacket> _persistentLastValue = new Dictionary<ulong, PersistentStatePacket>();
        // States that arrived while their scene was not loaded. They are retried after a
        // scene change instead of being dropped; a late joiner in another room otherwise
        // never learns about doors/levers/chests the host already changed.
        private readonly Dictionary<ulong, PersistentStatePacket> _pendingPersistentStates = new Dictionary<ulong, PersistentStatePacket>();
        private readonly Queue<ulong> _pendingPersistentOrder = new Queue<ulong>();
        private readonly Dictionary<ulong, WorldEventPacket> _pendingWorldEvents = new Dictionary<ulong, WorldEventPacket>();
        private readonly Dictionary<ulong, NetworkPeerId> _pendingWorldEventSenders = new Dictionary<ulong, NetworkPeerId>();
        private readonly Queue<ulong> _pendingWorldEventOrder = new Queue<ulong>();
        private Dictionary<ulong, PersistentItem<bool>>? _boolItems;
        private Dictionary<ulong, PersistentItem<int>>? _intItems;
        private string _persistentIndexScene = string.Empty;
        private bool _persistentIndexDirty;
        private float _pendingPersistentRetryAt;
        private float _pendingWorldRetryAt;
        private float _nextAmbiguousMatchLogAt;
        private int _suppressedAmbiguousMatchLogs;
        private float _nextEnemyLookupFailureLogAt;

        // Persistent bool/int items are polled once per second. The SaveState hook covers
        // the common path; polling catches doors/chests/lifts that mutate their value
        // without calling SaveState immediately, plus items whose script drives them.
        private float _persistentPollAt;
        private const float PersistentPollInterval = 1f;

        private static System.Reflection.FieldInfo? _tookDamageField;
        private static bool _tookDamageFieldResolved;
        private static bool _phaseNotifyFailureLogged;

        private const int MaxPendingPersistentStates = 4096;
        private const int MaxPendingWorldEvents = 1024;

        private readonly Queue<ReplayItem> _replayQueue = new Queue<ReplayItem>();
        private float _nextReplaySendTime;
        private bool _worldReplayPending;

        private string _sceneName = string.Empty;
        private int _sceneHandle = -1;
        private float _snapshotTimer;
        private uint _packetSequence;

        // Snapshots are normally change-driven. A periodic reconciliation resends every
        // damaged/dead enemy even if an earlier send was dropped by the transport, and a
        // newly connected peer requests an immediate one so it does not wait for a change.
        private const float SnapshotReconcileInterval = 10f;
        private float _snapshotReconcileAt;
        private bool _forceSnapshotReconcile;

        private static AccessTools.FieldRef<HealthManager, int>? _initHpRef;
        private static bool _initHpResolved;
        private static bool _bossProbeWarningLogged;

        // Edge-triggered boss-sequence tracking: sequences reuse boss hierarchy keys, so
        // death records created during one attempt must not survive into the next.
        private bool _wasInBossSequence;

        /// <summary>
        /// Binds the controller to the plugin.
        /// </summary>
        public void Initialize(SilksongCoopPlugin plugin)
        {
            _plugin = plugin;
        }

        /// <summary>
        /// Clears all per-session state. Host snapshot timestamps restart when the host
        /// process restarts, so stale entries must not reject the new session's packets.
        /// </summary>
        public void ResetSession()
        {
            RefreshSaveScope();
            _service.Reset();
            _keyCache = new ConditionalWeakTable<HealthManager, KeyHolder>();
            _lastSnapshot.Clear();
            _enemyIndex.Clear();
            _enemyIndexDirty = true;
            ClearWorldObjectCaches();
            _snapshotTimer = 0f;
            _snapshotReconcileAt = 0f;
            _forceSnapshotReconcile = false;

            // Do NOT clear _sceneName here. Forcing a scene change on the next Update
            // used to wipe the world-event/dead-enemy catch-up registries exactly when a
            // new client connected, before they could be replayed.

            // The dead/world registries deliberately survive a reconnect: the next host
            // snapshot/world replay catches a newly connected client up.
            _replayQueue.Clear();
            _nextReplaySendTime = 0f;
            _worldReplayPending = true;

            _pendingPersistentStates.Clear();
            _pendingPersistentOrder.Clear();
            _pendingWorldEvents.Clear();
            _pendingWorldEventSenders.Clear();
            _pendingWorldEventOrder.Clear();
            _pendingDeadEnemies.Clear();
            _pendingDeadOrder.Clear();
            _pendingDeadRetryAt = 0f;
            // A new session may come from a different host whose revisions restart from a
            // lower value, so applied revisions must not carry over.
            _appliedWorldRevisions.Clear();
            _appliedPersistentRevisions.Clear();
        }

        /// <summary>
        /// Clears replay registries when the game moved to a different save. Called on the
        /// cadence of Update and before any catch-up replay, so a switched save can never
        /// send the previous playthrough's doors, gates or boss deaths to a partner.
        /// </summary>
        private void RefreshSaveScope()
        {
            int profileId = 0;
            PlayerData? playerData = null;
            try
            {
                var gameManager = GameManager.instance;
                if (gameManager != null)
                    profileId = gameManager.profileID;

                // Only touch the PlayerData getter once a save is loaded. At the menu the
                // getter would create a default singleton as a side effect.
                if (profileId > 0)
                    playerData = PlayerData.instance;
            }
            catch
            {
                // Game singletons can disappear during teardown; keep the previous scope.
                return;
            }

            if (_saveScope.Observe(profileId, playerData))
            {
                ClearSaveScopedReplayState();
                SilksongCoopPlugin.LogInfo(
                    "[EnemySync] Save scope changed; cleared replay registries for the new playthrough");
            }
        }

        private void ClearSaveScopedReplayState()
        {
            _service.Reset();
            _lastSnapshot.Clear();
            _enemyIndex.Clear();
            _enemyIndexDirty = true;
            _deadRegistry.Clear();
            _worldEvents.Clear();
            _worldEventOrder.Clear();
            _persistentStates.Clear();
            _persistentStateOrder.Clear();
            _persistentLastValue.Clear();
            _pendingPersistentStates.Clear();
            _pendingPersistentOrder.Clear();
            _pendingWorldEvents.Clear();
            _pendingWorldEventSenders.Clear();
            _pendingWorldEventOrder.Clear();
            _pendingDeadEnemies.Clear();
            _pendingDeadOrder.Clear();
            _replayQueue.Clear();
            _appliedWorldRevisions.Clear();
            _appliedPersistentRevisions.Clear();
            ClearWorldObjectCaches();
            _boolItems = null;
            _intItems = null;
            _persistentIndexScene = string.Empty;
            _persistentIndexDirty = true;
            _worldReplayPending = true;
            _forceSnapshotReconcile = true;
        }

        private void Update()
        {
            // A save switch/reload invalidates every replay registry before it can be sent
            // to a new partner.
            RefreshSaveScope();

            // Repeatable boss sequences spawn the same hierarchy keys again; clear their
            // old death records as soon as the sequence ends, on both host and client.
            TrackBossSequenceTransition();

            // Track scene changes even while disconnected. Otherwise the first connection
            // looked like a scene change (empty _sceneName) and cleared dead enemies that
            // were recorded while playing solo, just before their catch-up replay.
            DetectSceneChange();

            // Poll even while solo: a change made before connecting is stored in the
            // persistent replay cache and sent to the partner on join.
            PollPersistentStateChanges();

            if (_plugin?.SessionState?.IsConnected != true) return;

            PumpPendingPersistentStates();
            PumpPendingWorldEvents();
            PumpPendingDeadEnemies();
            TrackReliableDrops();

            // Only the host is authoritative for periodic snapshots and world catch-up.
            if (_plugin.SessionState.IsHost != true) return;

            if (_worldReplayPending)
            {
                _worldReplayPending = false;
                QueueWorldReplay();
            }

            PumpWorldReplay();

            _snapshotTimer += Time.unscaledDeltaTime;

            bool reconcile = _forceSnapshotReconcile ||
                             Time.unscaledTime >= _snapshotReconcileAt;
            if (_snapshotTimer < 1f && !reconcile) return;

            _snapshotTimer = 0f;
            if (reconcile)
            {
                _forceSnapshotReconcile = false;
                _snapshotReconcileAt = Time.unscaledTime + SnapshotReconcileInterval;
            }

            SendSnapshot(reconcile);
        }

        #region Enemy hits

        /// <summary>
        /// Called by the HealthManager.Hit postfix after the local player damaged an enemy.
        /// </summary>
        public void OnLocalHit(HealthManager target, HitInstance hit, int hpAfter, bool dead)
        {
            if (target == null) return;
            if (_plugin?.SessionState?.IsConnected != true) return;

            // A lethal hit is also a death record; use a fresh key so it cannot disagree
            // with OnLocalEnemyDeath (which must bypass the fingerprint cache).
            ulong key = dead ? GetFreshKey(target) : GetKey(target);
            if (key == 0) return;
            _enemyIndex[key] = target;

            // Keep the local HP baseline in the same bookkeeping as remote hits, so the
            // additive path knows the enemy was at 100 before this 10-damage hit.
            _service.NoteLocalEnemyState(key, dead ? 0 : Math.Max(0, hpAfter), dead);

            var packet = new EnemySyncPacket
            {
                Header = new PacketHeader(PacketType.EnemySync, WorldChannel, ++_packetSequence, NetworkClock.NowMs),
                EnemyKey = key,
                Hp = dead ? 0 : Math.Max(0, hpAfter),
                Damage = (short)Math.Clamp(hit.DamageDealt, 0, short.MaxValue),
                PositionX = target.transform.position.x,
                PositionY = target.transform.position.y,
                EventType = dead ? EnemySyncEventType.Death : EnemySyncEventType.Hit,
                AttackType = (byte)Math.Clamp((int)hit.AttackType, 0, 255)
            };

            SendEnemyPacket(packet);
        }

        /// <summary>
        /// Called when a local enemy dies from any source (including non-hero damage).
        /// </summary>
        public void OnLocalEnemyDeath(HealthManager healthManager)
        {
            if (healthManager == null) return;

            var target = healthManager.SendDamageTo ?? healthManager;
            if (target == null) return;

            // Death records survive scene changes and reconnect, so use a freshly computed
            // key rather than a cache entry that may predate an ancestor rename/pool reuse.
            ulong key = GetFreshKey(target);
            if (key == 0) return;
            _enemyIndex[key] = target;

            _service.NoteLocalEnemyState(key, 0, dead: true);

            // Register even before a peer connects so a later client is caught up. Bosses
            // are flagged here, while the scene is still loaded, because the registry uses
            // that flag to survive the scene change that follows a boss kill. A boss that
            // belongs to a repeatable sequence is tracked separately: it must disappear
            // from catch-up data when the sequence ends.
            bool sceneBoss = IsBossSceneBoss(target) || IsBossSceneBoss(healthManager);
            bool sequenceBoss = !sceneBoss && IsInBossSequence();
            _deadRegistry.Register(
                key,
                target.transform.position.x,
                target.transform.position.y,
                sceneBoss || sequenceBoss,
                sequenceBoss);

            if (_plugin?.SessionState?.IsConnected != true) return;

            var packet = new EnemySyncPacket
            {
                Header = new PacketHeader(PacketType.EnemySync, WorldChannel, ++_packetSequence, NetworkClock.NowMs),
                EnemyKey = key,
                Hp = 0,
                Damage = 0,
                PositionX = target.transform.position.x,
                PositionY = target.transform.position.y,
                EventType = EnemySyncEventType.Death,
                AttackType = (byte)AttackTypes.Nail
            };

            SendEnemyPacket(packet);
        }

        /// <summary>
        /// Applies a hit/death packet from the peer.
        /// </summary>
        /// <param name="packet">Hit or death event.</param>
        /// <param name="sender">
        /// Peer that sent the event. Used to prove the sender is actually in the local
        /// scene before any position-based fallback is allowed.
        /// </param>
        public void ApplyRemoteHit(EnemySyncPacket packet, NetworkPeerId sender)
        {
            bool dead = packet.EventType == EnemySyncEventType.Death;
            var session = _plugin?.SessionState;
            bool localHost = session?.IsHost == true;

            // A death must match the exact object: the packet may describe an enemy in a
            // scene this peer has not loaded, and the proximity fallback could kill an
            // unrelated enemy standing at similar coordinates in the current scene.
            // Hits keep the fallback to tolerate a hierarchy-hash drift, but only while
            // the sender is confirmed to be in this scene; otherwise a hit from another
            // room could damage a local enemy that happens to share the coordinates.
            bool sameScene = IsSenderInLocalScene(sender);
            var target = FindEnemy(
                packet.EnemyKey,
                packet.PositionX,
                packet.PositionY,
                allowProximity: !dead && sameScene);
            if (target == null)
            {
                // Cache a death for a not-yet-loaded enemy instead of recording it as
                // applied. Recording it here used to lose the kill forever and made the
                // later duplicate look like a stale packet.
                if (dead && packet.EnemyKey != 0)
                {
                    // The host is authoritative for every enemy it simulates. When the
                    // enemy is not loaded there is no host HP to merge, so a client's kill
                    // claim must at least prove that a damaging hit happened. A zero-damage
                    // claim used to be cached and then applied as an authoritative snapshot
                    // when the scene loaded, bypassing the hosted HP check entirely.
                    if (localHost && packet.Damage <= 0)
                    {
                        SilksongCoopPlugin.LogWarn(
                            $"[EnemySync] Ignored deferred client death claim for {packet.EnemyKey}: no damage");
                        return;
                    }

                    CachePendingDeadEnemy(
                        new EnemySnapshotEntry
                        {
                            EnemyKey = packet.EnemyKey,
                            Hp = 0,
                            Flags = EnemySnapshotEntry.FlagDead,
                            PositionX = packet.PositionX,
                            PositionY = packet.PositionY
                        },
                        packet.Header.Timestamp,
                        packet.Damage,
                        fromRemoteClaim: true);
                }
                return;
            }

            // The authority model is defined in docs/state-authority.md. While the host
            // actively simulates this enemy, its absolute HP is authoritative and every
            // other peer's hit is deferred: the host already received that hit and its
            // snapshot contains it, so applying it again would double-count the damage (or
            // let a client declare a death the host has not confirmed). The legacy
            // clamp exists only for callers that cannot determine the sender role.
            RemoteHitPolicy policy;
            if (localHost)
            {
                policy = RemoteHitPolicy.LocalHost;
            }
            else if (!_service.HasFreshHostAuthority(packet.EnemyKey))
            {
                policy = RemoteHitPolicy.NoAuthority;
            }
            else if (session?.RemotePeerId.HasValue == true &&
                     session.RemotePeerId.Value == sender)
            {
                policy = RemoteHitPolicy.Authoritative;
            }
            else
            {
                policy = RemoteHitPolicy.Deferred;
            }

            bool wasDead = target.GetIsDead();

            // The host merges every client claim into its own live HP. Seed the service
            // with the value it is actually simulating so a divergent client cannot lower
            // or kill the enemy on its own; this is not a local hit event.
            if (localHost && !wasDead)
                _service.NoteCurrentEnemyState(packet.EnemyKey, target.hp, dead: false);

            // Only mark the packet as applied once the target was found. Otherwise the
            // service would reject every retry even though nothing was applied locally.
            if (!_service.TryAcceptRemoteHit(
                    packet.EnemyKey,
                    packet.Hp,
                    dead,
                    packet.Damage,
                    sender.Value,
                    packet.Header.TickSequence,
                    policy,
                    out int appliedHp))
            {
                return;
            }

            // The service already decided whether the death is confirmed. A host that
            // downgraded a client's Death claim to ordinary damage returns HP > 0 here,
            // so `dead` must not be used directly or the host would kill itself.
            bool appliedDeath = appliedHp <= 0;
            ulong appliedKey = ApplyEnemyState(
                packet.EnemyKey, target, appliedHp, appliedDeath, authoritative: false);

            // The host resolved combined lethal damage: broadcast the death so clients that
            // clamped to 1 HP do not leave a zero-health enemy alive. Use the key of the
            // object that actually died, not a proximity-match packet key.
            if (localHost && appliedDeath && !wasDead)
            {
                var deathPacket = packet;
                if (appliedKey != 0) deathPacket.EnemyKey = appliedKey;
                deathPacket.Header = new PacketHeader(
                    PacketType.EnemySync,
                    WorldChannel,
                    ++_packetSequence,
                    NetworkClock.NowMs);
                deathPacket.Hp = 0;
                deathPacket.Damage = 0;
                deathPacket.EventType = EnemySyncEventType.Death;
                SendEnemyPacket(deathPacket);
            }
        }

        /// <returns>
        /// The key of the object that died, or 0 when the target is still alive. The host
        /// uses it to broadcast a death for the object it actually resolved.
        /// </returns>
        private ulong ApplyEnemyState(ulong enemyKey, HealthManager target, int hp, bool dead, bool authoritative)
        {
            if (target == null || target.GetIsDead()) return 0;

            using (RemoteApplyScope.Begin())
            {
                try
                {
                    if (dead || hp <= 0)
                    {
                        // Record the kill even when it came from a remote packet. The host
                        // that resolves a client's lethal hit must keep the death for late
                        // joiners after the enemy object disappears from the active index.
                        // Prefer the target's own key over a proximity-match packet key.
                        ulong actualKey = GetFreshKey(target);
                        if (actualKey != 0) enemyKey = actualKey;
                        bool sceneBoss = IsBossSceneBoss(target);
                        bool sequenceBoss = !sceneBoss && IsInBossSequence();
                        _deadRegistry.Register(
                            enemyKey,
                            target.transform.position.x,
                            target.transform.position.y,
                            sceneBoss || sequenceBoss,
                            sequenceBoss);
                        target.hp = 0;
                        target.Die(null, AttackTypes.Nail, ignoreEvasion: true);
                        return enemyKey;
                    }

                    int next = authoritative ? hp : Math.Min(target.hp, hp);
                    int before = target.hp;
                    target.hp = Math.Max(1, next);

                    // A replicated hit (or a downward snapshot correction) must also drive
                    // the boss's phase FSM. Setting hp alone leaves PlayMaker bosses one
                    // phase behind: their transitions listen for the "TOOK DAMAGE" event
                    // that the local TakeDamage path emits. Re-emit the same signals
                    // without applying damage twice. Snapshots that merely confirm the
                    // current HP do not lower it, so they cannot re-trigger a phase.
                    if (target.hp < before)
                    {
                        NotifyBossTookDamage(target);
                    }
                }
                catch (Exception ex)
                {
                    SilksongCoopPlugin.LogWarn($"[EnemySync] Failed applying enemy state: {ex.Message}");
                }
            }

            return 0;
        }

        /// <summary>
        /// Re-emits the damage signals that <c>HealthManager.TakeDamage</c> would have
        /// produced locally: the C# <c>TookDamage</c> event and the PlayMaker
        /// <c>TOOK DAMAGE</c> event. Only called for replicated hits on bosses, where the
        /// phase state machine otherwise lags one hit behind the shared HP bar.
        /// </summary>
        private static void NotifyBossTookDamage(HealthManager target)
        {
            if (target == null || !IsBoss(target)) return;

            try
            {
                if (!_tookDamageFieldResolved)
                {
                    _tookDamageFieldResolved = true;
                    _tookDamageField = AccessTools.Field(typeof(HealthManager), "TookDamage");
                }

                if (_tookDamageField?.GetValue(target) is Action handlers)
                {
                    handlers.Invoke();
                }

                FSMUtility.SendEventToGameObject(target.gameObject, "TOOK DAMAGE");
            }
            catch (Exception ex)
            {
                if (!_phaseNotifyFailureLogged)
                {
                    _phaseNotifyFailureLogged = true;
                    SilksongCoopPlugin.LogWarn($"[EnemySync] Boss phase notification failed: {ex.Message}");
                }
            }
        }

        private void SendEnemyPacket(EnemySyncPacket packet)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(64);
            try
            {
                var writer = new PacketWriter(buffer);
                packet.Serialize(ref writer);
                SendBuffer(buffer, writer.Position);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[EnemySync] Failed sending enemy packet: {ex.Message}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        #endregion

        #region Snapshots

        /// <summary>
        /// Asks the host to send a full snapshot and world replay to every peer. Called when
        /// a new peer connects: the registries already hold past world changes, but without
        /// clearing the sent markers the new peer would only receive future changes.
        /// Existing peers merely receive idempotent repeats.
        /// </summary>
        public void BeginCatchUpForNewPeer()
        {
            RefreshSaveScope();
            _lastSnapshot.Clear();
            _worldReplayPending = true;
            _forceSnapshotReconcile = true;
        }

        /// <summary>
        /// Detects reliable packets rejected by transport backpressure and schedules an
        /// idempotent replay once the backlog has drained. Without this, a burst that fills
        /// the reliable queue leaves world state permanently behind even though the
        /// connection itself recovers.
        /// </summary>
        private void TrackReliableDrops()
        {
            if (_plugin?.ActiveTransport is not IReliableDropDiagnostics diagnostics)
                return;

            long dropped = diagnostics.DroppedReliablePackets;
            if (dropped < _observedDroppedReliable)
            {
                // A new transport instance starts its counter from zero.
                _observedDroppedReliable = dropped;
            }
            else if (dropped > _observedDroppedReliable)
            {
                _observedDroppedReliable = dropped;
                _resyncAfterBacklog = true;
            }

            if (!_resyncAfterBacklog) return;

            // Do not add more traffic while the reliable queue is still near its cap.
            if (_plugin.ActiveTransport.ReliableBacklogCount > NetworkLimits.ResyncBacklogThreshold) return;

            _resyncAfterBacklog = false;
            if (_plugin.SessionState?.IsHost != true) return;

            _worldReplayPending = true;
            _forceSnapshotReconcile = true;
            SilksongCoopPlugin.LogInfo("[EnemySync] Reliable backlog recovered; replaying world state");
        }

        private void SendSnapshot(bool reconcile)
        {
            // The index is also our active-enemy enumeration, so one rebuild replaces the
            // old "copy to List<HealthManager>" and every FindEnemy scan for this snapshot.
            RebuildEnemyIndex();
            _snapshotBatch.Clear();

            foreach (var pair in _enemyIndex)
            {
                var hm = pair.Value;
                if (hm == null) continue;

                ulong key = pair.Key;
                int hp = Math.Max(0, hm.hp);
                bool dead = hm.GetIsDead();
                int initHp = Math.Max(1, GetInitHp(hm));

                bool known = _lastSnapshot.TryGetValue(key, out var last);
                bool wasDead = known && last.Dead;

                // The policy is shared with Core tests: first-seen, damaged, changed and
                // newly-dead enemies are sent; a reconciliation pass sends everything.
                if (!EnemySnapshotSendPolicy.ShouldSend(
                        reconcile, known, last.Hp, hp, initHp, dead, wasDead))
                {
                    continue;
                }

                // One transform read per emitted enemy instead of two property accesses.
                Vector3 position = hm.transform.position;
                _snapshotBatch.Add(new EnemySnapshotEntry
                {
                    EnemyKey = key,
                    Hp = dead ? 0 : hp,
                    Flags = dead ? EnemySnapshotEntry.FlagDead : (byte)0,
                    PositionX = position.x,
                    PositionY = position.y
                });

                if (_snapshotBatch.Count >= EnemySnapshotPacket.MaxEntries)
                    FlushSnapshotBatch();
            }

            // Enemies killed before the current client connected may already be gone from
            // EnumerateActiveEnemies(). Replay them from the local death registry. Boss
            // records survive scene changes, so this also covers a late joiner who enters
            // the boss room after the host has already left it.
            foreach (var dead in _deadRegistry.Records)
            {
                if (!reconcile &&
                    _lastSnapshot.TryGetValue(dead.Key, out var alreadySent) && alreadySent.Dead)
                {
                    continue;
                }

                _snapshotBatch.Add(new EnemySnapshotEntry
                {
                    EnemyKey = dead.Key,
                    Hp = 0,
                    Flags = EnemySnapshotEntry.FlagDead,
                    PositionX = dead.Value.X,
                    PositionY = dead.Value.Y
                });

                if (_snapshotBatch.Count >= EnemySnapshotPacket.MaxEntries)
                    FlushSnapshotBatch();
            }

            FlushSnapshotBatch();
        }

        private void FlushSnapshotBatch()
        {
            if (_snapshotBatch.Count == 0) return;

            if (SendSnapshotBatch(_snapshotBatch))
            {
                MarkSnapshotBatchSent(_snapshotBatch);
            }
            _snapshotBatch.Clear();
        }

        /// <summary>
        /// Records a batch as sent only after serialization and the transport call
        /// succeeded. Marking before the send meant a single failure suppressed that enemy
        /// for the rest of the session.
        /// </summary>
        private void MarkSnapshotBatchSent(List<EnemySnapshotEntry> batch)
        {
            for (int i = 0; i < batch.Count; i++)
            {
                var entry = batch[i];
                _lastSnapshot[entry.EnemyKey] = new SnapshotState
                {
                    Hp = entry.Hp,
                    Dead = entry.IsDead
                };
            }
        }

        private bool SendSnapshotBatch(List<EnemySnapshotEntry> batch)
        {
            int count = Math.Min(batch.Count, _snapshotEntryBuffer.Length);
            for (int i = 0; i < count; i++)
            {
                _snapshotEntryBuffer[i] = batch[i];
            }

            var packet = new EnemySnapshotPacket
            {
                Header = new PacketHeader(PacketType.EnemySnapshot, WorldChannel, ++_packetSequence, NetworkClock.NowMs),
                Entries = _snapshotEntryBuffer,
                Count = count
            };

            byte[] buffer = ArrayPool<byte>.Shared.Rent(1024);
            try
            {
                var writer = new PacketWriter(buffer);
                packet.Serialize(ref writer);
                return SendBuffer(buffer, writer.Position, requireReliableCapacity: true);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[EnemySync] Failed sending snapshot: {ex.Message}");
                return false;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        /// <summary>
        /// Rebuilds the exact-key enemy index for the active scene. Called at the start of
        /// each host snapshot and lazily on a client when the scene changed.
        /// </summary>
        private void RebuildEnemyIndex()
        {
            RefreshKeyCacheIfNeeded();
            _enemyIndex.Clear();
            _enemyIndexDirty = true;
            try
            {
                foreach (var hm in HealthManager.EnumerateActiveEnemies())
                {
                    if (hm == null) continue;
                    ulong key = GetKey(hm);
                    if (key != 0) _enemyIndex[key] = hm;
                }
                _enemyIndexDirty = false;
                _enemyIndexFailureLogged = false;
                _nextEnemyIndexRebuildAt = Time.unscaledTime + 5f;
            }
            catch (Exception ex)
            {
                // Keep the fallback scan working and avoid rebuilding on every hit; the
                // next host snapshot retries. Log once per failure streak.
                _enemyIndex.Clear();
                _enemyIndexDirty = false;
                _nextEnemyIndexRebuildAt = Time.unscaledTime + 5f;
                if (!_enemyIndexFailureLogged)
                {
                    _enemyIndexFailureLogged = true;
                    SilksongCoopPlugin.LogWarn($"[EnemySync] Enemy index rebuild failed: {ex.Message}");
                }
            }
        }

        private void EnsureEnemyIndex()
        {
            // Clients do not send snapshots, so periodically rebuild to drop dead/inactive
            // entries and pick up dynamically spawned enemies. Hosts also rebuild every
            // snapshot, which resets this deadline sooner.
            RefreshKeyCacheIfNeeded();
            if (_enemyIndexDirty || Time.unscaledTime >= _nextEnemyIndexRebuildAt)
            {
                RebuildEnemyIndex();
            }
        }

        /// <summary>
        /// Applies an authoritative host snapshot (client side only). Position-based
        /// matching of live entries is allowed only while the host is in the same scene,
        /// so an entry describing another room cannot correct the HP of a local enemy at
        /// the same coordinates.
        /// </summary>
        /// <param name="packet">Snapshot batch.</param>
        /// <param name="sender">Host peer that produced the snapshot.</param>
        public void ApplyRemoteSnapshot(EnemySnapshotPacket packet, NetworkPeerId sender)
        {
            // Only the host produces authoritative snapshots. A client that sends one must
            // not be able to rewrite enemy HP or mark the host authority for another peer.
            var snapshotSession = _plugin?.SessionState;
            if (snapshotSession?.IsHost == true) return;
            if (snapshotSession?.RemotePeerId.HasValue == true &&
                sender != snapshotSession.RemotePeerId.Value)
            {
                return;
            }

            if (_plugin?.SessionState?.IsHost == true) return;
            if (packet.Entries == null) return;

            bool sameScene = IsSenderInLocalScene(sender);
            EnsureEnemyIndex();
            int count = Math.Min(packet.Count, packet.Entries.Length);
            for (int i = 0; i < count; i++)
            {
                var entry = packet.Entries[i];
                var target = FindEnemy(
                    entry.EnemyKey,
                    entry.PositionX,
                    entry.PositionY,
                    allowProximity: !entry.IsDead && sameScene);

                // A dead boss may belong to a scene this peer has not loaded yet. Cache the
                // death and apply it when that scene finishes loading; the snapshot itself
                // is only sent once per session, so dropping it here loses the kill forever.
                if (target == null)
                {
                    if (entry.IsDead)
                        CachePendingDeadEnemy(entry, packet.Header.Timestamp);
                    else
                        _pendingDeadEnemies.Remove(entry.EnemyKey);
                    continue;
                }

                // A fresh snapshot for the loaded object supersedes a cached death (for
                // example a boss-rush encounter that respawned the same boss).
                _pendingDeadEnemies.Remove(entry.EnemyKey);

                if (!_service.TryAcceptSnapshot(entry.EnemyKey, entry.Hp, entry.IsDead, packet.Header.Timestamp))
                    continue;

                ApplyEnemyState(entry.EnemyKey, target, entry.Hp, entry.IsDead, authoritative: true);
            }

            if (_pendingDeadEnemies.Count > 0)
                _pendingDeadRetryAt = Time.unscaledTime + 0.25f;
        }

        #endregion

        #region World events

        /// <summary>
        /// Broadcasts a permanent world change (breakable wall / opened gate).
        /// </summary>
        public void OnLocalWorldEvent(WorldEventType eventType, Transform? transform)
        {
            if (transform == null) return;

            // World events are permanent replay records; use a fresh key so a rename within
            // the cache window cannot create a key that peers can never resolve.
            ulong key = WorldObjectKey.ComputeFresh(transform);
            if (key == 0) return;

            var packet = new WorldEventPacket
            {
                Header = new PacketHeader(PacketType.WorldEvent, WorldChannel, ++_packetSequence, NetworkClock.NowMs),
                ObjectKey = key,
                EventType = eventType,
                PositionX = transform.position.x,
                PositionY = transform.position.y,
                StateRevision = _packetSequence == 0 ? ++_packetSequence : _packetSequence,
                OriginPeerId = _revisionOrigin
            };

            // Register even before a peer connects so a later client is caught up.
            RecordWorldEventForReplay(packet);

            if (_plugin?.SessionState?.IsConnected != true) return;

            SendWorldPacket(packet);
        }

        /// <summary>
        /// Stores a world event for late-join replay. Used both for local events and for
        /// events the host applied on behalf of another peer, so a third player who joins
        /// later still receives the opened gate/breakable.
        /// </summary>
        private void RecordWorldEventForReplay(WorldEventPacket packet)
        {
            if (packet.ObjectKey == 0) return;

            if (!_worldEvents.ContainsKey(packet.ObjectKey))
            {
                while (_worldEvents.Count >= 1024 && _worldEventOrder.Count > 0)
                {
                    ulong oldest = _worldEventOrder.Dequeue();
                    if (_worldEvents.Remove(oldest)) break;
                }
                _worldEventOrder.Enqueue(packet.ObjectKey);
            }
            _worldEvents[packet.ObjectKey] = packet;
        }

        /// <summary>
        /// Snapshots the current world registries into the replay queue. The queue is sent
        /// gradually by <see cref="PumpWorldReplay"/> so a late joiner can never overflow
        /// the sender's reliable window.
        /// </summary>
        private void QueueWorldReplay()
        {
            _replayQueue.Clear();

            foreach (var stored in _worldEvents.Values)
            {
                _replayQueue.Enqueue(new ReplayItem { IsPersistent = false, World = stored });
            }

            foreach (var stored in _persistentStates.Values)
            {
                _replayQueue.Enqueue(new ReplayItem { IsPersistent = true, Persistent = stored });
            }

            _nextReplaySendTime = 0f;
        }

        /// <summary>
        /// Sends a bounded batch of queued world-state packets, pausing while the transport
        /// still has many unacknowledged reliable packets. This keeps the stream contiguous
        /// (the transport drops new reliable packets once its 512-packet window is full).
        /// </summary>
        private void PumpWorldReplay()
        {
            if (_replayQueue.Count == 0) return;
            if (Time.unscaledTime < _nextReplaySendTime) return;

            var transport = _plugin?.ActiveTransport;
            if (transport == null) return;

            if (transport.ReliableBacklogCount >= ReplayBacklogThreshold)
            {
                _nextReplaySendTime = Time.unscaledTime + 0.05f;
                return;
            }

            for (int i = 0; i < ReplayBatchSize && _replayQueue.Count > 0; i++)
            {
                // Peek, then dequeue only after the transport accepted the packet. A refused
                // send previously lost the item for this replay pass; pausing keeps it for
                // the next pump (and _worldReplayPending still re-queues everything after a
                // reconnect or a detected reliable drop).
                var item = _replayQueue.Peek();
                bool sent;
                if (item.IsPersistent)
                {
                    var stored = item.Persistent;
                    sent = SendPersistentPacket(new PersistentStatePacket
                    {
                        Header = new PacketHeader(PacketType.PersistentState, WorldChannel, 0, NetworkClock.NowMs),
                        ItemKey = stored.ItemKey,
                        ValueType = stored.ValueType,
                        Value = stored.Value,
                        StateRevision = stored.StateRevision,
                        OriginPeerId = stored.OriginPeerId
                    });
                }
                else
                {
                    var stored = item.World;
                    sent = SendWorldPacket(new WorldEventPacket
                    {
                        Header = new PacketHeader(PacketType.WorldEvent, WorldChannel, ++_packetSequence, NetworkClock.NowMs),
                        ObjectKey = stored.ObjectKey,
                        EventType = stored.EventType,
                        PositionX = stored.PositionX,
                        PositionY = stored.PositionY,
                        StateRevision = stored.StateRevision,
                        OriginPeerId = stored.OriginPeerId
                    });
                }

                if (!sent)
                {
                    // Backpressure or a refused send: retry this batch shortly instead of
                    // advancing past unsent state.
                    _nextReplaySendTime = Time.unscaledTime + 0.05f;
                    break;
                }
                _replayQueue.Dequeue();

                if (transport.ReliableBacklogCount >= ReplayBacklogThreshold)
                    break;
            }

            // ~16 packets every 20 ms: fast enough for a late join, slow enough that the
            // receiver's rate limiter and the sender's retransmit window are not flooded.
            _nextReplaySendTime = Time.unscaledTime + 0.02f;
        }

        /// <summary>True when the transport accepted the packet for delivery.</summary>
        private bool SendWorldPacket(WorldEventPacket packet)
        {
            var transport = _plugin?.ActiveTransport;
            if (transport == null || !transport.IsHost || packet.OriginPeerId != _revisionOrigin) return false;
            packet.OriginPeerId = transport.LocalPeerId.Value;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(64);
            try
            {
                var writer = new PacketWriter(buffer);
                packet.Serialize(ref writer);
                return SendBuffer(buffer, writer.Position);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[EnemySync] Failed sending world event: {ex.Message}");
                return false;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        /// <summary>
        /// Applies a permanent world change from the peer. If the object lives in a scene that
        /// is not loaded right now, the event is cached and retried after the next transition.
        /// </summary>
        /// <param name="packet">World event to apply.</param>
        /// <param name="sender">
        /// Peer that sent the event. Position-based matching is only allowed while that peer
        /// is confirmed to be in the local scene.
        /// </param>
        public void ApplyRemoteWorldEvent(WorldEventPacket packet, NetworkPeerId sender)
        {
            if (RemoteApplyScope.IsActive) return;
            if (packet.ObjectKey == 0 || packet.OriginPeerId == _revisionOrigin) return;

            // Idempotency: drop stale or duplicate revisions. A revision is only meaningful
            // together with its origin, and revision 0 means "unversioned" (legacy sender).
            if (_appliedWorldRevisions.IsStale(packet.ObjectKey, packet.StateRevision, packet.OriginPeerId))
                return;

            bool allowProximity = IsSenderInLocalScene(sender);
            if (!TryApplyWorldEvent(packet, allowProximity))
            {
                CachePendingWorldEvent(packet, sender);
            }
            else
            {
                _appliedWorldRevisions.Record(packet.ObjectKey, packet.StateRevision, packet.OriginPeerId);
            }
        }

        private bool TryApplyWorldEvent(WorldEventPacket packet, bool allowProximity)
        {
            try
            {
                EnsureWorldIndexes();

                using (RemoteApplyScope.Begin())
                {
                    switch (packet.EventType)
                    {
                        case WorldEventType.BreakableBroken:
                            var breakable = FindIndexedWorldObject(_breakableIndex, packet.ObjectKey);
                            if (breakable == null)
                            {
                                // Index miss: preserve the full exact-key/proximity/ambiguity
                                // behavior by running the original scan, then remember the hit.
                                breakable = FindObjectByKey<Breakable>(
                                    packet.ObjectKey, packet.PositionX, packet.PositionY, allowProximity);
                                if (breakable != null) _breakableIndex[packet.ObjectKey] = breakable;
                            }
                            if (breakable == null) return false;
                            breakable.BreakSelf();
                            return true;

                        case WorldEventType.GateOpened:
                            var gate = FindIndexedWorldObject(_gateIndex, packet.ObjectKey);
                            if (gate == null)
                            {
                                gate = FindObjectByKey<Gate>(
                                    packet.ObjectKey, packet.PositionX, packet.PositionY, allowProximity);
                                if (gate != null) _gateIndex[packet.ObjectKey] = gate;
                            }
                            if (gate == null) return false;
                            gate.Open();
                            return true;
                    }
                }

                // Unknown event types are validated away by PacketValidator; drop safely.
                return true;
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[EnemySync] Failed applying world event: {ex.Message}");
                return false;
            }
        }

        private void CachePendingWorldEvent(WorldEventPacket packet, NetworkPeerId sender)
        {
            if (!_pendingWorldEvents.ContainsKey(packet.ObjectKey))
            {
                while (_pendingWorldEvents.Count >= MaxPendingWorldEvents &&
                       _pendingWorldEventOrder.Count > 0)
                {
                    ulong oldest = _pendingWorldEventOrder.Dequeue();
                    if (_pendingWorldEvents.Remove(oldest))
                    {
                        _pendingWorldEventSenders.Remove(oldest);
                        break;
                    }
                }

                _pendingWorldEventOrder.Enqueue(packet.ObjectKey);
            }

            // Later events for the same object supersede earlier ones, but only if their
            // revision is newer (idempotency guard). Revisions from different origins are
            // not comparable, so an update from another peer must replace the cached one.
            if (!_pendingWorldEvents.TryGetValue(packet.ObjectKey, out var cached) ||
                cached.StateRevision == 0 || packet.StateRevision == 0 ||
                cached.OriginPeerId != packet.OriginPeerId ||
                RevisionLedger.IsNewer(packet.StateRevision, cached.StateRevision))
            {
                _pendingWorldEvents[packet.ObjectKey] = packet;
                _pendingWorldEventSenders[packet.ObjectKey] = sender;
            }
            _pendingWorldRetryAt = Time.unscaledTime + 0.25f;
        }

        private void PumpPendingWorldEvents()
        {
            if (_pendingWorldEvents.Count == 0) return;
            if (Time.unscaledTime < _pendingWorldRetryAt) return;

            List<ulong>? applied = null;
            List<ulong>? stale = null;
            foreach (var pair in _pendingWorldEvents)
            {
                // A newer revision for the same object may have been applied directly while
                // this event was queued (the object was not loaded when it arrived). Applying
                // the older cached value now would roll the world state back.
                if (_appliedWorldRevisions.IsStale(
                        pair.Key, pair.Value.StateRevision, pair.Value.OriginPeerId))
                {
                    (stale ??= new List<ulong>()).Add(pair.Key);
                    continue;
                }

                // A cached event belongs to a scene that was not loaded when it arrived.
                // Once the scene is loaded the sender may or may not be there; only allow
                // the proximity fallback when presence can actually be proven.
                bool allowProximity = _pendingWorldEventSenders.TryGetValue(pair.Key, out var sender) &&
                                      IsSenderInLocalScene(sender);
                if (!TryApplyWorldEvent(pair.Value, allowProximity)) continue;
                _appliedWorldRevisions.Record(pair.Key, pair.Value.StateRevision, pair.Value.OriginPeerId);
                (applied ??= new List<ulong>()).Add(pair.Key);
            }

            if (stale != null)
            {
                for (int i = 0; i < stale.Count; i++)
                {
                    _pendingWorldEvents.Remove(stale[i]);
                    _pendingWorldEventSenders.Remove(stale[i]);
                }
            }

            if (applied != null || stale != null)
            {
                if (applied != null)
                {
                    for (int i = 0; i < applied.Count; i++)
                    {
                        _pendingWorldEvents.Remove(applied[i]);
                        _pendingWorldEventSenders.Remove(applied[i]);
                    }
                }

                _pendingWorldRetryAt = Time.unscaledTime + 0.5f;
            }
            else
            {
                // Keep retrying at a slower cadence. The target may simply not be active
                // yet (lazy room load), so giving up after a few attempts loses state.
                _pendingWorldRetryAt = Time.unscaledTime + 5f;
            }

            while (_pendingWorldEventOrder.Count > 0 &&
                   !_pendingWorldEvents.ContainsKey(_pendingWorldEventOrder.Peek()))
            {
                _pendingWorldEventOrder.Dequeue();
            }
        }

        /// <summary>
        /// Called after a local persistent world item (door, lever, chest, cache) saves its
        /// value. Registers the state for catch-up and broadcasts it when connected.
        /// </summary>
        public void OnLocalPersistentState(string sceneName, string itemId, PersistentValueType valueType, int value)
        {
            ulong key = WorldObjectKey.ComputePersistent(sceneName, itemId, (int)valueType);
            if (key == 0) return;

            if (_persistentLastValue.TryGetValue(key, out var last) &&
                last.ValueType == valueType && last.Value == value)
            {
                return;
            }

            var packet = new PersistentStatePacket
            {
                Header = new PacketHeader(PacketType.PersistentState, WorldChannel, ++_packetSequence, NetworkClock.NowMs),
                ItemKey = key,
                ValueType = valueType,
                Value = value,
                StateRevision = _packetSequence == 0 ? ++_packetSequence : _packetSequence,
                OriginPeerId = _revisionOrigin
            };

            RecordPersistentStateForReplay(packet);

            if (_plugin?.SessionState?.IsConnected != true) return;

            SendPersistentPacket(packet);
        }

        /// <summary>
        /// Stores a persistent object value for late-join replay. Called for local changes
        /// and for remote changes the host applied, so a third player sees them too.
        /// </summary>
        private void RecordPersistentStateForReplay(PersistentStatePacket packet)
        {
            if (packet.ItemKey == 0) return;

            if (!_persistentStates.ContainsKey(packet.ItemKey))
            {
                while (_persistentStates.Count >= 2048 && _persistentStateOrder.Count > 0)
                {
                    ulong oldest = _persistentStateOrder.Dequeue();
                    if (_persistentStates.Remove(oldest))
                    {
                        _persistentLastValue.Remove(oldest);
                        break;
                    }
                }
                _persistentStateOrder.Enqueue(packet.ItemKey);
            }

            _persistentStates[packet.ItemKey] = packet;
            _persistentLastValue[packet.ItemKey] = packet;
        }

        /// <summary>
        /// Applies a persistent world value from the peer.
        /// </summary>
        public void ApplyRemotePersistentState(PersistentStatePacket packet)
        {
            if (RemoteApplyScope.IsActive) return;
            if (packet.ItemKey == 0 || IsStalePersistentState(packet)) return;

            EnsurePersistentIndex();

            bool applied;
            try
            {
                applied = TryApplyPersistentState(packet);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[EnemySync] Failed applying persistent state: {ex.Message}");
                CachePendingPersistentState(packet);
                return;
            }

            if (!applied)
            {
                // The object lives in a scene that is not loaded right now. Keep the packet
                // and retry after the next scene change instead of silently dropping it.
                CachePendingPersistentState(packet);
            }
            else
            {
                _appliedPersistentRevisions.Record(packet.ItemKey, packet.StateRevision, packet.OriginPeerId);
            }
        }

        private bool IsStalePersistentState(PersistentStatePacket packet)
        {
            if (packet.OriginPeerId == _revisionOrigin) return true;
            if (_appliedPersistentRevisions.IsStale(packet.ItemKey, packet.StateRevision, packet.OriginPeerId))
                return true;

            return false;
        }

        /// <summary>
        /// Applies one persistent value using the current scene's lookup index.
        /// Returns false when the object is not present in the loaded scene.
        /// </summary>
        private bool TryApplyPersistentState(PersistentStatePacket packet)
        {
            try
            {
                if (packet.ValueType == PersistentValueType.Bool)
                {
                    if (_boolItems == null || !_boolItems.TryGetValue(packet.ItemKey, out var item) || item == null)
                        return false;

                    bool value = packet.Value != 0;
                    if (item.GetCurrentValue() == value)
                    {
                        RememberPersistentValue(packet.ItemKey, packet.ValueType, packet.Value);
                        return true;
                    }

                    using (RemoteApplyScope.Begin())
                    {
                        item.SetValueOverride(value);
                        item.SaveState();
                    }
                    // The SaveState postfix is suppressed under RemoteApplyScope, so update
                    // the local heartbeat cache explicitly; otherwise the 1 Hz poll would
                    // re-broadcast the peer's own value back to it.
                    RememberPersistentValue(packet.ItemKey, packet.ValueType, packet.Value);
                    return true;
                }

                if (packet.ValueType == PersistentValueType.Int)
                {
                    if (_intItems == null || !_intItems.TryGetValue(packet.ItemKey, out var item) || item == null)
                        return false;

                    if (item.GetCurrentValue() == packet.Value)
                    {
                        RememberPersistentValue(packet.ItemKey, packet.ValueType, packet.Value);
                        return true;
                    }

                    using (RemoteApplyScope.Begin())
                    {
                        item.SetValueOverride(packet.Value);
                        item.SaveState();
                    }
                    RememberPersistentValue(packet.ItemKey, packet.ValueType, packet.Value);
                    return true;
                }

                // Unknown value type: acknowledged and discarded (the validator rejects new ones).
                return true;
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[EnemySync] Failed applying persistent state: {ex.Message}");
                return false;
            }
        }

        private void CachePendingPersistentState(PersistentStatePacket packet)
        {
            if (!_pendingPersistentStates.ContainsKey(packet.ItemKey))
            {
                while (_pendingPersistentStates.Count >= MaxPendingPersistentStates &&
                       _pendingPersistentOrder.Count > 0)
                {
                    ulong oldest = _pendingPersistentOrder.Dequeue();
                    if (_pendingPersistentStates.Remove(oldest)) break;
                }

                _pendingPersistentOrder.Enqueue(packet.ItemKey);
            }

            // Same rule as world events: revisions are only comparable within one origin,
            // so a second peer's update replaces a cached entry instead of being discarded.
            if (!_pendingPersistentStates.TryGetValue(packet.ItemKey, out var cached) ||
                cached.StateRevision == 0 || packet.StateRevision == 0 ||
                cached.OriginPeerId != packet.OriginPeerId ||
                RevisionLedger.IsNewer(packet.StateRevision, cached.StateRevision))
            {
                _pendingPersistentStates[packet.ItemKey] = packet;
            }
            _pendingPersistentRetryAt = Time.unscaledTime + 0.25f;
        }

        /// <summary>
        /// Rebuilds the key → object indexes for the active scene. Includes inactive objects
        /// so a door/lever/chest that activates later is still found by the pending-state
        /// pump instead of being retried forever.
        /// </summary>
        private void RebuildPersistentIndex()
        {
            var bools = new Dictionary<ulong, PersistentItem<bool>>();
            var ints = new Dictionary<ulong, PersistentItem<int>>();

            var boolItems = UnityEngine.Object.FindObjectsByType<PersistentBoolItem>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            for (int i = 0; i < boolItems.Length; i++)
            {
                var item = boolItems[i];
                if (item == null) continue;
                ulong key = WorldObjectKey.ComputePersistent(item.GetSceneName(), item.GetId(), (int)PersistentValueType.Bool);
                if (key != 0) bools[key] = item;
            }

            var intItems = UnityEngine.Object.FindObjectsByType<PersistentIntItem>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            for (int i = 0; i < intItems.Length; i++)
            {
                var item = intItems[i];
                if (item == null) continue;
                ulong key = WorldObjectKey.ComputePersistent(item.GetSceneName(), item.GetId(), (int)PersistentValueType.Int);
                if (key != 0) ints[key] = item;
            }

            _boolItems = bools;
            _intItems = ints;
            _persistentIndexScene = SceneManager.GetActiveScene().name;
            _persistentIndexDirty = false;
        }

        private void EnsurePersistentIndex()
        {
            if (_persistentIndexDirty ||
                _boolItems == null || _intItems == null ||
                !string.Equals(_persistentIndexScene, SceneManager.GetActiveScene().name, StringComparison.Ordinal))
            {
                RebuildPersistentIndex();
            }
        }

        private void PumpPendingPersistentStates()
        {
            if (_pendingPersistentStates.Count == 0) return;
            if (Time.unscaledTime < _pendingPersistentRetryAt) return;

            EnsurePersistentIndex();

            List<ulong>? applied = null;
            List<ulong>? stale = null;
            foreach (var pair in _pendingPersistentStates)
            {
                // A newer revision may have been applied directly after this packet was
                // queued (for example when the object streamed in while the pump was
                // waiting). Re-checking here prevents the old cached value from rolling the
                // door/chest/lift back and persisting that rollback through SaveState.
                if (IsStalePersistentState(pair.Value))
                {
                    (stale ??= new List<ulong>()).Add(pair.Key);
                    continue;
                }

                if (!TryApplyPersistentState(pair.Value)) continue;
                _appliedPersistentRevisions.Record(pair.Key, pair.Value.StateRevision, pair.Value.OriginPeerId);
                (applied ??= new List<ulong>()).Add(pair.Key);
            }

            if (stale != null)
            {
                for (int i = 0; i < stale.Count; i++)
                    _pendingPersistentStates.Remove(stale[i]);
            }

            if (applied != null || stale != null)
            {
                if (applied != null)
                {
                    for (int i = 0; i < applied.Count; i++)
                        _pendingPersistentStates.Remove(applied[i]);
                }

                _pendingPersistentRetryAt = Time.unscaledTime + 0.5f;
            }

            if (applied == null && _pendingPersistentStates.Count > 0)
            {
                // Some entries still could not be applied. Ask for a fresh index on the
                // next attempt: an object instantiated after the first scan is not in the
                // cached dictionary yet. When nothing changed at all, back off to 5 s so a
                // permanently missing object does not force a scene scan twice a second.
                _persistentIndexDirty = true;
                if (stale == null)
                    _pendingPersistentRetryAt = Time.unscaledTime + 5f;
            }

            while (_pendingPersistentOrder.Count > 0 &&
                   !_pendingPersistentStates.ContainsKey(_pendingPersistentOrder.Peek()))
            {
                _pendingPersistentOrder.Dequeue();
            }
        }

        /// <summary>
        /// Compares the current scene's persistent bool/int items against the last value
        /// this controller remembers and broadcasts any change. This covers doors, chests
        /// and lifts whose script changes the value without routing through the
        /// <c>SaveState</c> postfix (the common path is already event-driven).
        /// </summary>
        private void PollPersistentStateChanges()
        {
            if (Time.unscaledTime < _persistentPollAt) return;
            _persistentPollAt = Time.unscaledTime + PersistentPollInterval;

            EnsurePersistentIndex();

            if (_boolItems != null)
            {
                foreach (var pair in _boolItems)
                {
                    var item = pair.Value;
                    if (item == null || !item.HasLoadedValue) continue;

                    int value = item.GetCurrentValue() ? 1 : 0;
                    if (_persistentLastValue.TryGetValue(pair.Key, out var last) &&
                        last.ValueType == PersistentValueType.Bool)
                    {
                        if (last.Value == value) continue;
                        OnLocalPersistentState(item.GetSceneName(), item.GetId(), PersistentValueType.Bool, value);
                    }
                    else
                    {
                        // Seed the cache with the scene's initial state. Broadcasting every
                        // unchanged item on load would flood the reliable channel.
                        RememberPersistentValue(pair.Key, PersistentValueType.Bool, value);
                    }
                }
            }

            if (_intItems != null)
            {
                foreach (var pair in _intItems)
                {
                    var item = pair.Value;
                    if (item == null || !item.HasLoadedValue) continue;

                    int value = item.GetCurrentValue();
                    if (_persistentLastValue.TryGetValue(pair.Key, out var last) &&
                        last.ValueType == PersistentValueType.Int)
                    {
                        if (last.Value == value) continue;
                        OnLocalPersistentState(item.GetSceneName(), item.GetId(), PersistentValueType.Int, value);
                    }
                    else
                    {
                        RememberPersistentValue(pair.Key, PersistentValueType.Int, value);
                    }
                }
            }
        }

        /// <summary>
        /// Seeds the local "last value" cache so the poll does not re-broadcast a value
        /// that already matches what the peer is expected to have.
        /// </summary>
        private void RememberPersistentValue(ulong key, PersistentValueType valueType, int value)
        {
            _persistentLastValue[key] = new PersistentStatePacket
            {
                ItemKey = key,
                ValueType = valueType,
                Value = value
            };
        }

        /// <summary>True when the transport accepted the packet for delivery.</summary>
        private bool SendPersistentPacket(PersistentStatePacket packet)
        {
            var transport = _plugin?.ActiveTransport;
            if (transport == null || !transport.IsHost || packet.OriginPeerId != _revisionOrigin) return false;
            packet.OriginPeerId = transport.LocalPeerId.Value;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(64);
            try
            {
                var writer = new PacketWriter(buffer);
                packet.Serialize(ref writer);
                return SendBuffer(buffer, writer.Position);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[EnemySync] Failed sending persistent state: {ex.Message}");
                return false;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        private T? FindObjectByKey<T>(ulong key, float x, float y, bool allowProximity) where T : Component
        {
            var objects = UnityEngine.Object.FindObjectsByType<T>(FindObjectsSortMode.None);
            T? nearest = null;
            const float radiusSq = 2.5f * 2.5f;
            float nearestSq = radiusSq;
            int nearby = 0;

            for (int i = 0; i < objects.Length; i++)
            {
                var component = objects[i];
                if (component == null) continue;
                if (WorldObjectKey.Compute(component.transform) == key)
                    return component;

                if (!allowProximity) continue;
                if (float.IsNaN(x) || float.IsNaN(y)) continue;

                float dx = component.transform.position.x - x;
                float dy = component.transform.position.y - y;
                float distanceSq = dx * dx + dy * dy;
                if (distanceSq > radiusSq) continue;

                nearby++;
                if (distanceSq < nearestSq)
                {
                    nearestSq = distanceSq;
                    nearest = component;
                }
            }

            // A sibling-index mismatch can change the hash while the object is still at the
            // authoritative position; use the same proximity fallback as enemies, but never
            // guess between two objects in the same radius.
            if (nearby > 1)
            {
                LogAmbiguousMatch($"{typeof(T).Name}: {nearby} candidates within 2.5 units");
                return null;
            }

            return nearby == 1 ? nearest : null;
        }

        /// <summary>
        /// Rebuilds the Breakable/Gate exact-key indexes when the active scene changed. The
        /// scans use the default active-only semantics, matching the previous per-event
        /// FindObjectByKey scan.
        /// </summary>
        private void EnsureWorldIndexes()
        {
            var activeScene = SceneManager.GetActiveScene();
            if (!_worldIndexDirty &&
                _worldIndexSceneHandle == activeScene.handle &&
                string.Equals(_worldIndexScene, activeScene.name, StringComparison.Ordinal))
            {
                return;
            }

            _breakableIndex.Clear();
            _gateIndex.Clear();

            var breakables = UnityEngine.Object.FindObjectsByType<Breakable>(FindObjectsSortMode.None);
            for (int i = 0; i < breakables.Length; i++)
            {
                var breakable = breakables[i];
                if (breakable == null) continue;
                ulong key = WorldObjectKey.Compute(breakable.transform);
                if (key != 0) _breakableIndex[key] = breakable;
            }

            var gates = UnityEngine.Object.FindObjectsByType<Gate>(FindObjectsSortMode.None);
            for (int i = 0; i < gates.Length; i++)
            {
                var gate = gates[i];
                if (gate == null) continue;
                ulong key = WorldObjectKey.Compute(gate.transform);
                if (key != 0) _gateIndex[key] = gate;
            }

            _worldIndexScene = activeScene.name;
            _worldIndexSceneHandle = activeScene.handle;
            _worldIndexDirty = false;
        }

        /// <summary>
        /// Exact-key index lookup. The active check mirrors FindObjectsByType's default
        /// active-only filter and the recompute catches objects recycled since the build.
        /// </summary>
        private static T? FindIndexedWorldObject<T>(Dictionary<ulong, T> index, ulong key)
            where T : Component
        {
            if (key == 0 || !index.TryGetValue(key, out var component)) return null;

            if (component == null || !component.gameObject.activeInHierarchy)
            {
                index.Remove(key);
                return null;
            }

            ulong currentKey = WorldObjectKey.Compute(component.transform);
            if (currentKey == key) return component;

            index.Remove(key);
            if (currentKey != 0) index[currentKey] = component;
            return null;
        }

        #endregion

        #region Pickups

        /// <summary>
        /// Returns the active-scene currency drops, reusing a recent enumeration so a burst
        /// of remote claims does not scan every CurrencyObjectBase each time. The cached
        /// array is refreshed after <see cref="PickupCacheTtl"/> or when the scene changed.
        /// </summary>
        private CurrencyObjectBase[] GetCachedPickups(out bool scannedNow)
        {
            var activeScene = SceneManager.GetActiveScene();
            float now = Time.unscaledTime;
            if (!_pickupCacheDirty &&
                _pickupCache != null &&
                now < _pickupCacheExpiresAt &&
                _pickupCacheSceneHandle == activeScene.handle &&
                string.Equals(_pickupCacheScene, activeScene.name, StringComparison.Ordinal))
            {
                scannedNow = false;
                return _pickupCache;
            }

            scannedNow = true;
            return ScanPickups();
        }

        private CurrencyObjectBase[] ScanPickups()
        {
            var activeScene = SceneManager.GetActiveScene();
            _pickupCache = UnityEngine.Object.FindObjectsByType<CurrencyObjectBase>(FindObjectsSortMode.None);
            _pickupCacheScene = activeScene.name;
            _pickupCacheSceneHandle = activeScene.handle;
            _pickupCacheExpiresAt = Time.unscaledTime + PickupCacheTtl;
            _pickupCacheDirty = false;
            return _pickupCache;
        }

        /// <summary>Disables the local copy only when its stable key matches the claim.</summary>
        public void OnRemotePickupClaimed(Core.Protocol.Packets.CurrencyType currency, ulong key)
        {
            if (key == 0) return;

            var pickups = GetCachedPickups(out bool scannedNow);
            var target = FindPickup(pickups, currency, key);
            if (target == null && !scannedNow)
                target = FindPickup(ScanPickups(), currency, key);

            if (target != null)
            {
                target.Disable(0f);
                _plugin?.VitalsSync?.MarkPickupObserved(currency, key);
            }
        }

        private static CurrencyObjectBase? FindPickup(
            CurrencyObjectBase[] pickups, Core.Protocol.Packets.CurrencyType currency, ulong key)
        {
            bool wantShards = currency == Core.Protocol.Packets.CurrencyType.ShellShards;
            foreach (var pickup in pickups)
            {
                if (pickup == null || !pickup.gameObject.activeInHierarchy) continue;
                if ((pickup is ShellShard) != wantShards) continue;
                if (WorldObjectKey.ComputePickup(pickup) == key) return pickup;
            }

            return null;
        }

        #endregion

        #region Helpers

        private bool SendBuffer(byte[] buffer, int length, bool requireReliableCapacity = false)
        {
            var transport = _plugin?.ActiveTransport;
            if (transport == null || _plugin?.SessionState?.IsConnected != true) return false;

            // A full reliable window silently refuses new packets. Avoid entering that state
            // for snapshots so the batch is retried instead of being marked as sent.
            if (requireReliableCapacity && transport.ReliableBacklogCount >= NetworkLimits.SnapshotBacklogThreshold) return false;

            // Since protocol v11 SendToAll reports acceptance: false means at least one peer
            // refused (or nobody is connected). Propagate the real result so the caller keeps
            // the batch and retries, instead of suppressing those enemies until the periodic
            // reconcile. Retrying after a partial multi-peer refusal only duplicates entries
            // to the peers that already accepted, which is idempotent for snapshots.
            return transport.SendToAll(buffer, 0, length, DeliveryMethod.ReliableOrdered, WorldChannel);
        }

        /// <summary>
        /// Drops every scene-scoped world lookup cache. Called on scene changes and when a
        /// new session starts; both invalidate transform keys and object references.
        /// </summary>
        private void ClearWorldObjectCaches()
        {
            WorldObjectKey.ClearCache();

            _breakableIndex.Clear();
            _gateIndex.Clear();
            _worldIndexScene = string.Empty;
            _worldIndexSceneHandle = -1;
            _worldIndexDirty = true;

            _pickupCache = null;
            _pickupCacheScene = string.Empty;
            _pickupCacheSceneHandle = -1;
            _pickupCacheExpiresAt = 0f;
            _pickupCacheDirty = true;
        }

        private void DetectSceneChange()
        {
            var activeScene = SceneManager.GetActiveScene();
            string scene = activeScene.name;
            int sceneHandle = activeScene.handle;
            if (string.Equals(scene, _sceneName, StringComparison.Ordinal) &&
                sceneHandle == _sceneHandle)
            {
                return;
            }

            _sceneName = scene;
            _sceneHandle = sceneHandle;
            _keyCache = new ConditionalWeakTable<HealthManager, KeyHolder>();
            _lastSnapshot.Clear();
            _enemyIndex.Clear();
            _enemyIndexDirty = true;
            ClearWorldObjectCaches();
            _service.Reset();
            if (_plugin?.SessionState?.IsConnected == true)
                _plugin.VitalsSync?.OnPickupSceneLoaded();

            // WorldEvent records are permanent (breakables/gates survive scene loads), so
            // they intentionally survive a scene change and remain available for late-join
            // catch-up. The same is true for bosses: they do not respawn when their room is
            // reloaded, so only ordinary-enemy deaths are dropped here.
            int dropped = _deadRegistry.DropRespawnable();
            if (dropped > 0)
            {
                SilksongCoopPlugin.LogInfo(
                    $"[EnemySync] Scene changed; dropped {dropped} respawnable death record(s), kept {_deadRegistry.Count} boss record(s)");
            }

            // The scene lookup indexes are stale; the pending persistent-state cache is
            // retried once the new scene has finished loading.
            _boolItems = null;
            _intItems = null;
            _persistentIndexScene = string.Empty;
            if (_pendingPersistentStates.Count > 0)
            {
                _pendingPersistentRetryAt = Time.unscaledTime + 0.25f;
            }
            if (_pendingWorldEvents.Count > 0)
            {
                _pendingWorldRetryAt = Time.unscaledTime + 0.25f;
            }
            if (_pendingDeadEnemies.Count > 0)
            {
                _pendingDeadRetryAt = Time.unscaledTime + 0.25f;
            }
        }

        /// <summary>
        /// Caches a dead snapshot entry until its scene is loaded. The snapshot is sent once
        /// per session, so an entry dropped while the boss room is unloaded would otherwise
        /// leave the boss alive on the late joiner's screen forever.
        /// </summary>
        private void CachePendingDeadEnemy(
            EnemySnapshotEntry entry,
            uint timestamp,
            short damage = 0,
            bool fromRemoteClaim = false)
        {
            if (entry.EnemyKey == 0) return;

            if (!_pendingDeadEnemies.ContainsKey(entry.EnemyKey))
            {
                while (_pendingDeadEnemies.Count >= MaxPendingDeadEnemies &&
                       _pendingDeadOrder.Count > 0)
                {
                    ulong oldest = _pendingDeadOrder.Dequeue();
                    if (_pendingDeadEnemies.Remove(oldest)) break;
                }

                _pendingDeadOrder.Enqueue(entry.EnemyKey);
            }

            if (!_pendingDeadEnemies.TryGetValue(entry.EnemyKey, out var existing) ||
                unchecked((int)(timestamp - existing.Timestamp)) > 0)
            {
                _pendingDeadEnemies[entry.EnemyKey] = new PendingDeadEnemy
                {
                    Entry = entry,
                    Timestamp = timestamp,
                    Damage = damage,
                    FromRemoteClaim = fromRemoteClaim
                };
            }

            _pendingDeadRetryAt = Time.unscaledTime + 0.25f;
        }

        private void PumpPendingDeadEnemies()
        {
            if (_pendingDeadEnemies.Count == 0) return;
            if (Time.unscaledTime < _pendingDeadRetryAt) return;

            List<ulong>? resolved = null;
            foreach (var pair in _pendingDeadEnemies)
            {
                var pending = pair.Value;
                // Pending deaths belong to a specific enemy in a scene that was not loaded
                // when the packet arrived. Only an exact key match may apply them; the
                // proximity fallback could otherwise kill an unrelated enemy in the scene
                // the player has since entered.
                var target = FindEnemy(
                    pending.Entry.EnemyKey,
                    pending.Entry.PositionX,
                    pending.Entry.PositionY,
                    allowProximity: false);
                if (target == null) continue;

                // Defense in depth for claims cached by older code paths: the host will
                // not apply a deferred client death that carries no damage proof, nor one
                // whose damage is insufficient to kill the enemy as it is loaded now.
                if (_plugin?.SessionState?.IsHost == true &&
                    pending.FromRemoteClaim)
                {
                    bool reject = pending.Damage <= 0;
                    if (!reject && target != null)
                    {
                        int currentHp = target.hp;
                        if (currentHp > 0 && pending.Damage < currentHp)
                            reject = true;
                    }
                    if (reject)
                    {
                        SilksongCoopPlugin.LogWarn(
                            $"[EnemySync] Dropped deferred death claim for {pending.Entry.EnemyKey}: insufficient damage");
                        (resolved ??= new List<ulong>()).Add(pair.Key);
                        continue;
                    }
                }

                if (_service.TryAcceptSnapshot(
                        pending.Entry.EnemyKey,
                        pending.Entry.Hp,
                        pending.Entry.IsDead,
                        pending.Timestamp))
                {
                    ApplyEnemyState(pending.Entry.EnemyKey, target!, pending.Entry.Hp, pending.Entry.IsDead, authoritative: true);
                }

                (resolved ??= new List<ulong>()).Add(pair.Key);
            }

            if (resolved != null)
            {
                for (int i = 0; i < resolved.Count; i++)
                    _pendingDeadEnemies.Remove(resolved[i]);
            }

            while (_pendingDeadOrder.Count > 0 &&
                   !_pendingDeadEnemies.ContainsKey(_pendingDeadOrder.Peek()))
            {
                _pendingDeadOrder.Dequeue();
            }

            _pendingDeadRetryAt = Time.unscaledTime +
                (_pendingDeadEnemies.Count == 0 ? 5f : 0.25f);
        }

        /// <summary>
        /// Detects the end of a repeatable boss sequence and drops the death records and
        /// per-enemy state that belonged to that attempt. Without this, the next attempt
        /// reused the same keys, sent both an alive snapshot and the old death, and the
        /// client killed the freshly spawned boss immediately.
        /// </summary>
        private void TrackBossSequenceTransition()
        {
            bool inSequence = IsInBossSequence();
            if (inSequence == _wasInBossSequence) return;
            _wasInBossSequence = inSequence;

            if (inSequence) return;

            var droppedKeys = _deadRegistry.DropSequenceBosses();
            if (droppedKeys.Count == 0) return;

            for (int i = 0; i < droppedKeys.Count; i++)
            {
                ulong key = droppedKeys[i];
                _service.Forget(key);
                _lastSnapshot.Remove(key);
                _pendingDeadEnemies.Remove(key);
            }

            while (_pendingDeadOrder.Count > 0 &&
                   !_pendingDeadEnemies.ContainsKey(_pendingDeadOrder.Peek()))
            {
                _pendingDeadOrder.Dequeue();
            }

            // The next periodic snapshot must resend the bosses that remain dead for the
            // save; the cleared sequence bosses are now alive again.
            _forceSnapshotReconcile = true;

            SilksongCoopPlugin.LogInfo(
                $"[EnemySync] Boss sequence ended; cleared {droppedKeys.Count} repeatable boss death record(s)");
        }

        /// <summary>
        /// Whether this enemy is a boss that does not respawn with its scene. BossSceneController
        /// covers scripted boss rooms; a boss sequence covers Godhome-style fights where every
        /// active enemy belongs to the run.
        /// </summary>
        private static bool IsBoss(HealthManager healthManager)
            => IsBossSceneBoss(healthManager) || IsInBossSequence();

        /// <summary>
        /// A boss owned by the scripted boss room. These persist for the whole save.
        /// </summary>
        private static bool IsBossSceneBoss(HealthManager healthManager)
        {
            if (healthManager == null) return false;

            try
            {
                var controller = BossSceneController.Instance;
                if (controller != null)
                {
                    var bosses = controller.bosses;
                    if (bosses != null)
                    {
                        for (int i = 0; i < bosses.Length; i++)
                        {
                            if (bosses[i] == healthManager)
                                return true;
                        }
                    }

                    var lookup = controller.BossHealthLookup;
                    if (lookup != null && lookup.ContainsKey(healthManager))
                        return true;
                }
            }
            catch (Exception ex)
            {
                if (!_bossProbeWarningLogged)
                {
                    _bossProbeWarningLogged = true;
                    SilksongCoopPlugin.LogWarn(
                        $"[EnemySync] Boss detection failed ({ex.Message}); the death will be treated as a regular enemy");
                }
            }

            return false;
        }

        /// <summary>
        /// Whether a repeatable boss sequence is running. Such bosses reuse their hierarchy
        /// keys between attempts and must not keep a death record after the sequence ends.
        /// </summary>
        private static bool IsInBossSequence()
        {
            try
            {
                return BossSequenceController.IsInSequence;
            }
            catch
            {
                return false;
            }
        }

        private static bool MatchesHierarchy(KeyHolder holder, Transform transform)
        {
            // References, sibling indices and the scene handle are allocation-free.
            // Ancestor/name changes are caught by the 30s key-cache refresh.
            if (holder.Chain.Length == 0 ||
                holder.SceneHandle != transform.gameObject.scene.handle)
            {
                return false;
            }

            int index = 0;
            for (var current = transform; current != null; current = current.parent)
            {
                if (index >= holder.Chain.Length ||
                    holder.Chain[index] != current ||
                    holder.SiblingIndices[index] != current.GetSiblingIndex())
                {
                    return false;
                }
                index++;
            }

            return index == holder.Chain.Length;
        }

        private void RefreshKeyCacheIfNeeded()
        {
            if (Time.unscaledTime < _nextKeyCacheRefreshAt) return;

            _nextKeyCacheRefreshAt = Time.unscaledTime + 30f;
            _keyCache = new ConditionalWeakTable<HealthManager, KeyHolder>();
            // The hierarchy fingerprint intentionally skips names, so drop the shared key
            // cache here to keep the periodic full recompute able to catch ancestor renames.
            WorldObjectKey.ClearCache();
            _enemyIndexDirty = true;
        }

        private ulong GetKey(HealthManager healthManager)
        {
            if (healthManager == null) return 0;

            var transform = healthManager.transform;
            if (_keyCache.TryGetValue(healthManager, out var holder))
            {
                if (MatchesHierarchy(holder, transform))
                {
                    return holder.Key;
                }

                // The object or one of its ancestors was pooled/reparented/renamed.
                _keyCache.Remove(healthManager);
            }

            ulong key = WorldObjectKey.Compute(transform);
            if (key == 0) return 0;

            CacheKey(healthManager, transform, key);
            return key;
        }

        private ulong GetFreshKey(HealthManager healthManager)
        {
            if (healthManager == null) return 0;

            var transform = healthManager.transform;
            ulong key = WorldObjectKey.ComputeFresh(transform);
            if (key == 0) return 0;

            _keyCache.Remove(healthManager);
            CacheKey(healthManager, transform, key);
            return key;
        }

        private void CacheKey(HealthManager healthManager, Transform transform, ulong key)
        {
            var chain = new List<Transform>();
            var siblings = new List<int>();
            for (var current = transform; current != null; current = current.parent)
            {
                chain.Add(current);
                siblings.Add(current.GetSiblingIndex());
            }

            _keyCache.Add(healthManager, new KeyHolder
            {
                Key = key,
                Chain = chain.ToArray(),
                SiblingIndices = siblings.ToArray(),
                SceneHandle = transform.gameObject.scene.handle
            });
        }

        /// <summary>
        /// True when the remote peer is known to be in the local scene. The clone spawner
        /// tracks this from transform packets and explicit scene-transition packets; an
        /// unknown/absent state is treated as "not in this scene" so the safe exact-key
        /// path is used.
        /// </summary>
        private bool IsSenderInLocalScene(NetworkPeerId sender)
        {
            try
            {
                return _plugin?.CloneSpawner?.IsRemoteInSameScene(sender) == true;
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[EnemySync] Scene presence check failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Logs a dropped position-based match at most once every five seconds. A busy fight
        /// can hit this path many times per second, and an unthrottled warning would both
        /// flood BepInEx's log and hide the first, most useful occurrence.
        /// </summary>
        private void LogAmbiguousMatch(string detail)
        {
            if (Time.unscaledTime < _nextAmbiguousMatchLogAt)
            {
                _suppressedAmbiguousMatchLogs++;
                return;
            }

            int suppressed = _suppressedAmbiguousMatchLogs;
            _suppressedAmbiguousMatchLogs = 0;
            _nextAmbiguousMatchLogAt = Time.unscaledTime + 5f;

            string suffix = suppressed > 0 ? $" ({suppressed} similar match(es) suppressed)" : string.Empty;
            SilksongCoopPlugin.LogWarn(
                $"[EnemySync] Proximity match skipped: {detail}; falling back to the exact key only{suffix}");
        }

        private HealthManager? FindEnemy(ulong key, float x, float y, bool allowProximity = true)
        {
            EnsureEnemyIndex();
            if (_enemyIndex.TryGetValue(key, out var indexed))
            {
                if (indexed == null || !indexed.gameObject.activeInHierarchy)
                {
                    _enemyIndex.Remove(key);
                }
                else
                {
                    // An active object is not enough: it may have been reparented/reused.
                    // Only return it if its current key still matches the requested key.
                    ulong currentKey = GetKey(indexed);
                    if (currentKey == key)
                    {
                        return indexed;
                    }

                    _enemyIndex.Remove(key);
                    if (currentKey != 0) _enemyIndex[currentKey] = indexed;
                }
            }

            HealthManager? nearest = null;
            const float radiusSq = 2.5f * 2.5f;
            float nearestSq = radiusSq;
            int nearby = 0;

            try
            {
                foreach (var hm in HealthManager.EnumerateActiveEnemies())
                {
                    if (hm == null) continue;

                    if (GetKey(hm) == key)
                    {
                        _enemyIndex[key] = hm;
                        return hm;
                    }

                    if (!allowProximity) continue;
                    if (hm.GetIsDead()) continue;
                    if (float.IsNaN(x) || float.IsNaN(y)) continue;

                    float dx = hm.transform.position.x - x;
                    float dy = hm.transform.position.y - y;
                    float distanceSq = dx * dx + dy * dy;
                    if (distanceSq > radiusSq) continue;

                    nearby++;
                    if (distanceSq < nearestSq)
                    {
                        nearestSq = distanceSq;
                        nearest = hm;
                    }
                }
            }
            catch (Exception ex)
            {
                if (Time.unscaledTime >= _nextEnemyLookupFailureLogAt)
                {
                    _nextEnemyLookupFailureLogAt = Time.unscaledTime + 5f;
                    SilksongCoopPlugin.LogWarn($"[EnemySync] Enemy lookup failed: {ex.Message}");
                }
            }

            // Never guess between two enemies in the same radius: applying a hit to the
            // wrong one is worse than dropping a fallback match, and the host snapshot
            // still corrects real drift later.
            if (nearby > 1)
            {
                LogAmbiguousMatch($"Enemy: {nearby} live candidates within 2.5 units");
                return null;
            }

            return nearby == 1 ? nearest : null;
        }

        private static int GetInitHp(HealthManager healthManager)
        {
            if (!_initHpResolved)
            {
                _initHpResolved = true;
                try
                {
                    _initHpRef = AccessTools.FieldRefAccess<HealthManager, int>("initHp");
                }
                catch
                {
                    _initHpRef = null;
                }
            }

            if (_initHpRef != null)
            {
                try
                {
                    int value = _initHpRef(healthManager);
                    if (value > 0) return value;
                }
                catch
                {
                    // Fall back to the current HP below.
                }
            }

            return Math.Max(1, healthManager.hp);
        }

        #endregion
    }
}
