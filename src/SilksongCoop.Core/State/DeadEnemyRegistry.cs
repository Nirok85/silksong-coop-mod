using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SilksongCoop.Core.State
{
    /// <summary>
    /// A death recorded for an enemy or boss, together with the position used to find the
    /// same object after a scene load.
    /// </summary>
    public readonly struct DeadEnemyRecord
    {
        public DeadEnemyRecord(float x, float y, bool isBoss, bool isSequenceBoss = false)
        {
            X = x;
            Y = y;
            IsBoss = isBoss;
            IsSequenceBoss = isSequenceBoss;
        }

        public float X { get; }
        public float Y { get; }
        public bool IsBoss { get; }

        /// <summary>
        /// A boss that belongs to a repeatable boss sequence and respawns when the sequence
        /// is restarted. It survives ordinary scene changes like a boss, but must be
        /// dropped when the sequence ends or the next attempt starts already dead.
        /// </summary>
        public bool IsSequenceBoss { get; }
    }

    /// <summary>
    /// Bounded registry of enemies the local player has killed.
    /// </summary>
    /// <remarks>
    /// Ordinary enemies respawn when their scene is reloaded, so their death records must be
    /// dropped on a scene change or a late joiner would be told to kill a freshly spawned
    /// enemy. Bosses do not respawn, so their records must survive the scene change and stay
    /// available for catch-up. The registry keeps that distinction in one place and is
    /// engine-agnostic so it can be covered by the Core test suite.
    /// </remarks>
    public sealed class DeadEnemyRegistry
    {
        public const int DefaultCapacity = 256;

        private readonly Dictionary<ulong, DeadEnemyRecord> _records =
            new Dictionary<ulong, DeadEnemyRecord>();
        private readonly Queue<ulong> _order = new Queue<ulong>();
        private readonly int _capacity;
        private readonly object _gate = new object();

        public DeadEnemyRegistry(int capacity = DefaultCapacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            _capacity = capacity;
        }

        /// <summary>Number of currently retained death records.</summary>
        public int Count { get { lock (_gate) return _records.Count; } }

        /// <summary>Stable, read-only snapshot used by catch-up.</summary>
        public IReadOnlyDictionary<ulong, DeadEnemyRecord> Records
        {
            get
            {
                lock (_gate)
                    return new ReadOnlyDictionary<ulong, DeadEnemyRecord>(
                        new Dictionary<ulong, DeadEnemyRecord>(_records));
            }
        }

        /// <summary>Whether a death is already recorded for <paramref name="key"/>.</summary>
        public bool Contains(ulong key) { lock (_gate) return _records.ContainsKey(key); }

        /// <summary>
        /// Records a death. Re-registering the same key updates its position/flags without
        /// growing the eviction queue; the oldest record is evicted at capacity.
        /// </summary>
        public void Register(ulong key, float x, float y, bool isBoss, bool isSequenceBoss = false)
        {
            lock (_gate)
            {
                if (key == 0) return;

                if (!_records.ContainsKey(key))
                {
                    while (_records.Count >= _capacity)
                    {
                        if (!EvictOne()) break;
                    }

                    _order.Enqueue(key);
                }
                else if (_records.TryGetValue(key, out var existing))
                {
                    // Never downgrade a boss/sequence record when the same key is later
                    // reported from a context without that information (for example a
                    // snapshot applied after the boss-sequence lookup went away).
                    isBoss |= existing.IsBoss;
                    isSequenceBoss |= existing.IsSequenceBoss;
                }

                _records[key] = new DeadEnemyRecord(x, y, isBoss, isSequenceBoss);
            }
        }

        /// <summary>
        /// Evicts one record, preferring an ordinary enemy over a boss. Boss records are
        /// needed for late-join catch-up across the scene change that follows the kill, so
        /// they must not be pushed out by a long tail of regular kills.
        /// </summary>
        private bool EvictOne()
        {
            if (_records.Count == 0) return false;

            int count = _order.Count;
            bool foundVictim = false;

            // Walk the FIFO once: remove the oldest non-boss record, re-queue the rest.
            for (int i = 0; i < count; i++)
            {
                ulong key = _order.Dequeue();
                if (!foundVictim &&
                    _records.TryGetValue(key, out var record) &&
                    !record.IsBoss)
                {
                    _records.Remove(key);
                    foundVictim = true;
                    continue;
                }

                _order.Enqueue(key);
            }

            if (foundVictim) return true;

            // Every record is a boss (or the queue is stale): fall back to the oldest.
            while (_order.Count > 0)
            {
                ulong key = _order.Dequeue();
                if (_records.Remove(key)) return true;
            }

            return false;
        }

        /// <summary>
        /// Drops death records for enemies that respawn with their scene, keeping only bosses.
        /// </summary>
        /// <returns>The number of records that were removed.</returns>
        public int DropRespawnable()
        {
            lock (_gate)
            {
                if (_records.Count == 0) return 0;

                var survivors = new List<KeyValuePair<ulong, DeadEnemyRecord>>();
                foreach (var pair in _records)
                {
                    if (pair.Value.IsBoss)
                        survivors.Add(pair);
                }

                int removed = _records.Count - survivors.Count;
                if (removed == 0) return 0;

                _records.Clear();
                _order.Clear();
                for (int i = 0; i < survivors.Count; i++)
                {
                    _records[survivors[i].Key] = survivors[i].Value;
                    _order.Enqueue(survivors[i].Key);
                }

                return removed;
            }
        }

        /// <summary>
        /// Drops death records that belong to a repeatable boss sequence and returns their
        /// keys so the caller can also forget the matching per-object service state. Called
        /// when a sequence ends: the next attempt spawns the same bosses again and must not
        /// inherit the previous attempt's deaths.
        /// </summary>
        public List<ulong> DropSequenceBosses()
        {
            lock (_gate)
            {
                var removed = new List<ulong>();
                if (_records.Count == 0) return removed;

                var survivors = new List<KeyValuePair<ulong, DeadEnemyRecord>>();
                foreach (var pair in _records)
                {
                    if (pair.Value.IsSequenceBoss)
                        removed.Add(pair.Key);
                    else
                        survivors.Add(pair);
                }

                if (removed.Count == 0) return removed;

                _records.Clear();
                _order.Clear();
                for (int i = 0; i < survivors.Count; i++)
                {
                    _records[survivors[i].Key] = survivors[i].Value;
                    _order.Enqueue(survivors[i].Key);
                }

                return removed;
            }
        }

        /// <summary>Removes every death record.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _records.Clear();
                _order.Clear();
            }
        }
    }
}
