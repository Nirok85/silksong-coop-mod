using System.Collections.Generic;

namespace SilksongCoop.Core.State
{
    /// <summary>
    /// Tracks the last applied state revision for every world object and origin. Revisions
    /// are only comparable within one origin (a packet may be relayed from a peer whose
    /// local counter is unrelated), and revision 0 means "unversioned". Keeping the rule in
    /// Core lets the Unity controller and tests share exactly one implementation.
    /// </summary>
    public sealed class RevisionLedger
    {
        // One watermark per (object, origin). A single origin slot per object was not
        // enough: applying A/5, then B/1, then a delayed A/4 made A/4 look fresh again
        // because the newer origin overwrote A's watermark.
        private readonly Dictionary<(ulong Key, ulong Origin), uint> _applied =
            new Dictionary<(ulong Key, ulong Origin), uint>();

        private readonly HashSet<ulong> _keys = new HashSet<ulong>();

        /// <summary>Number of objects with a recorded revision (diagnostics/tests).</summary>
        public int Count => _keys.Count;

        /// <summary>
        /// True when the packet describes a state that was already applied or is older than
        /// the last applied revision from the same origin.
        /// </summary>
        public bool IsStale(ulong key, uint revision, ulong origin)
        {
            if (revision == 0) return false;
            if (!_applied.TryGetValue((key, origin), out uint last)) return false;
            return revision <= last;
        }

        /// <summary>Records an applied revision. Revision 0 carries no ordering.</summary>
        public void Record(ulong key, uint revision, ulong origin)
        {
            if (revision == 0) return;

            if (!_applied.TryGetValue((key, origin), out uint last) || revision > last)
            {
                _applied[(key, origin)] = revision;
                _keys.Add(key);
            }
        }

        public void Clear()
        {
            _applied.Clear();
            _keys.Clear();
        }
    }
}
