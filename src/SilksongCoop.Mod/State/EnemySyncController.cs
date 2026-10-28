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

        private const byte WorldChannel = 4;

        private struct DeadEnemy
        {
            public float X;
            public float Y;
        }

        private const int MaxDeadRegistry = 256;

        private SilksongCoopPlugin? _plugin;
        private readonly EnemySyncService _service = new EnemySyncService();
        private ConditionalWeakTable<HealthManager, KeyHolder> _keyCache = new ConditionalWeakTable<HealthManager, KeyHolder>();
        private readonly Dictionary<ulong, int> _lastSnapshotHp = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, bool> _lastSnapshotDead = new Dictionary<ulong, bool>();
        private readonly Dictionary<ulong, DeadEnemy> _deadEnemies = new Dictionary<ulong, DeadEnemy>();
        private readonly Dictionary<ulong, WorldEventPacket> _worldEvents = new Dictionary<ulong, WorldEventPacket>();
        private readonly Dictionary<ulong, PersistentStatePacket> _persistentStates = new Dictionary<ulong, PersistentStatePacket>();
        private readonly Dictionary<ulong, PersistentStatePacket> _persistentLastValue = new Dictionary<ulong, PersistentStatePacket>();
        private bool _worldReplayPending;

        private string _sceneName = string.Empty;
        private float _snapshotTimer;
        private uint _packetSequence;

        private static AccessTools.FieldRef<HealthManager, int>? _initHpRef;
        private static bool _initHpResolved;

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
            _sceneName = string.Empty;
            _snapshotTimer = 0f;

            // The dead/world registries deliberately survive a reconnect: the next host
            // snapshot/world replay catches a newly connected client up.
            _worldReplayPending = true;
        }

        private void Update()
        {
            if (_plugin?.SessionState?.IsConnected != true) return;

            DetectSceneChange();

            // Only the host is authoritative for periodic snapshots and world catch-up.
            if (_plugin.SessionState.IsHost != true) return;

            if (_worldReplayPending)
            {
                _worldReplayPending = false;
                ReplayWorldEvents();
            }

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
                Header = new PacketHeader(PacketType.EnemySync, WorldChannel, ++_packetSequence, (uint)Environment.TickCount),
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

            // Register even before a peer connects so a later client is caught up.
            _deadEnemies[key] = new DeadEnemy
            {
                X = target.transform.position.x,
                Y = target.transform.position.y
            };
            if (_deadEnemies.Count > MaxDeadRegistry)
            {
                _deadEnemies.Clear();
            }

            if (_plugin?.SessionState?.IsConnected != true) return;

            var packet = new EnemySyncPacket
            {
                Header = new PacketHeader(PacketType.EnemySync, WorldChannel, ++_packetSequence, (uint)Environment.TickCount),
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
        public void ApplyRemoteHit(EnemySyncPacket packet)
        {
            bool dead = packet.EventType == EnemySyncEventType.Death;
            if (!_service.TryAcceptHit(packet.EnemyKey, packet.Hp, dead)) return;

            var target = FindEnemy(packet.EnemyKey, packet.PositionX, packet.PositionY);
            if (target == null) return;

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
                ArrayPool<byte>.Shared.Return(buffer);
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
            // EnumerateActiveEnemies(). Replay them from the local death registry.
            foreach (var dead in _deadEnemies)
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
                Header = new PacketHeader(PacketType.EnemySnapshot, WorldChannel, ++_packetSequence, (uint)Environment.TickCount),
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
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Applies the host's authoritative snapshot (client side only).
        /// </summary>
        public void ApplyRemoteSnapshot(EnemySnapshotPacket packet)
        {
            if (_plugin?.SessionState?.IsHost == true) return;
            if (packet.Entries == null) return;

            int count = Math.Min(packet.Count, packet.Entries.Length);
            for (int i = 0; i < count; i++)
            {
                var entry = packet.Entries[i];
                if (!_service.TryAcceptSnapshot(entry.EnemyKey, entry.Hp, entry.IsDead, packet.Header.Timestamp))
                    continue;

                var target = FindEnemy(entry.EnemyKey, entry.PositionX, entry.PositionY);
                if (target == null) continue;

                ApplyEnemyState(target, entry.Hp, entry.IsDead, authoritative: true);
            }
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
                Header = new PacketHeader(PacketType.WorldEvent, WorldChannel, ++_packetSequence, (uint)Environment.TickCount),
                ObjectKey = key,
                EventType = eventType,
                PositionX = transform.position.x,
                PositionY = transform.position.y
            };

            // Register even before a peer connects so a later client is caught up.
            if (_worldEvents.Count >= 1024)
            {
                _worldEvents.Clear();
            }
            _worldEvents[key] = packet;

            if (_plugin?.SessionState?.IsConnected != true) return;

            SendWorldPacket(packet);
        }

        private void ReplayWorldEvents()
        {
            if (_plugin?.SessionState?.IsConnected != true) return;

            foreach (var stored in _worldEvents.Values)
            {
                SendWorldPacket(new WorldEventPacket
                {
                    Header = new PacketHeader(PacketType.WorldEvent, WorldChannel, ++_packetSequence, (uint)Environment.TickCount),
                    ObjectKey = stored.ObjectKey,
                    EventType = stored.EventType,
                    PositionX = stored.PositionX,
                    PositionY = stored.PositionY
                });
            }

            foreach (var stored in _persistentStates.Values)
            {
                SendPersistentPacket(new PersistentStatePacket
                {
                    Header = new PacketHeader(PacketType.PersistentState, WorldChannel, ++_packetSequence, (uint)Environment.TickCount),
                    ItemKey = stored.ItemKey,
                    ValueType = stored.ValueType,
                    Value = stored.Value
                });
            }
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
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Applies a permanent world change from the peer.
        /// </summary>
        public void ApplyRemoteWorldEvent(WorldEventPacket packet)
        {
            if (RemoteApplyScope.IsActive) return;

            using (RemoteApplyScope.Begin())
            {
                try
                {
                    switch (packet.EventType)
                    {
                        case WorldEventType.BreakableBroken:
                            var breakable = FindObjectByKey<Breakable>(packet.ObjectKey);
                            breakable?.BreakSelf();
                            break;

                        case WorldEventType.GateOpened:
                            var gate = FindObjectByKey<Gate>(packet.ObjectKey);
                            gate?.Open();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    SilksongCoopPlugin.LogWarn($"[EnemySync] Failed applying world event: {ex.Message}");
                }
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
                Header = new PacketHeader(PacketType.PersistentState, WorldChannel, ++_packetSequence, (uint)Environment.TickCount),
                ItemKey = key,
                ValueType = valueType,
                Value = value
            };

            if (_persistentStates.Count >= 2048)
            {
                _persistentStates.Clear();
                _persistentLastValue.Clear();
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

            using (RemoteApplyScope.Begin())
            {
                try
                {
                    if (packet.ValueType == PersistentValueType.Bool)
                    {
                        var item = FindPersistentBool(packet.ItemKey);
                        if (item == null) return;

                        bool value = packet.Value != 0;
                        if (item.GetCurrentValue() == value) return;

                        item.SetValueOverride(value);
                        item.SaveState();
                    }
                    else if (packet.ValueType == PersistentValueType.Int)
                    {
                        var item = FindPersistentInt(packet.ItemKey);
                        if (item == null) return;

                        if (item.GetCurrentValue() == packet.Value) return;

                        item.SetValueOverride(packet.Value);
                        item.SaveState();
                    }
                }
                catch (Exception ex)
                {
                    SilksongCoopPlugin.LogWarn($"[EnemySync] Failed applying persistent state: {ex.Message}");
                }
            }
        }

        private static PersistentItem<bool>? FindPersistentBool(ulong key)
        {
            var items = UnityEngine.Object.FindObjectsByType<PersistentBoolItem>(FindObjectsSortMode.None);
            for (int i = 0; i < items.Length; i++)
            {
                var item = items[i];
                if (item == null) continue;

                if (WorldObjectKey.ComputePersistent(item.GetSceneName(), item.GetId(), (int)PersistentValueType.Bool) == key)
                    return item;
            }

            return null;
        }

        private static PersistentItem<int>? FindPersistentInt(ulong key)
        {
            var items = UnityEngine.Object.FindObjectsByType<PersistentIntItem>(FindObjectsSortMode.None);
            for (int i = 0; i < items.Length; i++)
            {
                var item = items[i];
                if (item == null) continue;

                if (WorldObjectKey.ComputePersistent(item.GetSceneName(), item.GetId(), (int)PersistentValueType.Int) == key)
                    return item;
            }

            return null;
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
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static T? FindObjectByKey<T>(ulong key) where T : Component
        {
            var objects = UnityEngine.Object.FindObjectsByType<T>(FindObjectsSortMode.None);
            for (int i = 0; i < objects.Length; i++)
            {
                var component = objects[i];
                if (component == null) continue;
                if (WorldObjectKey.Compute(component.transform) == key)
                    return component;
            }

            return null;
        }

        #endregion

        #region Pickups

        /// <summary>
        /// Destroys the local copy of a drop that the peer collected. Uses the stable key first
        /// and falls back to the nearest drop of the same currency within a small radius.
        /// </summary>
        public void OnRemotePickupClaimed(SilksongCoop.Core.Protocol.Packets.CurrencyType currency, ulong key, float x, float y)
        {
            var pickups = UnityEngine.Object.FindObjectsByType<CurrencyObjectBase>(FindObjectsSortMode.None);
            if (pickups == null || pickups.Length == 0) return;

            bool wantShards = currency == SilksongCoop.Core.Protocol.Packets.CurrencyType.ShellShards;
            CurrencyObjectBase? nearest = null;
            float nearestSq = 2.5f * 2.5f;

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

                if (float.IsNaN(x) || float.IsNaN(y)) continue;

                float dx = pickup.transform.position.x - x;
                float dy = pickup.transform.position.y - y;
                float distanceSq = dx * dx + dy * dy;
                if (distanceSq <= nearestSq)
                {
                    nearestSq = distanceSq;
                    nearest = pickup;
                }
            }

            if (nearest != null)
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
            _deadEnemies.Clear();
            _worldEvents.Clear();
            _worldReplayPending = false;
            _service.Reset();
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

        private HealthManager? FindEnemy(ulong key, float x, float y)
        {
            HealthManager? nearest = null;
            float nearestSq = 2.5f * 2.5f;

            try
            {
                foreach (var hm in HealthManager.EnumerateActiveEnemies())
                {
                    if (hm == null) continue;

                    if (GetKey(hm) == key)
                        return hm;

                    if (hm.GetIsDead()) continue;

                    float dx = hm.transform.position.x - x;
                    float dy = hm.transform.position.y - y;
                    float distanceSq = dx * dx + dy * dy;
                    if (distanceSq <= nearestSq)
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

            return nearest;
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
