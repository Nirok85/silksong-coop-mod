using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Progression;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Mod.State;
using UnityEngine;

namespace SilksongCoop.Mod.Services
{
    /// <summary>
    /// Replicates the PlayerData bool/int flags that changed while an NPC dialogue was
    /// open. Quest state in Silksong is spread across NPC/quest/conversation flags; this
    /// manager diffs a conservative allowlist around each dialogue instead of copying every
    /// PlayerData field. Flags changed by the partner are applied under
    /// <see cref="RemoteApplyScope"/> and do not re-enter the local dialogue diff.
    /// </summary>
    public sealed class QuestStateSyncManager
    {
        private const BindingFlags FieldFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly float FlushDelaySeconds = 0.5f;

        private readonly struct BoolAccessor
        {
            public readonly Func<PlayerData, bool> Get;
            public readonly Action<PlayerData, bool> Set;

            public BoolAccessor(Func<PlayerData, bool> get, Action<PlayerData, bool> set)
            {
                Get = get;
                Set = set;
            }
        }

        private readonly struct IntAccessor
        {
            public readonly Func<PlayerData, int> Get;
            public readonly Action<PlayerData, int> Set;

            public IntAccessor(Func<PlayerData, int> get, Action<PlayerData, int> set)
            {
                Get = get;
                Set = set;
            }
        }

        private readonly SilksongCoopPlugin _plugin;
        private readonly Dictionary<string, BoolAccessor> _boolFields = new Dictionary<string, BoolAccessor>(StringComparer.Ordinal);
        private readonly Dictionary<string, IntAccessor> _intFields = new Dictionary<string, IntAccessor>(StringComparer.Ordinal);

        private QuestFlagSnapshot? _before;
        // Every dialogue that ended but has not flushed yet. A list (not a single field) is
        // required because two dialogues can finish within FlushDelaySeconds: overwriting
        // the baseline would let one coroutine clear another's marker and either re-send an
        // already replicated change or lose a legitimate diff.
        private readonly List<QuestFlagSnapshot> _pendingFlushes = new List<QuestFlagSnapshot>();
        private QuestFlagSnapshot? _lastSnapshot;
        private float _nextPollAt;
        private int _generation;
        private uint _sequence;
        // Reliable packets dropped by transport backpressure are lost. A full allowlisted
        // snapshot is cheap and idempotent, so send one after the backlog drains.
        private long _observedDroppedReliable;
        private bool _resyncPending;
        private readonly Dictionary<string, QuestFlagValue> _authoredChanges = new(StringComparer.Ordinal);

        public QuestStateSyncManager(SilksongCoopPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            CacheQuestFields();
        }

        /// <summary>Number of PlayerData fields that can participate in quest sync.</summary>
        public int TrackedFieldCount => _boolFields.Count + _intFields.Count;

        private bool Enabled => _plugin.SyncQuestFlagsConfig?.Value ?? true;

        private bool CanSync =>
            Enabled &&
            _plugin.SessionState?.IsConnected == true &&
            _plugin.ActiveTransport != null &&
            _plugin.SessionState?.RemotePeerId != null;

        /// <summary>
        /// Called before an NPC dialogue starts. If a previous dialogue never reported its
        /// end (quest-board UI, interrupted scene), its stale baseline is dropped and a new
        /// one is captured from the current state.
        /// </summary>
        public void BeginDialogue()
        {
            if (!CanSync) return;

            // A flag can change after the last 1 Hz poll but before this dialogue starts.
            // It is present in the new "before" baseline, so the dialogue diff would never
            // contain it. Flush that window first; the dialogue baseline is captured next.
            FlushPollChangesBeforeDialogue();

            // Do not invalidate a flush queued by the previous dialogue: rapid
            // dialogue-to-dialogue transitions must still send the earlier changes.
            _before = Capture();
        }

        /// <summary>
        /// Sends changes accumulated since the last poll before a dialogue baseline replaces
        /// the poll snapshot. Seed-only on the first observation after connecting, so joining
        /// still cannot overwrite the partner's existing progress.
        /// </summary>
        private void FlushPollChangesBeforeDialogue()
        {
            QuestFlagSnapshot current;
            try
            {
                current = Capture();
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[QuestSync] Pre-dialogue capture failed: {ex.Message}");
                return;
            }

            if (_lastSnapshot == null)
            {
                _lastSnapshot = current;
                return;
            }

            List<QuestFlagChange> changes;
            try
            {
                changes = QuestFlagDiff.Compute(_lastSnapshot, current);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[QuestSync] Pre-dialogue diff failed: {ex.Message}");
                return;
            }

            _lastSnapshot = current;
            SendChanges(changes);
        }

        /// <summary>
        /// Called after an NPC dialogue ends. The actual diff is taken after a short delay
        /// because some quest FSMs set their flags in an end-of-dialogue coroutine.
        /// </summary>
        public void EndDialogue()
        {
            var before = _before;
            if (before == null) return;

            _before = null;

            if (!CanSync) return;

            // Keep the baseline until this flush finishes. Poll() and ApplyRemote() inspect
            // the list, so an overlapping dialogue cannot hide an in-flight diff.
            _pendingFlushes.Add(before);
            int generation = _generation;
            _plugin.StartCoroutine(FlushAfterDelay(before, generation));
        }

        /// <summary>
        /// Drops an in-flight dialogue baseline. A diff already scheduled by
        /// <see cref="EndDialogue"/> is allowed to finish; the quest flags were set even if
        /// the dialogue box is leaving the scene.
        /// </summary>
        public void CancelDialogue()
        {
            _before = null;
        }

        public void ResetSession()
        {
            _generation++;
            _before = null;
            _pendingFlushes.Clear();
            _lastSnapshot = null;
            _nextPollAt = 0f;
        }

        /// <summary>
        /// Safety net for quest flags changed outside NPC dialogue (quest-item pickups,
        /// scripts, cutscenes). Runs at 1 Hz while connected and idle; the first snapshot
        /// after a connection only seeds the baseline, so joining cannot overwrite the
        /// partner's existing progress.
        /// </summary>
        public void Poll()
        {
            if (!CanSync)
            {
                _lastSnapshot = null;
                return;
            }

            TrackReliableDrops();
            if (_resyncPending && _plugin.ActiveTransport!.ReliableBacklogCount <= NetworkLimits.ResyncBacklogThreshold)
            {
                _resyncPending = false;
                SendFullSnapshot();
            }

            // Dialogue hooks send their own diff; do not duplicate it from the poll.
            if (_before != null || _pendingFlushes.Count > 0) return;
            if (Time.unscaledTime < _nextPollAt) return;
            _nextPollAt = Time.unscaledTime + 1f;

            QuestFlagSnapshot current;
            try
            {
                current = Capture();
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[QuestSync] Poll capture failed: {ex.Message}");
                return;
            }

            if (_lastSnapshot == null)
            {
                // Do not seed the baseline with an empty snapshot captured before PlayerData
                // has loaded. Otherwise the next poll would diff empty -> full and broadcast
                // every tracked flag, potentially overwriting the partner's newer state.
                if (PlayerData.instance == null) return;
                _lastSnapshot = current;
                return;
            }

            var changes = QuestFlagDiff.Compute(_lastSnapshot, current);
            _lastSnapshot = current;
            SendChanges(changes);
        }

        private void TrackReliableDrops()
        {
            if (_plugin.ActiveTransport is not IReliableDropDiagnostics diagnostics) return;

            long dropped = diagnostics.DroppedReliablePackets;
            if (dropped < _observedDroppedReliable)
            {
                _observedDroppedReliable = dropped;
            }
            else if (dropped > _observedDroppedReliable)
            {
                _observedDroppedReliable = dropped;
                _resyncPending = true;
            }
        }

        /// <summary>
        /// Re-sends every tracked allowlisted flag. Used after the transport dropped a
        /// reliable packet, where a diff is not enough because the lost change is unknown.
        /// </summary>
        private void SendFullSnapshot() => ReplayAuthoredChanges();

        public void ClearAuthoredChanges() => _authoredChanges.Clear();

        public void ReplayAuthoredChanges(NetworkPeerId? peer = null)
        {
            var changes = new List<QuestFlagChange>(_authoredChanges.Count);
            foreach (var pair in _authoredChanges)
                changes.Add(new QuestFlagChange(pair.Key, pair.Value));
            SendChanges(changes, peer);
        }

        /// <summary>
        /// Applies a quest-state packet from the partner. Only cached, allowlisted fields
        /// can be written; unknown names are ignored defensively.
        /// </summary>
        public void ApplyRemote(QuestStatePacket packet)
        {
            if (!Enabled || packet.Entries == null || packet.Count <= 0) return;

            var playerData = PlayerData.instance;
            if (playerData == null) return;

            int applied = 0;
            using (RemoteApplyScope.Begin())
            {
                try
                {
                    int count = Math.Min(packet.Count, packet.Entries.Length);
                    for (int i = 0; i < count; i++)
                    {
                        var entry = packet.Entries[i];
                        if (entry.Kind == QuestFlagKind.Bool &&
                            _boolFields.TryGetValue(entry.Key, out var boolAccessor))
                        {
                            // Quest flags are monotonic: a true flag must not be cleared by a
                            // stale remote packet. Only apply when the remote value advances.
                            bool current = boolAccessor.Get(playerData);
                            if (entry.BoolValue && !current)
                            {
                                boolAccessor.Set(playerData, true);
                                _authoredChanges.Remove(entry.Key);
                                _before?.SetBool(entry.Key, true);
                                for (int p = 0; p < _pendingFlushes.Count; p++)
                                    _pendingFlushes[p].SetBool(entry.Key, true);
                                _lastSnapshot?.SetBool(entry.Key, true);
                                applied++;
                            }
                            else if (!entry.BoolValue && !current)
                            {
                                // Both sides agree it is false; keep baselines in sync.
                                _before?.SetBool(entry.Key, false);
                                for (int p = 0; p < _pendingFlushes.Count; p++)
                                    _pendingFlushes[p].SetBool(entry.Key, false);
                                _lastSnapshot?.SetBool(entry.Key, false);
                            }
                        }
                        else if (entry.Kind == QuestFlagKind.Int &&
                                 _intFields.TryGetValue(entry.Key, out var intAccessor))
                        {
                            int current = intAccessor.Get(playerData);
                            // Quest stages only move forward; a lower stage must not roll
                            // back a newer local/remote one.
                            if (entry.IntValue > current)
                            {
                                intAccessor.Set(playerData, entry.IntValue);
                                _authoredChanges.Remove(entry.Key);
                                _before?.SetInt(entry.Key, entry.IntValue);
                                for (int p = 0; p < _pendingFlushes.Count; p++)
                                    _pendingFlushes[p].SetInt(entry.Key, entry.IntValue);
                                _lastSnapshot?.SetInt(entry.Key, entry.IntValue);
                                applied++;
                            }
                            else if (entry.IntValue == current)
                            {
                                _before?.SetInt(entry.Key, current);
                                for (int p = 0; p < _pendingFlushes.Count; p++)
                                    _pendingFlushes[p].SetInt(entry.Key, current);
                                _lastSnapshot?.SetInt(entry.Key, current);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    SilksongCoopPlugin.LogWarn($"[QuestSync] Failed applying remote quest flags: {ex.Message}");
                }
            }

            if (applied > 0)
            {
                SilksongCoopPlugin.LogInfo($"[QuestSync] Applied {applied} remote quest flag(s)");
            }
        }

        private IEnumerator FlushAfterDelay(QuestFlagSnapshot before, int generation)
        {
            yield return new WaitForSecondsRealtime(FlushDelaySeconds);

            try
            {
                if (generation != _generation) yield break;
                if (!CanSync) yield break;

                QuestFlagSnapshot after;
                try
                {
                    after = Capture();
                }
                catch (Exception ex)
                {
                    SilksongCoopPlugin.LogWarn($"[QuestSync] Failed capturing quest flags: {ex.Message}");
                    yield break;
                }

                List<QuestFlagChange> changes;
                try
                {
                    changes = QuestFlagDiff.Compute(before, after);
                }
                catch (Exception ex)
                {
                    SilksongCoopPlugin.LogWarn($"[QuestSync] Failed diffing quest flags: {ex.Message}");
                    yield break;
                }

                _lastSnapshot = after;
                SendChanges(changes);
            }
            finally
            {
                // Remove only this flush's baseline; another dialogue may still be waiting.
                _pendingFlushes.Remove(before);
            }
        }

        private QuestFlagSnapshot Capture()
        {
            var snapshot = new QuestFlagSnapshot();
            var playerData = PlayerData.instance;
            if (playerData == null) return snapshot;

            foreach (var pair in _boolFields)
            {
                snapshot.SetBool(pair.Key, pair.Value.Get(playerData));
            }

            foreach (var pair in _intFields)
            {
                snapshot.SetInt(pair.Key, pair.Value.Get(playerData));
            }

            return snapshot;
        }

        private void SendChanges(List<QuestFlagChange> changes, NetworkPeerId? target = null)
        {
            if (changes.Count == 0 || !CanSync) return;

            var transport = _plugin.ActiveTransport!;
            if (!target.HasValue)
                foreach (var change in changes)
                    _authoredChanges[change.Key] = change.Value;
            var entries = new List<QuestFlagEntry>(Math.Min(changes.Count, QuestStatePacket.MaxEntries));
            int encodedSize = QuestStatePacket.EncodedOverhead;

            for (int i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                int entrySize = QuestStatePacket.GetEntryEncodedSize(change.Key, change.Value.Kind);

                // A single name that cannot fit even in an otherwise empty packet would make
                // every batch overflow, so drop just that flag with a clear log line.
                if (QuestStatePacket.EncodedOverhead + entrySize > QuestStatePacket.MaxEncodedBytes)
                {
                    SilksongCoopPlugin.LogWarn(
                        $"[QuestSync] Skipping oversized quest flag '{change.Key}' " +
                        $"({entrySize} bytes, limit {QuestStatePacket.MaxEncodedBytes})");
                    continue;
                }

                // Split by serialized size as well as by entry count: 64 long PlayerData
                // names exceed the UDP receive buffer even though they pass the old
                // MaxEntries check, and an oversized reliable datagram is never delivered.
                if (entries.Count >= QuestStatePacket.MaxEntries ||
                    encodedSize + entrySize > QuestStatePacket.MaxEncodedBytes)
                {
                    SendBatch(transport, entries, target);
                    entries.Clear();
                    encodedSize = QuestStatePacket.EncodedOverhead;
                }

                entries.Add(change.Value.Kind == QuestFlagKind.Int
                    ? new QuestFlagEntry
                    {
                        Kind = QuestFlagKind.Int,
                        Key = change.Key,
                        IntValue = change.Value.IntValue
                    }
                    : new QuestFlagEntry
                    {
                        Kind = QuestFlagKind.Bool,
                        Key = change.Key,
                        BoolValue = change.Value.BoolValue
                    });
                encodedSize += entrySize;
            }

            if (entries.Count > 0)
                SendBatch(transport, entries, target);
        }

        private void SendBatch(INetworkTransport transport, List<QuestFlagEntry> entries, NetworkPeerId? target)
        {
            var packet = new QuestStatePacket
            {
                Header = new PacketHeader(PacketType.QuestState, NetworkChannels.World, ++_sequence, NetworkClock.NowMs),
                Count = entries.Count,
                Entries = entries.ToArray()
            };

            if (target.HasValue)
                PacketSender.SendTo(transport, target.Value, packet, DeliveryMethod.ReliableOrdered,
                    NetworkChannels.World, QuestStatePacket.MaxEncodedBytes);
            else
                PacketSender.SendToAll(transport, packet, DeliveryMethod.ReliableOrdered,
                    NetworkChannels.World, QuestStatePacket.MaxEncodedBytes, diagnostics: null, logContext: "[QuestSync]");
        }

        private void CacheQuestFields()
        {
            try
            {
                foreach (var field in typeof(PlayerData).GetFields(FieldFlags))
                {
                    if (field.IsInitOnly || field.IsLiteral) continue;
                    if (!QuestFlagFilter.IsQuestRelated(field.Name)) continue;

                    if (field.FieldType == typeof(bool))
                    {
                        _boolFields[field.Name] = CreateBoolAccessor(field);
                    }
                    else if (field.FieldType == typeof(int))
                    {
                        _intFields[field.Name] = CreateIntAccessor(field);
                    }
                }

                SilksongCoopPlugin.LogInfo(
                    $"[QuestSync] Tracking {_boolFields.Count} bool and {_intFields.Count} int quest flag(s)");
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[QuestSync] Failed caching PlayerData fields: {ex.Message}");
            }
        }

        /// <summary>
        /// Builds a typed bool accessor pair once. Compilation failures (for example a field
        /// declared on an unexpected base type) fall back to reflection so quest sync keeps
        /// working instead of dropping the flag.
        /// </summary>
        private static BoolAccessor CreateBoolAccessor(FieldInfo field)
        {
            try
            {
                var instance = Expression.Parameter(typeof(PlayerData), "data");
                var value = Expression.Parameter(typeof(bool), "value");
                var fieldAccess = Expression.Field(instance, field);

                var getter = Expression.Lambda<Func<PlayerData, bool>>(fieldAccess, instance).Compile();
                var setter = Expression.Lambda<Action<PlayerData, bool>>(
                    Expression.Assign(fieldAccess, value), instance, value).Compile();
                return new BoolAccessor(getter, setter);
            }
            catch
            {
                return new BoolAccessor(
                    data => field.GetValue(data) is bool current && current,
                    (data, current) => field.SetValue(data, current));
            }
        }

        /// <summary>
        /// Builds a typed int accessor pair once, with the same reflection fallback as the
        /// bool path.
        /// </summary>
        private static IntAccessor CreateIntAccessor(FieldInfo field)
        {
            try
            {
                var instance = Expression.Parameter(typeof(PlayerData), "data");
                var value = Expression.Parameter(typeof(int), "value");
                var fieldAccess = Expression.Field(instance, field);

                var getter = Expression.Lambda<Func<PlayerData, int>>(fieldAccess, instance).Compile();
                var setter = Expression.Lambda<Action<PlayerData, int>>(
                    Expression.Assign(fieldAccess, value), instance, value).Compile();
                return new IntAccessor(getter, setter);
            }
            catch
            {
                return new IntAccessor(
                    data => field.GetValue(data) is int current ? current : 0,
                    (data, current) => field.SetValue(data, current));
            }
        }
    }
}
