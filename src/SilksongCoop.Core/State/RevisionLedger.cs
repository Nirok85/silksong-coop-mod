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
        private readonly Dictionary<ulong, (uint Revision, ulong Origin)> _applied =
            new Dictionary<ulong, (uint Revision, ulong Origin)>();

        /// <summary>Number of objects with a recorded revision (diagnostics/tests).</summary>
        public int Count => _applied.Count;

        /// <summary>
        /// True when the packet describes a state that was already applied or is older than
        /// the last applied revision from the same origin.
        /// </summary>
        public bool IsStale(ulong key, uint revision, ulong origin)
        {
            if (revision == 0) return false;
            if (!_applied.TryGetValue(key, out var last)) return false;
            if (last.Origin != origin) return false;
            return revision <= last.Revision;
        }

        /// <summary>Records an applied revision. Revision 0 carries no ordering.</summary>
        public void Record(ulong key, uint revision, ulong origin)
        {
            if (revision != 0) _applied[key] = (revision, origin);
        }

        public void Clear() => _applied.Clear();
    }
}
