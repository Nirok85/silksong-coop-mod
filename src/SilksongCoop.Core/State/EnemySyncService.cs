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
        private sealed class Entry
        {
            public bool HasState;
            public int LastHp;
            public bool Dead;
            public uint LastSnapshotTimestamp;
            public Dictionary<ulong, uint>? LastSequenceBySender;
            public bool HostAuthoritative;
            public int LastHostAuthorityTick;
        }

        // A small cap keeps a long session from growing one dictionary per enemy without
        // bound. Real co-op sessions have at most eight peers; the cap only guards against
        // a malformed sender identity.
        private const int MaxSendersPerEnemy = 16;

        // A host snapshot or a host hit proves the host is simulating this enemy. Clients
        // must then wait for the host's authoritative value instead of adding client hits
        // that the snapshot may already contain. The TTL covers the host leaving the room.
        private const int HostAuthorityTtlMs = 3000;

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
        /// Records the locally simulated HP after the local player hit an enemy. This is the
        /// base value used by <see cref="TryAcceptRemoteHit"/> when two players hit the same
        /// enemy almost simultaneously.
        /// </summary>
        public void NoteLocalEnemyState(ulong enemyKey, int hp, bool dead)
        {
            if (enemyKey == 0) return;

            lock (_lock)
            {
                if (!_enemies.TryGetValue(enemyKey, out var entry))
                {
                    EnsureCapacity();
                    _enemyOrder.Enqueue(enemyKey);
                    entry = new Entry();
                }

                if (entry.Dead) return;

                entry.HasState = true;
                entry.LastHp = dead ? 0 : Math.Max(1, hp);
                entry.Dead = dead;
                _enemies[enemyKey] = entry;
            }
        }

        /// <summary>
        /// True when a recent host snapshot/hit proves the host is simulating this enemy.
        /// In that case clients defer to the host instead of applying client hits additively
        /// (the snapshot may already contain those hits, which would double-count them).
        /// </summary>
        public bool HasFreshHostAuthority(ulong enemyKey)
        {
            if (enemyKey == 0) return false;

            lock (_lock)
            {
                if (!_enemies.TryGetValue(enemyKey, out var entry) || !entry.HostAuthoritative)
                    return false;

                return unchecked(Environment.TickCount - entry.LastHostAuthorityTick) <= HostAuthorityTtlMs;
            }
        }

        /// <summary>
        /// Accepts a remote hit and returns the resulting local HP. Unlike
        /// <see cref="TryAcceptHit"/>, damage is additive when the sender's HP is not lower
        /// than the local value: both players hit the same 100 HP enemy for 10 at the same
        /// time, each sends "90", and the receiver must end at 80 rather than at 90.
        /// </summary>
        /// <param name="enemyKey">Stable enemy identifier.</param>
        /// <param name="hp">Enemy HP reported by the sender after its hit.</param>
        /// <param name="dead">Whether the sender observed the enemy as dead.</param>
        /// <param name="damage">Damage dealt by this hit.</param>
        /// <param name="senderId">Sender identity, used for event deduplication.</param>
        /// <param name="sequence">Per-sender packet sequence.</param>
        /// <param name="resultingHp">HP that should be applied locally.</param>
        /// <returns>True when the event is new and should be applied.</returns>
        /// <param name="allowLethal">
        /// False while the host is authoritative for this enemy. The client then clamps
        /// combined damage to 1 HP and waits for the host's death event/snapshot, so a hit
        /// already present in a snapshot cannot be double-counted into a false kill.
        /// </param>
        public bool TryAcceptRemoteHit(
            ulong enemyKey,
            int hp,
            bool dead,
            short damage,
            ulong senderId,
            uint sequence,
            out int resultingHp,
            bool allowLethal = true)
        {
            resultingHp = hp;
            if (enemyKey == 0) return false;

            lock (_lock)
            {
                if (!_enemies.TryGetValue(enemyKey, out var entry))
                {
                    EnsureCapacity();
                    _enemyOrder.Enqueue(enemyKey);
                    entry = new Entry();
                }

                if (entry.Dead) return false;

                if (dead)
                {
                    entry.HasState = true;
                    entry.Dead = true;
                    entry.LastHp = 0;
                    resultingHp = 0;
                    _enemies[enemyKey] = entry;
                    return true;
                }

                // Reliable packets are rare duplicates, but a reconnect/replay can repeat
                // one. Sequence 0 means "unversioned" and skips deduplication.
                if (senderId != 0 && sequence != 0 &&
                    entry.LastSequenceBySender != null &&
                    entry.LastSequenceBySender.TryGetValue(senderId, out uint lastSequence))
                {
                    if (unchecked((int)(sequence - lastSequence)) <= 0)
                        return false;
                }

                if (senderId != 0 && sequence != 0)
                {
                    entry.LastSequenceBySender ??= new Dictionary<ulong, uint>();
                    if (entry.LastSequenceBySender.Count >= MaxSendersPerEnemy &&
                        !entry.LastSequenceBySender.ContainsKey(senderId))
                    {
                        entry.LastSequenceBySender.Clear();
                    }
                    entry.LastSequenceBySender[senderId] = sequence;
                }

                if (!entry.HasState)
                {
                    // First observation: the absolute HP is the only baseline we have.
                    if (hp <= 0)
                    {
                        entry.HasState = true;
                        if (allowLethal)
                        {
                            entry.Dead = true;
                            entry.LastHp = 0;
                            resultingHp = 0;
                        }
                        else
                        {
                            // A non-death packet claiming 0 HP must not bypass host
                            // confirmation. Wait for an explicit Death or the host snapshot.
                            entry.LastHp = 1;
                            resultingHp = 1;
                        }
                        _enemies[enemyKey] = entry;
                        return true;
                    }

                    entry.HasState = true;
                    entry.LastHp = hp;
                    resultingHp = hp;
                    _enemies[enemyKey] = entry;
                    return true;
                }

                if (damage <= 0)
                {
                    if (hp >= entry.LastHp) return false;

                    if (hp <= 0 && !allowLethal)
                    {
                        entry.LastHp = 1;
                        resultingHp = 1;
                        _enemies[enemyKey] = entry;
                        return true;
                    }

                    entry.LastHp = Math.Max(0, hp);
                    if (entry.LastHp == 0)
                    {
                        entry.Dead = true;
                        resultingHp = 0;
                    }
                    else
                    {
                        resultingHp = entry.LastHp;
                    }
                    _enemies[enemyKey] = entry;
                    return true;
                }

                // Both simulations may have applied a different local hit. Combine this
                // event with the local value and also respect a lower absolute HP (the
                // sender may already have seen our hit). This is symmetric:
                //   local 70 + remote 10 (packet hp 90) -> 60
                //   local 90 + remote 30 (packet hp 70) -> 60
                int additiveHp = entry.LastHp - damage;
                int nextHp = Math.Min(hp, additiveHp);
                if (nextHp <= 0)
                {
                    entry.HasState = true;
                    if (allowLethal)
                    {
                        entry.Dead = true;
                        entry.LastHp = 0;
                        resultingHp = 0;
                    }
                    else
                    {
                        // Pending lethal: do not mark Dead. The host will confirm with its
                        // death event/snapshot; a later hit can still finish the job.
                        entry.LastHp = 1;
                        resultingHp = 1;
                    }
                    _enemies[enemyKey] = entry;
                    return true;
                }

                entry.HasState = true;
                entry.LastHp = nextHp;
                resultingHp = nextHp;
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
                entry.HostAuthoritative = true;
                entry.LastHostAuthorityTick = Environment.TickCount;
                entry.Dead = dead;
                entry.LastHp = dead ? 0 : hp;
                _enemies[enemyKey] = entry;
                return true;
            }
        }
    }
}
