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
        private QuestFlagSnapshot? _pendingBefore;
        private QuestFlagSnapshot? _lastSnapshot;
        private float _nextPollAt;
        private int _generation;
        private uint _sequence;

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
            _pendingBefore = before;
            int generation = _generation;

            if (!CanSync)
            {
                _pendingBefore = null;
                return;
            }
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
            _pendingBefore = null;
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

            // Dialogue hooks send their own diff; do not duplicate it from the poll.
            if (_before != null || _pendingBefore != null) return;
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
                            _pendingBefore?.SetBool(entry.Key, entry.BoolValue);
                            _lastSnapshot?.SetBool(entry.Key, entry.BoolValue);
                            applied++;
                        }
                        else if (entry.Kind == QuestFlagKind.Int &&
                                 _intFields.TryGetValue(entry.Key, out var intField))
                        {
                            intField.SetValue(playerData, entry.IntValue);
                            _before?.SetInt(entry.Key, entry.IntValue);
                            _pendingBefore?.SetInt(entry.Key, entry.IntValue);
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

            if (generation != _generation) yield break;
            _pendingBefore = null;
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

            for (int offset = 0; offset < changes.Count; offset += QuestStatePacket.MaxEntries)
            {
                int count = Math.Min(QuestStatePacket.MaxEntries, changes.Count - offset);
                var entries = new QuestFlagEntry[count];
                for (int i = 0; i < count; i++)
                {
                    var change = changes[offset + i];
                    entries[i] = change.Value.Kind == QuestFlagKind.Int
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
                        };
                }

                var packet = new QuestStatePacket
                {
                    Header = new PacketHeader(PacketType.QuestState, NetworkChannels.World, ++_sequence, NetworkClock.NowMs),
                    Count = count,
                    Entries = entries
                };

                byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
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
