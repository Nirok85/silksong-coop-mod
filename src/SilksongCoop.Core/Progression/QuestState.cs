using System;
using System.Collections.Generic;

namespace SilksongCoop.Core.Progression
{
    /// <summary>
    /// The two PlayerData field types the quest sync understands. Bools cover dialogue
    /// progress/offered/talked flags; ints cover quest counters.
    /// </summary>
    public enum QuestFlagKind : byte
    {
        Bool = 0,
        Int = 1
    }

    /// <summary>
    /// One PlayerData value captured before or after a dialogue.
    /// </summary>
    public readonly struct QuestFlagValue : IEquatable<QuestFlagValue>
    {
        public QuestFlagKind Kind { get; }
        public bool BoolValue { get; }
        public int IntValue { get; }

        private QuestFlagValue(QuestFlagKind kind, bool boolValue, int intValue)
        {
            Kind = kind;
            BoolValue = boolValue;
            IntValue = intValue;
        }

        public static QuestFlagValue FromBool(bool value) => new QuestFlagValue(QuestFlagKind.Bool, value, 0);

        public static QuestFlagValue FromInt(int value) => new QuestFlagValue(QuestFlagKind.Int, false, value);

        public bool Equals(QuestFlagValue other)
            => Kind == other.Kind && BoolValue == other.BoolValue && IntValue == other.IntValue;

        public override bool Equals(object? obj) => obj is QuestFlagValue other && Equals(other);

        public override int GetHashCode() => HashCode.Combine((byte)Kind, BoolValue, IntValue);

        public static bool operator ==(QuestFlagValue left, QuestFlagValue right) => left.Equals(right);

        public static bool operator !=(QuestFlagValue left, QuestFlagValue right) => !left.Equals(right);
    }

    /// <summary>
    /// A captured set of quest-related PlayerData values. Keys that the filter rejects are
    /// ignored, so a bug in the caller cannot turn this into a generic PlayerData copier.
    /// </summary>
    public sealed class QuestFlagSnapshot
    {
        private readonly Dictionary<string, QuestFlagValue> _values =
            new Dictionary<string, QuestFlagValue>(StringComparer.Ordinal);

        public int Count => _values.Count;

        public IReadOnlyDictionary<string, QuestFlagValue> Values => _values;

        public void SetBool(string key, bool value)
        {
            if (!QuestFlagFilter.IsQuestRelated(key)) return;
            _values[key] = QuestFlagValue.FromBool(value);
        }

        public void SetInt(string key, int value)
        {
            if (!QuestFlagFilter.IsQuestRelated(key)) return;
            _values[key] = QuestFlagValue.FromInt(value);
        }

        public bool TryGet(string key, out QuestFlagValue value) => _values.TryGetValue(key, out value);
    }

    /// <summary>
    /// One changed quest flag, ready to be sent to the partner.
    /// </summary>
    public readonly struct QuestFlagChange
    {
        public string Key { get; }
        public QuestFlagValue Value { get; }

        public QuestFlagChange(string key, QuestFlagValue value)
        {
            Key = key;
            Value = value;
        }
    }

    /// <summary>
    /// Computes the values that changed between two dialogue snapshots. Removals are not
    /// produced because PlayerData fields are not removed at runtime.
    /// </summary>
    public static class QuestFlagDiff
    {
        public static List<QuestFlagChange> Compute(QuestFlagSnapshot before, QuestFlagSnapshot after)
        {
            if (before == null) throw new ArgumentNullException(nameof(before));
            if (after == null) throw new ArgumentNullException(nameof(after));

            var changes = new List<QuestFlagChange>();
            foreach (var pair in after.Values)
            {
                if (!before.TryGet(pair.Key, out var oldValue) || oldValue != pair.Value)
                {
                    changes.Add(new QuestFlagChange(pair.Key, pair.Value));
                }
            }

            return changes;
        }
    }

    /// <summary>
    /// Conservative allowlist for PlayerData names that may be replicated after a dialogue.
    /// Quest state in Silksong is spread across NPC/quest/conversation flags; syncing every
    /// PlayerData field would overwrite unrelated progression and is deliberately avoided.
    /// </summary>
    public static class QuestFlagFilter
    {
        public const int MaxKeyLength = 64;

        private static readonly string[] IncludeTerms =
        {
            // Deliberately narrow: only names that explicitly mention a quest. NPC
            // "met"/"convo"/"offered" flags describe flavour dialogue and must not be
            // mirrored, or the partner would lose dialogue they have not heard yet.
            "quest"
        };

        private static readonly string[] ExcludeTerms =
        {
            "pane", "msg", "ui", "seen", "completiondata", "rumourdata", "menu"
        };

        /// <summary>
        /// True when a PlayerData field name is safe to consider for quest synchronization.
        /// </summary>
        public static bool IsQuestRelated(string? name)
        {
            if (string.IsNullOrEmpty(name) || name!.Length > MaxKeyLength) return false;

            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool valid = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                             (c >= '0' && c <= '9') || c == '_';
                if (!valid) return false;
            }

            for (int i = 0; i < ExcludeTerms.Length; i++)
            {
                if (name.IndexOf(ExcludeTerms[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
            }

            for (int i = 0; i < IncludeTerms.Length; i++)
            {
                if (name.IndexOf(IncludeTerms[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }
    }
}
