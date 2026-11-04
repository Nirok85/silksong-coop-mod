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
                    return new List<PeerRecord>(_peers.Values);
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
                    var ready = new List<NetworkPeerId>();
                    foreach (var pair in _peers)
                    {
                        if (pair.Value.State == PeerState.Ready) ready.Add(pair.Key);
                    }
                    return ready;
                }
            }
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
                    result = previous.WithState(state, now);
                    if (!string.IsNullOrEmpty(role)) result = new PeerRecord(
                        peer, state, role, previous.ConnectedAtUtc, now);
                }
                else
                {
                    result = new PeerRecord(peer, state, role, now, now);
                    added = true;
                }

                _peers[peer] = result;
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
                updated = previous.WithState(state, DateTimeOffset.UtcNow);
                _peers[peer] = updated;
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
            }

            for (int i = 0; i < removed.Count; i++)
            {
                OnPeerRemoved?.Invoke(removed[i]);
            }
        }
    }
}
