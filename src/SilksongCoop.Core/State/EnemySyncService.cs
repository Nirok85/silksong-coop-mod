using System;
using System.Collections.Generic;

namespace SilksongCoop.Core.State
{
    /// <summary>
    /// How a remote hit from another peer relates to the authoritative simulator for that
    /// enemy. The caller decides this from the session role and the freshness of the last
    /// host snapshot; the service then applies the matching merge rule.
    /// </summary>
    public enum RemoteHitPolicy : byte
    {
        /// <summary>
        /// The local player is the host: combine simultaneous damage and resolve lethal
        /// damage locally.
        /// </summary>
        LocalHost = 0,

        /// <summary>
        /// No authoritative peer is simulating this enemy (for example the host is in
        /// another room): combine simultaneous damage and allow client kills.
        /// </summary>
        NoAuthority = 1,

        /// <summary>
        /// The packet comes from the authoritative peer: its absolute HP is authoritative,
        /// so it replaces the local value instead of being subtracted from it. Without this
        /// rule a hit already contained in a snapshot would be counted twice.
        /// </summary>
        Authoritative = 2,

        /// <summary>
        /// Another peer sent the packet while an authoritative peer is actively simulating
        /// this enemy. The packet is ignored: the host snapshot already contains every hit
        /// it received, and applying the hit again would double-count it or let a client
        /// declare a death the host has not confirmed.
        /// </summary>
        Deferred = 3
    }

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

            // Monotonic order of the last authoritative snapshot vs. the last local hit.
            // An authoritative hit is combined additively only when the local player hit
            // after that snapshot: then the snapshot cannot already contain this hit.
            public int LastSnapshotOrder;
            public int LastLocalHitOrder;
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
        private int _stateOrder;

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
        /// Forgets one enemy's tracked state. Used when a repeatable boss sequence ends:
        /// the same hierarchy key is reused by the next attempt and must start alive.
        /// </summary>
        public bool Forget(ulong enemyKey)
        {
            lock (_lock)
            {
                return _enemies.Remove(enemyKey);
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
                ulong keyToRemove = 0;
                foreach (var key in _enemies.Keys)
                {
                    keyToRemove = key;
                    break;
                }

                if (keyToRemove != 0)
                    _enemies.Remove(keyToRemove);
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
                entry.LastLocalHitOrder = ++_stateOrder;
                _enemies[enemyKey] = entry;
            }
        }

        /// <summary>
        /// Seeds the host's current authoritative HP for an enemy before a client hit is
        /// merged. Unlike <see cref="NoteLocalEnemyState"/> this is not a hit: it must not
        /// advance the local-hit ordering or turn a client's packet into a local event.
        /// </summary>
        public void NoteCurrentEnemyState(ulong enemyKey, int hp, bool dead)
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
        /// False while an authoritative peer is simulating this enemy. The client then
        /// clamps combined damage to 1 HP and waits for the host's death event/snapshot, so
        /// a hit already present in a snapshot cannot be double-counted into a false kill.
        /// </param>
        /// <remarks>
        /// This legacy overload keeps the pre-policy merge rules for callers and tests that
        /// predate <see cref="RemoteHitPolicy"/>. New code should use the policy overload.
        /// </remarks>
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
            return AcceptRemoteHitCore(
                enemyKey, hp, dead, damage, senderId, sequence,
                additive: true, allowLethal: allowLethal, confirmRemoteDeaths: false,
                out resultingHp);
        }

        /// <summary>
        /// Accepts a remote hit according to the caller's authority decision. Unlike the
        /// legacy overload, a packet from the authoritative peer replaces HP with its
        /// absolute value (the snapshot may already contain the same hit), and packets from
        /// other peers are ignored while that authority is fresh.
        /// </summary>
        public bool TryAcceptRemoteHit(
            ulong enemyKey,
            int hp,
            bool dead,
            short damage,
            ulong senderId,
            uint sequence,
            RemoteHitPolicy policy,
            out int resultingHp)
        {
            if (policy == RemoteHitPolicy.Deferred)
            {
                resultingHp = 0;
                return false;
            }

            // The local host never lets a client declare a kill on its own. The packet is
            // merged against the host's current HP and only combined lethal damage confirms
            // the death; an unconfirmed claim is clamped and waits for the host snapshot.
            return AcceptRemoteHitCore(
                enemyKey, hp, dead, damage, senderId, sequence,
                additive: policy != RemoteHitPolicy.Authoritative,
                allowLethal: true,
                confirmRemoteDeaths: policy == RemoteHitPolicy.LocalHost,
                out resultingHp);
        }

        private bool AcceptRemoteHitCore(
            ulong enemyKey,
            int hp,
            bool dead,
            short damage,
            ulong senderId,
            uint sequence,
            bool additive,
            bool allowLethal,
            bool confirmRemoteDeaths,
            out int resultingHp)
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

                // Sequence deduplication runs before the death branch: a stale death packet
                // must not override a newer hit, and a duplicate death from the same sender
                // must not be re-applied after the enemy was reset.
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

                if (dead && confirmRemoteDeaths)
                {
                    // LocalHost: a client's Death event is a damage claim, not proof that
                    // the host's enemy died. Merge the killing hit into the host's own HP
                    // and only accept the death when combined damage is actually lethal.
                    if (!entry.HasState)
                    {
                        // No host baseline yet: keep the enemy alive and let the next
                        // host snapshot provide the authoritative value.
                        entry.HasState = true;
                        entry.Dead = false;
                        entry.LastHp = 1;
                        resultingHp = 1;
                        _enemies[enemyKey] = entry;
                        return true;
                    }

                    int combinedHp = entry.LastHp - Math.Max(0, (int)damage);
                    entry.HasState = true;
                    if (combinedHp <= 0)
                    {
                        entry.Dead = true;
                        entry.LastHp = 0;
                        resultingHp = 0;
                    }
                    else
                    {
                        entry.LastHp = Math.Max(1, combinedHp);
                        resultingHp = entry.LastHp;
                    }
                    _enemies[enemyKey] = entry;
                    return true;
                }

                if (dead)
                {
                    entry.HasState = true;
                    entry.Dead = true;
                    entry.LastHp = 0;
                    resultingHp = 0;
                    _enemies[enemyKey] = entry;
                    return true;
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

                // An authoritative packet is absolute unless the local player hit after the
                // last applied snapshot AND the host's HP is still at or above ours: only
                // then the host cannot have incorporated the local hit yet. If the host HP
                // is already lower, it supersedes the local hit and must replace our value;
                // subtracting again would double-count the same damage.
                bool localHitSinceSnapshot = entry.LastLocalHitOrder > entry.LastSnapshotOrder;
                bool combineLocalHit = !additive &&
                                       localHitSinceSnapshot &&
                                       damage > 0 &&
                                       hp >= entry.LastHp;
                if (!additive && !combineLocalHit)
                {
                    // The authoritative peer's absolute HP replaces the local value. An
                    // equal or higher value is a duplicate or a stale packet: while that
                    // peer simulates the enemy, HP only goes down.
                    if (hp >= entry.LastHp) return false;

                    entry.LastHp = Math.Max(0, hp);
                    resultingHp = entry.LastHp;
                    if (entry.LastHp == 0) entry.Dead = true;
                    _enemies[enemyKey] = entry;
                    return true;
                }

                if (damage <= 0)
                {
                    // A host merges client hits through its own simulation; a packet with
                    // no damage carries no authoritative host value to apply.
                    if (confirmRemoteDeaths) return false;

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
                // LocalHost ignores the client's absolute HP entirely: only the host's
                // own HP and the claimed damage decide the result.
                int additiveHp = entry.LastHp - damage;
                int nextHp = confirmRemoteDeaths ? additiveHp : Math.Min(hp, additiveHp);
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

                // A snapshot generated before the local player's last hit cannot contain
                // that hit. Applying its higher HP would visually resurrect the enemy until
                // the host receives the hit; never raise HP in that window. Snapshots may
                // still lower HP (the host saw other damage) or confirm a death.
                bool localHitSinceSnapshot = entry.LastLocalHitOrder > entry.LastSnapshotOrder;
                int previousHp = entry.HasState ? entry.LastHp : int.MaxValue;
                int acceptedHp = dead ? 0 : hp;
                if (!dead && localHitSinceSnapshot)
                    acceptedHp = Math.Min(entry.LastHp, Math.Max(0, hp));

                entry.HasState = true;
                entry.LastSnapshotTimestamp = timestamp;
                entry.HostAuthoritative = true;
                entry.LastHostAuthorityTick = Environment.TickCount;
                entry.LastSnapshotOrder = ++_stateOrder;
                entry.Dead = dead;
                entry.LastHp = acceptedHp;
                _enemies[enemyKey] = entry;
                return dead || !localHitSinceSnapshot || acceptedHp < previousHp;
            }
        }
    }
}
