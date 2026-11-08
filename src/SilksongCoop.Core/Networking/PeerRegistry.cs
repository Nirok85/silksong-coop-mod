using System;
using System.Collections.Generic;

namespace SilksongCoop.Core.Networking
{
    /// <summary>
    /// Lifecycle of a remote peer. A peer moves Connecting → Authenticated → Ready as the
    /// handshake and initial state catch-up complete. Single-peer transports only use Ready
    /// today; the extra states are the foundation for 3+ player sessions.
    /// </summary>
    public enum PeerState : byte
    {
        Connecting = 0,
        Authenticated = 1,
        Ready = 2
    }

    /// <summary>
    /// Immutable snapshot of one peer's registry entry.
    /// </summary>
    public readonly struct PeerRecord
    {
        public NetworkPeerId Peer { get; }
        public PeerState State { get; }
        public string Role { get; }
        public DateTimeOffset ConnectedAtUtc { get; }
        public DateTimeOffset LastSeenUtc { get; }

        public PeerRecord(
            NetworkPeerId peer,
            PeerState state,
            string? role,
            DateTimeOffset connectedAtUtc,
            DateTimeOffset lastSeenUtc)
        {
            Peer = peer;
            State = state;
            Role = role ?? string.Empty;
            ConnectedAtUtc = connectedAtUtc;
            LastSeenUtc = lastSeenUtc;
        }

        public PeerRecord WithState(PeerState state, DateTimeOffset lastSeenUtc)
            => new PeerRecord(Peer, state, Role, ConnectedAtUtc, lastSeenUtc);
    }

    /// <summary>
    /// Thread-safe registry of remote peers and their lifecycle state. Transports and the
    /// session layer can add/remove peers independently of the legacy single
    /// <c>RemotePeerId</c> property, which keeps the current 2-player path working while
    /// 3+ player code migrates to the registry.
    /// </summary>
    public sealed class PeerRegistry
    {
        private readonly object _lock = new object();
        private readonly Dictionary<NetworkPeerId, PeerRecord> _peers =
            new Dictionary<NetworkPeerId, PeerRecord>();

        // Cached read-only views returned by Peers/ReadyPeers. They are rebuilt under
        // _lock on every mutation so steady-state reads allocate nothing. The views wrap
        // private copies so callers cannot mutate the live registry through a cast.
        private IReadOnlyList<PeerRecord> _peersSnapshot = Array.Empty<PeerRecord>();
        private IReadOnlyList<NetworkPeerId> _readyPeersSnapshot = Array.Empty<NetworkPeerId>();

        public event Action<PeerRecord>? OnPeerAdded;
        public event Action<PeerRecord, PeerRecord>? OnPeerUpdated;
        public event Action<PeerRecord>? OnPeerRemoved;

        public int Count
        {
            get { lock (_lock) { return _peers.Count; } }
        }

        /// <summary>Snapshot of every registered peer, safe to enumerate off-lock.</summary>
        public IReadOnlyList<PeerRecord> Peers
        {
            get
            {
                lock (_lock)
                {
                    return _peersSnapshot;
                }
            }
        }

        /// <summary>Snapshot of peers that finished authentication and catch-up.</summary>
        public IReadOnlyList<NetworkPeerId> ReadyPeers
        {
            get
            {
                lock (_lock)
                {
                    return _readyPeersSnapshot;
                }
            }
        }

        /// <summary>
        /// Rebuilds both cached snapshots. Must be called under <see cref="_lock"/> after
        /// <see cref="_peers"/> was mutated.
        /// </summary>
        private void RebuildSnapshots()
        {
            if (_peers.Count == 0)
            {
                _peersSnapshot = Array.Empty<PeerRecord>();
                _readyPeersSnapshot = Array.Empty<NetworkPeerId>();
                return;
            }

            var peers = new PeerRecord[_peers.Count];
            int readyCount = 0;
            foreach (var pair in _peers)
            {
                if (pair.Value.State == PeerState.Ready) readyCount++;
            }

            var ready = readyCount == 0 ? Array.Empty<NetworkPeerId>() : new NetworkPeerId[readyCount];
            int peerIndex = 0;
            int readyIndex = 0;
            foreach (var pair in _peers)
            {
                peers[peerIndex++] = pair.Value;
                // Use the record's peer, not the dictionary key: a same-Value upsert can
                // refresh the display name while the dictionary keeps the original key.
                if (pair.Value.State == PeerState.Ready) ready[readyIndex++] = pair.Value.Peer;
            }

            _peersSnapshot = Array.AsReadOnly(peers);
            _readyPeersSnapshot = ready.Length == 0
                ? Array.Empty<NetworkPeerId>()
                : Array.AsReadOnly(ready);
        }

        public bool TryGet(NetworkPeerId peer, out PeerRecord record)
        {
            lock (_lock)
            {
                return _peers.TryGetValue(peer, out record);
            }
        }

        public int CountInState(PeerState state)
        {
            lock (_lock)
            {
                int count = 0;
                foreach (var pair in _peers)
                {
                    if (pair.Value.State == state) count++;
                }
                return count;
            }
        }

        /// <summary>
        /// Adds a peer or updates its state/role. The original <see cref="PeerRecord.ConnectedAtUtc"/>
        /// is preserved across updates so callers can measure session age.
        /// </summary>
        public PeerRecord Upsert(NetworkPeerId peer, PeerState state, string? role = null)
        {
            PeerRecord result;
            PeerRecord previous = default;
            bool added = false;

            lock (_lock)
            {
                var now = DateTimeOffset.UtcNow;
                if (_peers.TryGetValue(peer, out previous))
                {
                    bool nameChanged = !string.Equals(
                        previous.Peer.DisplayName,
                        peer.DisplayName,
                        StringComparison.Ordinal);
                    if (nameChanged)
                    {
                        result = new PeerRecord(
                            peer,
                            state,
                            string.IsNullOrEmpty(role) ? previous.Role : role,
                            previous.ConnectedAtUtc,
                            now);
                        // NetworkPeerId equality ignores DisplayName, so replace the
                        // dictionary key explicitly to keep Peers/ReadyPeers consistent.
                        _peers.Remove(previous.Peer);
                    }
                    else
                    {
                        result = previous.WithState(state, now);
                        if (!string.IsNullOrEmpty(role)) result = new PeerRecord(
                            peer, state, role, previous.ConnectedAtUtc, now);
                    }
                }
                else
                {
                    result = new PeerRecord(peer, state, role, now, now);
                    added = true;
                }

                _peers[result.Peer] = result;
                RebuildSnapshots();
            }

            if (added)
            {
                OnPeerAdded?.Invoke(result);
            }
            else
            {
                OnPeerUpdated?.Invoke(previous, result);
            }

            return result;
        }

        /// <summary>Updates an existing peer's state. Returns false for an unknown peer.</summary>
        public bool SetState(NetworkPeerId peer, PeerState state)
        {
            PeerRecord previous;
            PeerRecord updated;

            lock (_lock)
            {
                if (!_peers.TryGetValue(peer, out previous)) return false;
                var now = DateTimeOffset.UtcNow;
                bool nameChanged = !string.Equals(
                    previous.Peer.DisplayName,
                    peer.DisplayName,
                    StringComparison.Ordinal);
                updated = nameChanged
                    ? new PeerRecord(peer, state, previous.Role, previous.ConnectedAtUtc, now)
                    : previous.WithState(state, now);

                if (nameChanged) _peers.Remove(previous.Peer);
                _peers[updated.Peer] = updated;
                RebuildSnapshots();
            }

            OnPeerUpdated?.Invoke(previous, updated);
            return true;
        }

        public bool Remove(NetworkPeerId peer)
        {
            PeerRecord removed;

            lock (_lock)
            {
                if (!_peers.TryGetValue(peer, out removed)) return false;
                _peers.Remove(peer);
                RebuildSnapshots();
            }

            OnPeerRemoved?.Invoke(removed);
            return true;
        }

        public void Clear()
        {
            List<PeerRecord> removed;

            lock (_lock)
            {
                if (_peers.Count == 0) return;
                removed = new List<PeerRecord>(_peers.Values);
                _peers.Clear();
                RebuildSnapshots();
            }

            for (int i = 0; i < removed.Count; i++)
            {
                OnPeerRemoved?.Invoke(removed[i]);
            }
        }
    }
}
