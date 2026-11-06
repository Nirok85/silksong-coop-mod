using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
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

        private readonly SilksongCoopPlugin _plugin;
        private readonly Dictionary<string, FieldInfo> _boolFields = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
        private readonly Dictionary<string, FieldInfo> _intFields = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);

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

            // Do not invalidate a flush queued by the previous dialogue: rapid
            // dialogue-to-dialogue transitions must still send the earlier changes.
            _before = Capture();
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
            if (_resyncPending && _plugin.ActiveTransport!.ReliableBacklogCount <= 128)
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
        private void SendFullSnapshot()
        {
            QuestFlagSnapshot current;
            try
            {
                current = Capture();
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[QuestSync] Full resync capture failed: {ex.Message}");
                return;
            }

            var changes = new List<QuestFlagChange>(current.Count);
            foreach (var pair in current.Values)
                changes.Add(new QuestFlagChange(pair.Key, pair.Value));

            SilksongCoopPlugin.LogInfo($"[QuestSync] Re-sending {changes.Count} flag(s) after a reliable drop");
            SendChanges(changes);
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
                            _boolFields.TryGetValue(entry.Key, out var boolField))
                        {
                            boolField.SetValue(playerData, entry.BoolValue);
                            _before?.SetBool(entry.Key, entry.BoolValue);
                            for (int p = 0; p < _pendingFlushes.Count; p++)
                                _pendingFlushes[p].SetBool(entry.Key, entry.BoolValue);
                            _lastSnapshot?.SetBool(entry.Key, entry.BoolValue);
                            applied++;
                        }
                        else if (entry.Kind == QuestFlagKind.Int &&
                                 _intFields.TryGetValue(entry.Key, out var intField))
                        {
                            intField.SetValue(playerData, entry.IntValue);
                            _before?.SetInt(entry.Key, entry.IntValue);
                            for (int p = 0; p < _pendingFlushes.Count; p++)
                                _pendingFlushes[p].SetInt(entry.Key, entry.IntValue);
                            _lastSnapshot?.SetInt(entry.Key, entry.IntValue);
                            applied++;
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
                if (pair.Value.GetValue(playerData) is bool value)
                {
                    snapshot.SetBool(pair.Key, value);
                }
            }

            foreach (var pair in _intFields)
            {
                if (pair.Value.GetValue(playerData) is int value)
                {
                    snapshot.SetInt(pair.Key, value);
                }
            }

            return snapshot;
        }

        private void SendChanges(List<QuestFlagChange> changes)
        {
            if (changes.Count == 0 || !CanSync) return;

            var transport = _plugin.ActiveTransport!;
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
                    SendBatch(transport, entries);
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
                SendBatch(transport, entries);
        }

        private void SendBatch(INetworkTransport transport, List<QuestFlagEntry> entries)
        {
            var packet = new QuestStatePacket
            {
                Header = new PacketHeader(PacketType.QuestState, NetworkChannels.World, ++_sequence, NetworkClock.NowMs),
                Count = entries.Count,
                Entries = entries.ToArray()
            };

            byte[] buffer = ArrayPool<byte>.Shared.Rent(QuestStatePacket.MaxEncodedBytes);
            try
            {
                var writer = new PacketWriter(buffer);
                packet.Serialize(ref writer);
                transport.SendToAll(buffer, 0, writer.Position, DeliveryMethod.ReliableOrdered, NetworkChannels.World);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[QuestSync] Failed sending quest flags: {ex.Message}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
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
                        _boolFields[field.Name] = field;
                    }
                    else if (field.FieldType == typeof(int))
                    {
                        _intFields[field.Name] = field;
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
    }
}
