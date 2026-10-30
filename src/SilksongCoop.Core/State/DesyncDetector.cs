using System;
using System.Collections.Generic;
using SilksongCoop.Core.Events;

namespace SilksongCoop.Core.State
{
    /// <summary>
    /// Detects desynchronization between peers by comparing state checksums.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both peers sample the shared state once per <see cref="CheckIntervalSeconds"/> using a
    /// round number derived from wall-clock time (see <see cref="DesyncRoundSchedule"/>), so
    /// equal rounds describe the same instant. Checksums are only compared within a round;
    /// comparing a fresh local sample against a remote one taken seconds earlier reported a
    /// desync on every legitimate state change.
    /// </para>
    /// <para>
    /// A small history of local samples is retained because unreliable checksum frames can
    /// arrive after the local peer has already sampled the next round. Keeping only the latest
    /// sample silently skipped those comparisons.
    /// </para>
    /// </remarks>
    public class DesyncDetector
    {
        public class DesyncDetectedEvent : GameEvent
        {
            public string LocalChecksum { get; set; } = string.Empty;
            public string RemoteChecksum { get; set; } = string.Empty;
            public string Context { get; set; } = string.Empty;
        }

        private const int MaxRetainedRounds = 8;

        private readonly IEventBus _eventBus;
        private readonly Dictionary<uint, uint> _localSamples = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, uint> _pendingRemote = new Dictionary<uint, uint>();
        private readonly List<uint> _pruneScratch = new List<uint>(MaxRetainedRounds);

        private float _lastReportTime = float.NegativeInfinity;
        private uint _newestLocalRound;
        private bool _hasLocalSample;

        public float CheckIntervalSeconds { get; set; } = 5f;
        public float ReportCooldownSeconds { get; set; } = 10f;
        public bool Enabled { get; set; } = true;

        public DesyncDetector(IEventBus eventBus)
        {
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        }

        /// <summary>
        /// Stores the local sample for <paramref name="round"/> and compares it with a
        /// remote sample for the same round that arrived early, if any.
        /// </summary>
        public void Update(float now, uint localChecksum, uint round)
        {
            if (!Enabled) return;

            if (!_hasLocalSample)
            {
                _newestLocalRound = round;
                _hasLocalSample = true;
            }
            else if (IsNewerRound(round, _newestLocalRound))
            {
                _newestLocalRound = round;
            }
            else if (unchecked((int)(round - _newestLocalRound)) <= -MaxRetainedRounds)
            {
                // The wall clock stepped backwards by more than the retention window (manual
                // clock change, VM resume). Samples from the old timeline can never be
                // compared with the new round numbers, so start a fresh history instead of
                // pruning every new sample until the clock catches up.
                _localSamples.Clear();
                _pendingRemote.Clear();
                _newestLocalRound = round;
            }

            _localSamples[round] = localChecksum;
            Prune();

            if (_pendingRemote.TryGetValue(round, out uint earlyRemote))
            {
                _pendingRemote.Remove(round);
                ReportIfMismatch(localChecksum, earlyRemote, round, now, "state");
            }
        }

        /// <summary>
        /// Handles a remote sample. Samples that belong to a round the local peer has not
        /// reached yet are buffered and compared by the next <see cref="Update"/>; samples
        /// for an older round are compared against the retained local history.
        /// </summary>
        /// <returns>True when a desync event was published.</returns>
        public bool OnRemoteChecksumReceived(uint remoteChecksum, uint round, float now, string context = "state")
        {
            if (!Enabled) return false;

            if (_localSamples.TryGetValue(round, out uint localChecksum))
                return ReportIfMismatch(localChecksum, remoteChecksum, round, now, context);

            _pendingRemote[round] = remoteChecksum;
            Prune();
            return false;
        }

        private static bool IsNewerRound(uint candidate, uint reference)
        {
            return unchecked((int)(candidate - reference)) > 0;
        }

        private bool ReportIfMismatch(uint localChecksum, uint remoteChecksum, uint round, float now, string context)
        {
            if (remoteChecksum == localChecksum) return false;
            if (now - _lastReportTime < ReportCooldownSeconds) return false;

            _lastReportTime = now;
            _eventBus.Publish(new DesyncDetectedEvent
            {
                LocalChecksum = localChecksum.ToString("X8"),
                RemoteChecksum = remoteChecksum.ToString("X8"),
                Context = string.IsNullOrEmpty(context) ? $"round {round}" : $"{context} (round {round})"
            });
            return true;
        }

        private void Prune()
        {
            if (!_hasLocalSample)
            {
                // Without a local timeline there is no modular reference point, but the
                // pending map must still stay bounded while detection is paused (partner in
                // another scene or clone not spawned yet).
                if (_pendingRemote.Count > MaxRetainedRounds)
                    _pendingRemote.Clear();
                return;
            }

            PruneMap(_localSamples);
            PruneMap(_pendingRemote);
        }

        private void PruneMap(Dictionary<uint, uint> samples)
        {
            if (samples.Count == 0) return;

            // Drop rounds that fell out of the retention window. Keep
            // MaxRetainedRounds entries including the current one.
            _pruneScratch.Clear();
            foreach (uint round in samples.Keys)
            {
                if (unchecked((int)(round - _newestLocalRound)) <= -MaxRetainedRounds)
                    _pruneScratch.Add(round);
            }
            for (int i = 0; i < _pruneScratch.Count; i++)
                samples.Remove(_pruneScratch[i]);

            // If a clock jump produced a burst of future samples, keep the ones closest to
            // the local round: those are the first that will be compared.
            while (samples.Count > MaxRetainedRounds)
            {
                uint farthestRound = 0;
                int farthestDelta = int.MinValue;
                bool found = false;

                foreach (uint round in samples.Keys)
                {
                    int delta = unchecked((int)(round - _newestLocalRound));
                    if (delta > farthestDelta)
                    {
                        farthestDelta = delta;
                        farthestRound = round;
                        found = true;
                    }
                }

                if (!found) break;
                samples.Remove(farthestRound);
            }
        }

        /// <summary>
        /// Computes a stable FNV-1a checksum over a caller-supplied state signature.
        /// The signature must contain only state both peers are expected to share
        /// (scene, progression, shared currency), never per-player positions or health.
        /// </summary>
        public uint ComputeChecksumValue(string stateSignature)
        {
            unchecked
            {
                uint hash = 2166136261;
                string signature = stateSignature ?? string.Empty;

                for (int i = 0; i < signature.Length; i++)
                {
                    char c = signature[i];
                    hash ^= (byte)c;
                    hash *= 16777619;
                    hash ^= (byte)(c >> 8);
                    hash *= 16777619;
                }

                return hash;
            }
        }

        public void Reset()
        {
            _lastReportTime = float.NegativeInfinity;
            _newestLocalRound = 0;
            _hasLocalSample = false;
            _localSamples.Clear();
            _pendingRemote.Clear();
            _pruneScratch.Clear();
        }
    }
}
