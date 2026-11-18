using System;
using System.Collections.Generic;
using SilksongCoop.Core.Networking;

namespace SilksongCoop.Mod.UI
{
    /// <summary>
    /// Persisted list of recent connection targets (IP:port, room codes or Steam lobby IDs) for quick re-join.
    /// Stores up to 5 entries, deduplicating as items are added. Uses PlayerPrefs for persistence.
    /// </summary>
    public class ConnectionHistory
    {
        public const int MaxEntries = 5;
        /// <summary>Maximum accepted target length. Room codes, IP:port and Steam IDs are
        /// far below this; anything longer is a corrupt/prefilled registry value.</summary>
        public const int MaxTargetLength = 128;
        private const string PrefsKey = "SilksongCoop.RecentConnections";
        private const char Separator = '|';

        private readonly List<string> _entries = new List<string>(MaxEntries);
        private bool _loaded;

        /// <summary>
        /// When false, entries are kept in memory only and nothing is written to PlayerPrefs
        /// (the Windows registry). Controlled by the [Network] SaveConnectionHistory config.
        /// </summary>
        private bool _persistEnabled;

        public bool PersistEnabled
        {
            get => _persistEnabled;
            set
            {
                _persistEnabled = value;
                if (!value)
                    DeletePersisted();
            }
        }

        /// <summary>
        /// Returns the list of recent connection targets, most recent first.
        /// </summary>
        public IReadOnlyList<string> GetAll()
        {
            EnsureLoaded();
            return _entries;
        }

        /// <summary>
        /// Add a connection target to the history. Deduplicates (moves to front if already present).
        /// Persists to PlayerPrefs.
        /// </summary>
        public void Add(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return;

            target = target.Trim();
            if (target.Length > MaxTargetLength) return;

            EnsureLoaded();

            // Remove duplicate if present
            _entries.Remove(target);

            // Insert at front
            _entries.Insert(0, target);

            // Trim to max
            while (_entries.Count > MaxEntries)
            {
                _entries.RemoveAt(_entries.Count - 1);
            }

            Save();
        }

        /// <summary>
        /// Clear all history entries and persist.
        /// </summary>
        public void Clear()
        {
            _entries.Clear();
            DeletePersisted();
        }

        private static void DeletePersisted()
        {
            // Always delete the persisted key, regardless of the current toggle, so the
            // on-disk state matches the in-memory state.
            try
            {
                UnityEngine.PlayerPrefs.DeleteKey(PrefsKey);
                UnityEngine.PlayerPrefs.Save();
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[ConnectionHistory] Failed to delete: {ex.Message}");
            }
        }

        /// <summary>
        /// Parses a raw separator-delimited string into history entries, capping to MaxEntries,
        /// deduplicating, and bounding split operations to avoid unbounded processing on corrupt data.
        /// </summary>
        public static List<string> ParseRawHistory(string raw, int maxEntries = MaxEntries, int maxTargetLength = MaxTargetLength)
        {
            var result = new List<string>(maxEntries);
            if (string.IsNullOrEmpty(raw)) return result;

            // Bound splitting to prevent unbounded string allocations if raw data is corrupt
            var parts = raw.Split(new[] { Separator }, maxEntries * 4, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                if (result.Count >= maxEntries)
                    break;

                var trimmed = p.Trim();
                if (!string.IsNullOrEmpty(trimmed) &&
                    trimmed.Length <= maxTargetLength &&
                    !result.Contains(trimmed) &&
                    !RoomCodeCodec.TryDecode(trimmed, out _, out _))
                {
                    result.Add(trimmed);
                    if (result.Count >= maxEntries)
                        break;
                }
            }
            return result;
        }

        private void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            if (!PersistEnabled) return;

            try
            {
                string raw = UnityEngine.PlayerPrefs.GetString(PrefsKey, "");
                if (string.IsNullOrEmpty(raw)) return;

                var parsed = ParseRawHistory(raw, MaxEntries, MaxTargetLength);
                _entries.Clear();
                _entries.AddRange(parsed);
                Save();
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[ConnectionHistory] Failed to load: {ex.Message}");
            }
        }

        private void Save()
        {
            if (!PersistEnabled) return;

            try
            {
                var persistable = _entries.FindAll(target => !RoomCodeCodec.TryDecode(target, out _, out _));
                string raw = string.Join(Separator.ToString(), persistable);
                if (raw.Length == 0)
                    UnityEngine.PlayerPrefs.DeleteKey(PrefsKey);
                else
                    UnityEngine.PlayerPrefs.SetString(PrefsKey, raw);
                UnityEngine.PlayerPrefs.Save();
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[ConnectionHistory] Failed to save: {ex.Message}");
            }
        }
    }
}
