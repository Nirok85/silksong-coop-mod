using System;
using System.Collections.Generic;

namespace SilksongCoop.Core.State
{
    /// <summary>
    /// Bounded evidence for enemy hits received while the enemy is unloaded. World-channel
    /// enemy events are reliable ordered, so a per-sender serial watermark rejects duplicates
    /// even after an individual enemy proof expires or its encounter is forgotten.
    /// </summary>
    public sealed class DeferredEnemyDamageLedger
    {
        public const int MaxEnemies = 512;
        public const int MaxSenders = 64;
        public const uint ProofLifetimeMs = 60000;

        private sealed class SenderProof
        {
            public uint Sequence;
            public int Hp;
            public int Damage;
        }

        private sealed class EnemyProof
        {
            public uint LastReceivedMs;
            public readonly Dictionary<ulong, SenderProof> Senders = new Dictionary<ulong, SenderProof>();
        }

        private readonly Dictionary<ulong, EnemyProof> _enemies = new Dictionary<ulong, EnemyProof>();
        private readonly Dictionary<ulong, uint> _senderSequences = new Dictionary<ulong, uint>();

        public int EnemyCount => _enemies.Count;
        public int SenderCount => _senderSequences.Count;

        /// <summary>
        /// Advances the sender's ordered event watermark. Zero is an ordinary serial value,
        /// including the first event and the value after uint wrap.
        /// </summary>
        public bool TryObserve(ulong sender, uint sequence)
        {
            if (sender == 0) return false;
            if (_senderSequences.TryGetValue(sender, out uint previous))
            {
                if (unchecked((int)(sequence - previous)) <= 0) return false;
            }
            else if (_senderSequences.Count >= MaxSenders)
            {
                return false;
            }

            _senderSequences[sender] = sequence;
            return true;
        }

        /// <summary>
        /// Records one new event and sums unique positive damage for its current encounter.
        /// An observed HP increase starts fresh evidence rather than combining attempts.
        /// </summary>
        public bool TryRecord(ulong enemy, ulong sender, uint sequence, int hp, int damage,
            uint receivedMs, out int totalDamage)
        {
            totalDamage = 0;
            if (enemy == 0 || damage < 0 || hp < 0) return false;
            Expire(receivedMs);
            if (!_enemies.TryGetValue(enemy, out var proof) && _enemies.Count >= MaxEnemies)
                return false; // Fail closed instead of evicting proof and reopening replay.
            if (!TryObserve(sender, sequence)) return false;

            if (proof == null)
            {
                proof = new EnemyProof();
                _enemies.Add(enemy, proof);
            }
            else if (proof.Senders.TryGetValue(sender, out var previous) && hp > previous.Hp)
            {
                proof.Senders.Clear();
            }

            if (!proof.Senders.TryGetValue(sender, out var sample))
                proof.Senders.Add(sender, sample = new SenderProof());
            sample.Sequence = sequence;
            sample.Hp = hp;
            sample.Damage = (int)Math.Min((long)sample.Damage + damage, int.MaxValue);
            proof.LastReceivedMs = receivedMs;
            totalDamage = Sum(proof);
            return true;
        }

        public int GetDamage(ulong enemy, uint receivedMs)
        {
            if (!_enemies.TryGetValue(enemy, out var proof)) return 0;
            if (unchecked(receivedMs - proof.LastReceivedMs) >= ProofLifetimeMs)
            {
                _enemies.Remove(enemy);
                return 0;
            }
            return Sum(proof);
        }

        public void Forget(ulong enemy) => _enemies.Remove(enemy);

        /// <summary>Discards encounter evidence but preserves replay watermarks.</summary>
        public void ClearEncounters() => _enemies.Clear();

        /// <summary>Only a new transport session may restart sender serials.</summary>
        public void Reset()
        {
            _enemies.Clear();
            _senderSequences.Clear();
        }

        private void Expire(uint receivedMs)
        {
            List<ulong>? expired = null;
            foreach (var pair in _enemies)
                if (unchecked(receivedMs - pair.Value.LastReceivedMs) >= ProofLifetimeMs)
                    (expired ??= new List<ulong>()).Add(pair.Key);
            if (expired != null)
                foreach (ulong enemy in expired) _enemies.Remove(enemy);
        }

        private static int Sum(EnemyProof proof)
        {
            long total = 0;
            foreach (var sample in proof.Senders.Values) total += sample.Damage;
            return (int)Math.Min(total, int.MaxValue);
        }
    }
}
