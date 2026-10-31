using System;
using System.Collections.Generic;
using SilksongCoop.Core.Networking;

namespace SilksongCoop.Core.State
{
    /// <summary>
    /// Defines the role of a peer in the co-op session.
    /// </summary>
    public enum SessionRole
    {
        None,
        Host,
        Client
    }

    /// <summary>
    /// Defines the current status of the co-op session connection.
    /// </summary>
    public enum SessionStatus
    {
        Disconnected,
        Hosting,
        Connecting,
        Connected,
        Spectating,
        TeamWipe
    }

    /// <summary>
    /// Central session state coordinator tracking connection status, role, peer info, and stats.
    /// </summary>
    public class CoopSessionState
    {
        #region Fields

        private readonly object _lock = new object();

        private SessionStatus _status = SessionStatus.Disconnected;
        private SessionRole _role = SessionRole.None;
        private NetworkPeerId? _localPeerId;
        private NetworkPeerId? _remotePeerId;
        private readonly List<NetworkPeerId> _remotePeerIds = new List<NetworkPeerId>();
        private string? _currentRoomCode;
        private ulong? _steamLobbyId;

        private int _roundTripTimeMs;
        private uint _sentPackets;
        private uint _receivedPackets;
        private ulong _sentBytes;
        private ulong _receivedBytes;
        private uint _currentTick;

        #endregion

        #region Properties

        public SessionStatus Status
        {
            get { lock (_lock) { return _status; } }
            private set { lock (_lock) { _status = value; } }
        }

        public SessionRole Role
        {
            get { lock (_lock) { return _role; } }
            private set { lock (_lock) { _role = value; } }
        }

        public NetworkPeerId? LocalPeerId
        {
            get { lock (_lock) { return _localPeerId; } }
            set { lock (_lock) { _localPeerId = value; } }
        }

        public NetworkPeerId? RemotePeerId
        {
            get { lock (_lock) { return _remotePeerId; } }
            set
            {
                lock (_lock)
                {
                    _remotePeerId = value;
                    _remotePeerIds.Clear();
                    if (value.HasValue) _remotePeerIds.Add(value.Value);
                }
            }
        }

        /// <summary>
        /// All connected remote peers (N-player foundation). For single-peer sessions this
        /// mirrors <see cref="RemotePeerId"/>.
        /// </summary>
        public IReadOnlyList<NetworkPeerId> RemotePeerIds
        {
            get { lock (_lock) { return _remotePeerIds.ToArray(); } }
        }

        public void AddRemotePeer(NetworkPeerId peer)
        {
            lock (_lock)
            {
                if (!_remotePeerIds.Contains(peer)) _remotePeerIds.Add(peer);
                if (!_remotePeerId.HasValue) _remotePeerId = peer;
            }
        }

        public void RemoveRemotePeer(NetworkPeerId peer)
        {
            lock (_lock)
            {
                _remotePeerIds.Remove(peer);
                if (_remotePeerId.HasValue && _remotePeerId.Value == peer)
                    _remotePeerId = _remotePeerIds.Count > 0 ? _remotePeerIds[0] : (NetworkPeerId?)null;
            }
        }

        public string? CurrentRoomCode
        {
            get { lock (_lock) { return _currentRoomCode; } }
            set { lock (_lock) { _currentRoomCode = value; } }
        }

        public ulong? SteamLobbyId
        {
            get { lock (_lock) { return _steamLobbyId; } }
            set { lock (_lock) { _steamLobbyId = value; } }
        }

        public int RoundTripTimeMs
        {
            get { lock (_lock) { return _roundTripTimeMs; } }
            set { lock (_lock) { _roundTripTimeMs = value; } }
        }
        public uint SentPackets
        {
            get { lock (_lock) { return _sentPackets; } }
            set { lock (_lock) { _sentPackets = value; } }
        }
        public uint ReceivedPackets
        {
            get { lock (_lock) { return _receivedPackets; } }
            set { lock (_lock) { _receivedPackets = value; } }
        }
        public ulong SentBytes
        {
            get { lock (_lock) { return _sentBytes; } }
            set { lock (_lock) { _sentBytes = value; } }
        }
        public ulong ReceivedBytes
        {
            get { lock (_lock) { return _receivedBytes; } }
            set { lock (_lock) { _receivedBytes = value; } }
        }
        public uint CurrentTick
        {
            get { lock (_lock) { return _currentTick; } }
            set { lock (_lock) { _currentTick = value; } }
        }

        public bool IsHost
        {
            get { lock (_lock) { return Role == SessionRole.Host; } }
        }
        public bool IsClient
        {
            get { lock (_lock) { return Role == SessionRole.Client; } }
        }
        public bool IsConnected
        {
            get
            {
                lock (_lock)
                {
                    return Status == SessionStatus.Connected || Status == SessionStatus.Spectating || Status == SessionStatus.TeamWipe;
                }
            }
        }

        #endregion

        #region Events

        public event Action<SessionStatus, SessionStatus>? OnStatusChanged;
        public event Action<NetworkPeerId>? OnPeerJoined;
        public event Action<NetworkPeerId, DisconnectReason>? OnPeerLeft;

        #endregion

        #region Public Methods

        /// <summary>
        /// Transitions the session to hosting state with the specified local peer and room code.
        /// </summary>
        /// <param name="localId">The local peer identifier.</param>
        /// <param name="roomCode">The room code for the session.</param>
        public void SetHosting(NetworkPeerId localId, string roomCode)
        {
            SessionStatus old;
            lock (_lock)
            {
                old = Status;
                Role = SessionRole.Host;
                LocalPeerId = localId;
                CurrentRoomCode = roomCode;
                Status = SessionStatus.Hosting;
            }
            OnStatusChanged?.Invoke(old, Status);
        }

        /// <summary>
        /// Transitions the session to connecting state with the specified local peer and target code.
        /// </summary>
        /// <param name="localId">The local peer identifier.</param>
        /// <param name="targetCode">The target room code to connect to.</param>
        public void SetConnecting(NetworkPeerId localId, string targetCode)
        {
            SessionStatus old;
            lock (_lock)
            {
                old = Status;
                Role = SessionRole.Client;
                LocalPeerId = localId;
                CurrentRoomCode = targetCode;
                Status = SessionStatus.Connecting;
            }
            OnStatusChanged?.Invoke(old, Status);
        }

        /// <summary>
        /// Transitions the session to connected state and registers the remote peer.
        /// </summary>
        /// <param name="remoteId">The remote peer identifier.</param>
        /// <param name="role">
        /// Optional role to (re)assert. A host whose peer timed out keeps listening but its
        /// session state is reset to <see cref="SessionRole.None"/>; restoring the role on the
        /// next connection keeps the UI and reconnect logic from treating it as a client.
        /// </param>
        public void SetConnected(NetworkPeerId remoteId, SessionRole role = SessionRole.None)
        {
            SessionStatus old;
            lock (_lock)
            {
                old = Status;
                if (role != SessionRole.None)
                {
                    Role = role;
                }
                RemotePeerId = remoteId;
                Status = SessionStatus.Connected;
            }
            OnStatusChanged?.Invoke(old, Status);
            OnPeerJoined?.Invoke(remoteId);
        }

        /// <summary>
        /// Transitions the session to spectating state (typically after death).
        /// </summary>
        public void SetSpectating()
        {
            SessionStatus old;
            SessionStatus newStatus;
            lock (_lock)
            {
                if (!IsConnected) return;
                old = Status;
                Status = SessionStatus.Spectating;
                newStatus = Status;
            }
            OnStatusChanged?.Invoke(old, newStatus);
        }

        /// <summary>
        /// Transitions the session to team wipe state.
        /// </summary>
        public void SetTeamWipe()
        {
            SessionStatus old;
            SessionStatus newStatus;
            lock (_lock)
            {
                if (!IsConnected) return;
                old = Status;
                Status = SessionStatus.TeamWipe;
                newStatus = Status;
            }
            OnStatusChanged?.Invoke(old, newStatus);
        }

        /// <summary>
        /// Returns the session to active connected state from spectating or team wipe.
        /// </summary>
        public void ReturnToActive()
        {
            SessionStatus old;
            SessionStatus newStatus;
            lock (_lock)
            {
                if (!IsConnected) return;
                old = Status;
                Status = SessionStatus.Connected;
                newStatus = Status;
            }
            OnStatusChanged?.Invoke(old, newStatus);
        }

        /// <summary>
        /// Transitions the session to disconnected state and clears peer information.
        /// </summary>
        /// <param name="reason">The reason for disconnection.</param>
        public void SetDisconnected(DisconnectReason reason = DisconnectReason.GracefulDisconnect)
        {
            SessionStatus old;
            NetworkPeerId? remote;
            lock (_lock)
            {
                old = Status;
                remote = RemotePeerId;

                Status = SessionStatus.Disconnected;
                Role = SessionRole.None;
                RemotePeerId = null;
                CurrentRoomCode = null;
                SteamLobbyId = null;
            }

            OnStatusChanged?.Invoke(old, Status);
            if (remote.HasValue)
            {
                OnPeerLeft?.Invoke(remote.Value, reason);
            }
        }

        /// <summary>
        /// Records a sent packet for statistics tracking.
        /// </summary>
        /// <param name="byteCount">The number of bytes sent.</param>
        public void RecordSent(int byteCount)
        {
            lock (_lock)
            {
                _sentPackets++;
                _sentBytes += (ulong)byteCount;
            }
        }

        /// <summary>
        /// Records a received packet for statistics tracking.
        /// </summary>
        /// <param name="byteCount">The number of bytes received.</param>
        public void RecordReceived(int byteCount)
        {
            lock (_lock)
            {
                _receivedPackets++;
                _receivedBytes += (ulong)byteCount;
            }
        }

        /// <summary>
        /// Advances the current tick counter by one.
        /// </summary>
        public void AdvanceTick()
        {
            lock (_lock)
            {
                _currentTick++;
            }
        }

        #endregion
    }
}
