using System;
using System.Collections.Generic;

namespace SilksongCoop.Core.State
{
    /// <summary>
    /// A death recorded for an enemy or boss, together with the position used to find the
    /// same object after a scene load.
    /// </summary>
    public readonly struct DeadEnemyRecord
    {
        public DeadEnemyRecord(float x, float y, bool isBoss)
        {
            X = x;
            Y = y;
            IsBoss = isBoss;
        }

        public float X { get; }
        public float Y { get; }
        public bool IsBoss { get; }
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

        public DeadEnemyRegistry(int capacity = DefaultCapacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            _capacity = capacity;
        }

        /// <summary>Number of currently retained death records.</summary>
        public int Count => _records.Count;

        /// <summary>Live view used by snapshot catch-up.</summary>
        public IReadOnlyDictionary<ulong, DeadEnemyRecord> Records => _records;

        /// <summary>Whether a death is already recorded for <paramref name="key"/>.</summary>
        public bool Contains(ulong key) => _records.ContainsKey(key);

        /// <summary>
        /// Records a death. Re-registering the same key updates its position/flags without
        /// growing the eviction queue; the oldest record is evicted at capacity.
        /// </summary>
        public void Register(ulong key, float x, float y, bool isBoss)
        {
            if (key == 0) return;

            if (!_records.ContainsKey(key))
            {
                while (_records.Count >= _capacity && _order.Count > 0)
                {
                    ulong oldest = _order.Dequeue();
                    if (_records.Remove(oldest)) break;
                }

                _order.Enqueue(key);
            }

            _records[key] = new DeadEnemyRecord(x, y, isBoss);
        }

        /// <summary>
        /// Drops death records for enemies that respawn with their scene, keeping only bosses.
        /// </summary>
        /// <returns>The number of records that were removed.</returns>
        public int DropRespawnable()
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

        /// <summary>Removes every death record.</summary>
        public void Clear()
        {
            _records.Clear();
            _order.Clear();
        }
    }
}
