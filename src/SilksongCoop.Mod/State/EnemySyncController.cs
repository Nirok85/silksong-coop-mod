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
        }

        private const byte WorldChannel = NetworkChannels.World;

        private struct PendingDeadEnemy
        {
            public EnemySnapshotEntry Entry;
            public uint Timestamp;
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
        private const int ReplayBacklogThreshold = 192;

        private SilksongCoopPlugin? _plugin;
        private readonly EnemySyncService _service = new EnemySyncService();
        private ConditionalWeakTable<HealthManager, KeyHolder> _keyCache = new ConditionalWeakTable<HealthManager, KeyHolder>();
        private readonly Dictionary<ulong, int> _lastSnapshotHp = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, bool> _lastSnapshotDead = new Dictionary<ulong, bool>();
        private readonly DeadEnemyRegistry _deadRegistry = new DeadEnemyRegistry(MaxDeadRegistry);
        // Dead snapshot entries whose scene is not loaded yet. Applying them immediately
        // dropped bosses killed before a late joiner entered the room; retry after a load.
        private readonly Dictionary<ulong, PendingDeadEnemy> _pendingDeadEnemies =
            new Dictionary<ulong, PendingDeadEnemy>();
        private readonly Queue<ulong> _pendingDeadOrder = new Queue<ulong>();
        private float _pendingDeadRetryAt;
        private readonly Dictionary<ulong, WorldEventPacket> _worldEvents = new Dictionary<ulong, WorldEventPacket>();
        private readonly Queue<ulong> _worldEventOrder = new Queue<ulong>();
        private readonly Dictionary<ulong, PersistentStatePacket> _persistentStates = new Dictionary<ulong, PersistentStatePacket>();
        private readonly Dictionary<ulong, (uint Revision, ulong Origin)> _appliedWorldRevisions = new Dictionary<ulong, (uint Revision, ulong Origin)>();
        private readonly Dictionary<ulong, (uint Revision, ulong Origin)> _appliedPersistentRevisions = new Dictionary<ulong, (uint Revision, ulong Origin)>();
        private readonly Queue<ulong> _persistentStateOrder = new Queue<ulong>();
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

        private const int MaxPendingPersistentStates = 4096;
        private const int MaxPendingWorldEvents = 1024;

        private readonly Queue<ReplayItem> _replayQueue = new Queue<ReplayItem>();
        private float _nextReplaySendTime;
        private bool _worldReplayPending;

        private string _sceneName = string.Empty;
        private float _snapshotTimer;
        private uint _packetSequence;

        private static AccessTools.FieldRef<HealthManager, int>? _initHpRef;
        private static bool _initHpResolved;
        private static bool _bossProbeWarningLogged;

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
            _service.Reset();
            _keyCache = new ConditionalWeakTable<HealthManager, KeyHolder>();
            _lastSnapshotHp.Clear();
            _lastSnapshotDead.Clear();
            _snapshotTimer = 0f;

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

        private void Update()
        {
            // Track scene changes even while disconnected. Otherwise the first connection
            // looked like a scene change (empty _sceneName) and cleared dead enemies that
            // were recorded while playing solo, just before their catch-up replay.
            DetectSceneChange();

            if (_plugin?.SessionState?.IsConnected != true) return;

            PumpPendingPersistentStates();
            PumpPendingWorldEvents();
            PumpPendingDeadEnemies();

            // Only the host is authoritative for periodic snapshots and world catch-up.
            if (_plugin.SessionState.IsHost != true) return;

            if (_worldReplayPending)
            {
                _worldReplayPending = false;
                QueueWorldReplay();
            }

            PumpWorldReplay();

            _snapshotTimer += Time.unscaledDeltaTime;
            if (_snapshotTimer < 1f) return;
            _snapshotTimer = 0f;
            SendSnapshot();
        }

        #region Enemy hits

        /// <summary>
        /// Called by the HealthManager.Hit postfix after the local player damaged an enemy.
        /// </summary>
        public void OnLocalHit(HealthManager target, HitInstance hit, int hpAfter, bool dead)
        {
            if (target == null) return;
            if (_plugin?.SessionState?.IsConnected != true) return;

            ulong key = GetKey(target);
            if (key == 0) return;

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

            ulong key = GetKey(target);
            if (key == 0) return;

            // Register even before a peer connects so a later client is caught up. Bosses
            // are flagged here, while the scene is still loaded, because the registry uses
            // that flag to survive the scene change that follows a boss kill.
            _deadRegistry.Register(
                key,
                target.transform.position.x,
                target.transform.position.y,
                IsBoss(target) || IsBoss(healthManager));

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
                    CachePendingDeadEnemy(
                        new EnemySnapshotEntry
                        {
                            EnemyKey = packet.EnemyKey,
                            Hp = 0,
                            Flags = EnemySnapshotEntry.FlagDead,
                            PositionX = packet.PositionX,
                            PositionY = packet.PositionY
                        },
                        packet.Header.Timestamp);
                }
                return;
            }

            // Only mark the packet as applied once the target was found. Otherwise the
            // service would reject every retry even though nothing was applied locally.
            if (!_service.TryAcceptHit(packet.EnemyKey, packet.Hp, dead)) return;

            ApplyEnemyState(target, packet.Hp, dead, authoritative: false);
        }

        private void ApplyEnemyState(HealthManager target, int hp, bool dead, bool authoritative)
        {
            if (target == null || target.GetIsDead()) return;

            using (RemoteApplyScope.Begin())
            {
                try
                {
                    if (dead || hp <= 0)
                    {
                        target.hp = 0;
                        target.Die(null, AttackTypes.Nail, ignoreEvasion: true);
                        return;
                    }

                    int next = authoritative ? hp : Math.Min(target.hp, hp);
                    target.hp = Math.Max(1, next);
                }
                catch (Exception ex)
                {
                    SilksongCoopPlugin.LogWarn($"[EnemySync] Failed applying enemy state: {ex.Message}");
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

        private void SendSnapshot()
        {
            List<HealthManager>? enemies;
            try
            {
                enemies = new List<HealthManager>(HealthManager.EnumerateActiveEnemies());
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[EnemySync] Enemy enumeration failed: {ex.Message}");
                return;
            }

            var batch = new List<EnemySnapshotEntry>(EnemySnapshotPacket.MaxEntries);

            for (int i = 0; i < enemies.Count; i++)
            {
                var hm = enemies[i];
                if (hm == null) continue;

                ulong key = GetKey(hm);
                if (key == 0) continue;

                int hp = Math.Max(0, hm.hp);
                bool dead = hm.GetIsDead();
                int initHp = Math.Max(1, GetInitHp(hm));

                bool knownHp = _lastSnapshotHp.TryGetValue(key, out int lastHp);
                bool wasDead = _lastSnapshotDead.TryGetValue(key, out bool lastDead) && lastDead;

                bool newlyDead = dead && !wasDead;
                // Send every enemy once so a client that joins mid-fight gets the host's
                // authoritative HP even if initHp reflection is unavailable.
                bool firstSeen = !knownHp;
                bool damaged = hp < initHp || firstSeen;
                bool changed = knownHp && lastHp != hp;

                if (dead && !newlyDead) continue;
                if (!dead && !damaged && !changed) continue;

                _lastSnapshotHp[key] = hp;
                _lastSnapshotDead[key] = dead;

                batch.Add(new EnemySnapshotEntry
                {
                    EnemyKey = key,
                    Hp = dead ? 0 : hp,
                    Flags = dead ? EnemySnapshotEntry.FlagDead : (byte)0,
                    PositionX = hm.transform.position.x,
                    PositionY = hm.transform.position.y
                });

                if (batch.Count >= EnemySnapshotPacket.MaxEntries)
                {
                    SendSnapshotBatch(batch);
                    batch.Clear();
                }
            }

            // Enemies killed before the current client connected may already be gone from
            // EnumerateActiveEnemies(). Replay them from the local death registry. Boss
            // records survive scene changes, so this also covers a late joiner who enters
            // the boss room after the host has already left it.
            foreach (var dead in _deadRegistry.Records)
            {
                if (_lastSnapshotDead.TryGetValue(dead.Key, out bool alreadySent) && alreadySent)
                    continue;

                _lastSnapshotDead[dead.Key] = true;
                batch.Add(new EnemySnapshotEntry
                {
                    EnemyKey = dead.Key,
                    Hp = 0,
                    Flags = EnemySnapshotEntry.FlagDead,
                    PositionX = dead.Value.X,
                    PositionY = dead.Value.Y
                });

                if (batch.Count >= EnemySnapshotPacket.MaxEntries)
                {
                    SendSnapshotBatch(batch);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                SendSnapshotBatch(batch);
            }
        }

        private void SendSnapshotBatch(List<EnemySnapshotEntry> batch)
        {
            var packet = new EnemySnapshotPacket
            {
                Header = new PacketHeader(PacketType.EnemySnapshot, WorldChannel, ++_packetSequence, NetworkClock.NowMs),
                Entries = batch.ToArray(),
                Count = batch.Count
            };

            byte[] buffer = ArrayPool<byte>.Shared.Rent(1024);
            try
            {
                var writer = new PacketWriter(buffer);
                packet.Serialize(ref writer);
                SendBuffer(buffer, writer.Position);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[EnemySync] Failed sending snapshot: {ex.Message}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
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
            if (_plugin?.SessionState?.IsHost == true) return;
            if (packet.Entries == null) return;

            bool sameScene = IsSenderInLocalScene(sender);
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

                ApplyEnemyState(target, entry.Hp, entry.IsDead, authoritative: true);
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

            ulong key = WorldObjectKey.Compute(transform);
            if (key == 0) return;

            var packet = new WorldEventPacket
            {
                Header = new PacketHeader(PacketType.WorldEvent, WorldChannel, ++_packetSequence, NetworkClock.NowMs),
                ObjectKey = key,
                EventType = eventType,
                PositionX = transform.position.x,
                PositionY = transform.position.y,
                StateRevision = _packetSequence,
                OriginPeerId = (ulong)(_plugin?.ActiveTransport?.LocalPeerId.Value ?? 0)
            };

            // Register even before a peer connects so a later client is caught up.
            if (!_worldEvents.ContainsKey(key))
            {
                while (_worldEvents.Count >= 1024 && _worldEventOrder.Count > 0)
                {
                    ulong oldest = _worldEventOrder.Dequeue();
                    if (_worldEvents.Remove(oldest)) break;
                }
                _worldEventOrder.Enqueue(key);
            }
            _worldEvents[key] = packet;

            if (_plugin?.SessionState?.IsConnected != true) return;

            SendWorldPacket(packet);
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
                var item = _replayQueue.Dequeue();
                if (item.IsPersistent)
                {
                    var stored = item.Persistent;
                    SendPersistentPacket(new PersistentStatePacket
                    {
                        Header = new PacketHeader(PacketType.PersistentState, WorldChannel, ++_packetSequence, NetworkClock.NowMs),
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
                    SendWorldPacket(new WorldEventPacket
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

                if (transport.ReliableBacklogCount >= ReplayBacklogThreshold)
                    break;
            }

            // ~16 packets every 20 ms: fast enough for a late join, slow enough that the
            // receiver's rate limiter and the sender's retransmit window are not flooded.
            _nextReplaySendTime = Time.unscaledTime + 0.02f;
        }

        private void SendWorldPacket(WorldEventPacket packet)
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
                SilksongCoopPlugin.LogWarn($"[EnemySync] Failed sending world event: {ex.Message}");
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
            if (packet.ObjectKey == 0) return;

            // Idempotency: drop stale or duplicate revisions. A revision is only meaningful
            // together with its origin, and revision 0 means "unversioned" (legacy sender).
            if (IsStaleRevision(_appliedWorldRevisions, packet.ObjectKey, packet.StateRevision, packet.OriginPeerId))
                return;

            bool allowProximity = IsSenderInLocalScene(sender);
            if (!TryApplyWorldEvent(packet, allowProximity))
            {
                CachePendingWorldEvent(packet, sender);
            }
            else
            {
                RecordRevision(_appliedWorldRevisions, packet.ObjectKey, packet.StateRevision, packet.OriginPeerId);
            }
        }

        private bool TryApplyWorldEvent(WorldEventPacket packet, bool allowProximity)
        {
            try
            {
                using (RemoteApplyScope.Begin())
                {
                    switch (packet.EventType)
                    {
                        case WorldEventType.BreakableBroken:
                            var breakable = FindObjectByKey<Breakable>(
                                packet.ObjectKey, packet.PositionX, packet.PositionY, allowProximity);
                            if (breakable == null) return false;
                            breakable.BreakSelf();
                            return true;

                        case WorldEventType.GateOpened:
                            var gate = FindObjectByKey<Gate>(
                                packet.ObjectKey, packet.PositionX, packet.PositionY, allowProximity);
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

        /// <summary>
        /// Compares an incoming revision with the last applied one for a world key.
        /// Revisions from a different origin are not comparable, so they are applied.
        /// </summary>
        private static bool IsStaleRevision(
            Dictionary<ulong, (uint Revision, ulong Origin)> applied,
            ulong key,
            uint revision,
            ulong origin)
        {
            if (revision == 0) return false;
            if (!applied.TryGetValue(key, out var last)) return false;
            if (last.Origin != origin) return false;
            return revision <= last.Revision;
        }

        private static void RecordRevision(
            Dictionary<ulong, (uint Revision, ulong Origin)> applied,
            ulong key,
            uint revision,
            ulong origin)
        {
            if (revision != 0)
                applied[key] = (revision, origin);
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
                packet.StateRevision > cached.StateRevision)
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
            foreach (var pair in _pendingWorldEvents)
            {
                // A cached event belongs to a scene that was not loaded when it arrived.
                // Once the scene is loaded the sender may or may not be there; only allow
                // the proximity fallback when presence can actually be proven.
                bool allowProximity = _pendingWorldEventSenders.TryGetValue(pair.Key, out var sender) &&
                                      IsSenderInLocalScene(sender);
                if (!TryApplyWorldEvent(pair.Value, allowProximity)) continue;
                RecordRevision(_appliedWorldRevisions, pair.Key, pair.Value.StateRevision, pair.Value.OriginPeerId);
                (applied ??= new List<ulong>()).Add(pair.Key);
            }

            if (applied != null)
            {
                for (int i = 0; i < applied.Count; i++)
                {
                    _pendingWorldEvents.Remove(applied[i]);
                    _pendingWorldEventSenders.Remove(applied[i]);
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
                StateRevision = _packetSequence,
                OriginPeerId = (ulong)(_plugin?.ActiveTransport?.LocalPeerId.Value ?? 0)
            };

            if (!_persistentStates.ContainsKey(key))
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
                _persistentStateOrder.Enqueue(key);
            }

            _persistentStates[key] = packet;
            _persistentLastValue[key] = packet;

            if (_plugin?.SessionState?.IsConnected != true) return;

            SendPersistentPacket(packet);
        }

        /// <summary>
        /// Applies a persistent world value from the peer.
        /// </summary>
        public void ApplyRemotePersistentState(PersistentStatePacket packet)
        {
            if (RemoteApplyScope.IsActive) return;
            if (packet.ItemKey == 0) return;

            // Idempotency: drop stale or duplicate revisions. A revision is only meaningful
            // together with its origin, and revision 0 means "unversioned" (legacy sender).
            if (IsStaleRevision(_appliedPersistentRevisions, packet.ItemKey, packet.StateRevision, packet.OriginPeerId))
                return;

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
                RecordRevision(_appliedPersistentRevisions, packet.ItemKey, packet.StateRevision, packet.OriginPeerId);
            }
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
                    if (item.GetCurrentValue() == value) return true;

                    using (RemoteApplyScope.Begin())
                    {
                        item.SetValueOverride(value);
                        item.SaveState();
                    }
                    return true;
                }

                if (packet.ValueType == PersistentValueType.Int)
                {
                    if (_intItems == null || !_intItems.TryGetValue(packet.ItemKey, out var item) || item == null)
                        return false;

                    if (item.GetCurrentValue() == packet.Value) return true;

                    using (RemoteApplyScope.Begin())
                    {
                        item.SetValueOverride(packet.Value);
                        item.SaveState();
                    }
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
                packet.StateRevision > cached.StateRevision)
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
            foreach (var pair in _pendingPersistentStates)
            {
                if (!TryApplyPersistentState(pair.Value)) continue;
                RecordRevision(_appliedPersistentRevisions, pair.Key, pair.Value.StateRevision, pair.Value.OriginPeerId);
                (applied ??= new List<ulong>()).Add(pair.Key);
            }

            if (applied != null)
            {
                for (int i = 0; i < applied.Count; i++)
                    _pendingPersistentStates.Remove(applied[i]);

                _pendingPersistentRetryAt = Time.unscaledTime + 0.5f;
            }
            else
            {
                // Keep retrying at a slower cadence; the object may activate later in the
                // same scene (lazy room loading) and must not be dropped permanently.
                // Ask for a fresh index on the next attempt: an object instantiated after
                // the first scan is not in the cached dictionary yet.
                _persistentIndexDirty = true;
                _pendingPersistentRetryAt = Time.unscaledTime + 5f;
            }

            while (_pendingPersistentOrder.Count > 0 &&
                   !_pendingPersistentStates.ContainsKey(_pendingPersistentOrder.Peek()))
            {
                _pendingPersistentOrder.Dequeue();
            }
        }

        private void SendPersistentPacket(PersistentStatePacket packet)
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
                SilksongCoopPlugin.LogWarn($"[EnemySync] Failed sending persistent state: {ex.Message}");
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

        #endregion

        #region Pickups

        /// <summary>
        /// Destroys the local copy of a drop that the peer collected. Uses the stable key first
        /// and falls back to a single nearby drop of the same currency, but only while the
        /// sender is confirmed to be in the local scene.
        /// </summary>
        /// <param name="currency">Currency type of the collected drop.</param>
        /// <param name="key">Stable pickup key from the sender, or 0 when unavailable.</param>
        /// <param name="x">World X reported by the sender.</param>
        /// <param name="y">World Y reported by the sender.</param>
        /// <param name="sender">Peer that collected the drop.</param>
        public void OnRemotePickupClaimed(
            SilksongCoop.Core.Protocol.Packets.CurrencyType currency,
            ulong key,
            float x,
            float y,
            NetworkPeerId sender)
        {
            var pickups = UnityEngine.Object.FindObjectsByType<CurrencyObjectBase>(FindObjectsSortMode.None);
            if (pickups == null || pickups.Length == 0) return;

            bool sameScene = IsSenderInLocalScene(sender);
            bool wantShards = currency == SilksongCoop.Core.Protocol.Packets.CurrencyType.ShellShards;
            CurrencyObjectBase? nearest = null;
            const float radiusSq = 2.5f * 2.5f;
            float nearestSq = radiusSq;
            int nearby = 0;

            for (int i = 0; i < pickups.Length; i++)
            {
                var pickup = pickups[i];
                if (pickup == null) continue;

                bool isShard = pickup is ShellShard;
                if (isShard != wantShards) continue;

                if (key != 0 && WorldObjectKey.ComputePickup(pickup) == key)
                {
                    pickup.Disable(0f);
                    return;
                }

                // A drop claimed in another room must not remove a local drop that happens
                // to lie at the same coordinates in the scene this peer has loaded.
                if (!sameScene) continue;
                if (float.IsNaN(x) || float.IsNaN(y)) continue;

                float dx = pickup.transform.position.x - x;
                float dy = pickup.transform.position.y - y;
                float distanceSq = dx * dx + dy * dy;
                if (distanceSq > radiusSq) continue;

                nearby++;
                if (distanceSq < nearestSq)
                {
                    nearestSq = distanceSq;
                    nearest = pickup;
                }
            }

            if (nearby > 1)
            {
                LogAmbiguousMatch($"Pickup: {nearby} candidates within 2.5 units");
                return;
            }

            if (nearby == 1 && nearest != null)
            {
                nearest.Disable(0f);
            }
        }

        #endregion

        #region Helpers

        private void SendBuffer(byte[] buffer, int length)
        {
            var transport = _plugin?.ActiveTransport;
            var peer = _plugin?.SessionState?.RemotePeerId;
            if (transport == null || !peer.HasValue) return;

            transport.Send(peer.Value, buffer, 0, length, DeliveryMethod.ReliableOrdered, WorldChannel);
        }

        private void DetectSceneChange()
        {
            string scene = SceneManager.GetActiveScene().name;
            if (string.Equals(scene, _sceneName, StringComparison.Ordinal))
                return;

            _sceneName = scene;
            _keyCache = new ConditionalWeakTable<HealthManager, KeyHolder>();
            _lastSnapshotHp.Clear();
            _lastSnapshotDead.Clear();
            _service.Reset();

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
        private void CachePendingDeadEnemy(EnemySnapshotEntry entry, uint timestamp)
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
                    Timestamp = timestamp
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

                if (_service.TryAcceptSnapshot(
                        pending.Entry.EnemyKey,
                        pending.Entry.Hp,
                        pending.Entry.IsDead,
                        pending.Timestamp))
                {
                    ApplyEnemyState(target, pending.Entry.Hp, pending.Entry.IsDead, authoritative: true);
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
        /// Whether this enemy is a boss that does not respawn with its scene. BossSceneController
        /// covers scripted boss rooms; a boss sequence covers Godhome-style fights where every
        /// active enemy belongs to the run.
        /// </summary>
        private static bool IsBoss(HealthManager healthManager)
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

                if (BossSequenceController.IsInSequence)
                    return true;
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

        private ulong GetKey(HealthManager healthManager)
        {
            if (healthManager == null) return 0;

            if (_keyCache.TryGetValue(healthManager, out var holder))
                return holder.Key;

            ulong key = WorldObjectKey.Compute(healthManager.transform);
            if (key == 0) return 0;

            _keyCache.Add(healthManager, new KeyHolder { Key = key });
            return key;
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
                        return hm;

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
                SilksongCoopPlugin.LogWarn($"[EnemySync] Enemy lookup failed: {ex.Message}");
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
