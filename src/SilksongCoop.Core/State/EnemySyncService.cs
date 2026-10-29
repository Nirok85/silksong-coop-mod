using System;
using System.Collections.Generic;

namespace SilksongCoop.Core.State
{
    /// <summary>
    /// Engine-agnostic bookkeeping for replicated enemy HP. The Unity layer finds the
    /// <c>HealthManager</c> and applies the actual HP/death; this service decides whether an
    /// incoming packet is new, so delayed duplicates cannot double-damage or resurrect an
    /// enemy.
    /// </summary>
    public class EnemySyncService
    {
        private struct Entry
        {
            public bool HasState;
            public int LastHp;
            public bool Dead;
            public uint LastSnapshotTimestamp;
        }

        private const int MaxTrackedEnemies = 1024;

        private readonly Dictionary<ulong, Entry> _enemies = new Dictionary<ulong, Entry>();
        private readonly Queue<ulong> _enemyOrder = new Queue<ulong>();
        private readonly object _lock = new object();

        /// <summary>
        /// Number of enemies currently tracked (diagnostics/tests).
        /// </summary>
        public int TrackedCount
        {
            get { lock (_lock) { return _enemies.Count; } }
        }

        /// <summary>
        /// Clears all per-scene state. Call when the local scene changes.
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _enemies.Clear();
                _enemyOrder.Clear();
            }
        }

        /// <summary>
        /// Evicts the oldest tracked enemy instead of clearing every entry, which would
        /// forget that earlier enemies are already dead.
        /// </summary>
        private void EnsureCapacity()
        {
            while (_enemies.Count >= MaxTrackedEnemies && _enemyOrder.Count > 0)
            {
                ulong oldest = _enemyOrder.Dequeue();
                if (_enemies.Remove(oldest)) return;
            }

            if (_enemies.Count >= MaxTrackedEnemies && _enemies.Count > 0)
            {
                foreach (var key in _enemies.Keys)
                {
                    _enemies.Remove(key);
                    return;
                }
            }
        }

        /// <summary>
        /// Accepts a hit packet. HP is expected to be monotonically non-increasing; a packet
        /// that would raise HP, or a second death event, is rejected.
        /// </summary>
        /// <param name="enemyKey">Stable enemy identifier.</param>
        /// <param name="hp">HP after the sender's hit.</param>
        /// <param name="dead">Whether the sender observed the enemy as dead.</param>
        /// <returns>True when the packet should be applied locally.</returns>
        public bool TryAcceptHit(ulong enemyKey, int hp, bool dead)
        {
            if (enemyKey == 0) return false;

            lock (_lock)
            {
                if (!_enemies.TryGetValue(enemyKey, out var entry))
                {
                    EnsureCapacity();
                    _enemyOrder.Enqueue(enemyKey);
                    entry = new Entry { HasState = false };
                }

                if (entry.Dead) return false;

                if (dead)
                {
                    entry.HasState = true;
                    entry.Dead = true;
                    entry.LastHp = 0;
                    _enemies[enemyKey] = entry;
                    return true;
                }

                if (entry.HasState && hp >= entry.LastHp)
                {
                    return false;
                }

                entry.HasState = true;
                entry.LastHp = hp;
                _enemies[enemyKey] = entry;
                return true;
            }
        }

        /// <summary>
        /// Accepts an authoritative host snapshot entry. Older snapshots are rejected; a dead
        /// enemy is never resurrected by a stale snapshot.
        /// </summary>
        public bool TryAcceptSnapshot(ulong enemyKey, int hp, bool dead, uint timestamp)
        {
            if (enemyKey == 0) return false;

            lock (_lock)
            {
                if (!_enemies.TryGetValue(enemyKey, out var entry))
                {
                    EnsureCapacity();
                    _enemyOrder.Enqueue(enemyKey);
                    entry = new Entry { HasState = false };
                }

                if (entry.Dead && !dead) return false;

                if (entry.LastSnapshotTimestamp != 0)
                {
                    int diff = unchecked((int)(timestamp - entry.LastSnapshotTimestamp));
                    if (diff <= 0) return false;
                }

                entry.HasState = true;
                entry.LastSnapshotTimestamp = timestamp;
                entry.Dead = dead;
                entry.LastHp = dead ? 0 : hp;
                _enemies[enemyKey] = entry;
                return true;
            }
        }
    }
}
