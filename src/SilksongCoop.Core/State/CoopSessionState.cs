using System;
using SilksongCoop.Core.Networking;

namespace SilksongCoop.Core.State
{
    public enum SessionRole
    {
        None,
        Host,
        Client
    }

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
        private readonly object _lock = new object();

        public SessionStatus Status { get; private set; } = SessionStatus.Disconnected;
        public SessionRole Role { get; private set; } = SessionRole.None;
        public NetworkPeerId? LocalPeerId { get; set; }
        public NetworkPeerId? RemotePeerId { get; set; }
        public string? CurrentRoomCode { get; set; }
        public ulong? SteamLobbyId { get; set; }

        public event Action<SessionStatus, SessionStatus>? OnStatusChanged;
        public event Action<NetworkPeerId>? OnPeerJoined;
        public event Action<NetworkPeerId, DisconnectReason>? OnPeerLeft;

        private int _roundTripTimeMs;
        private uint _sentPackets;
        private uint _receivedPackets;
        private ulong _sentBytes;
        private ulong _receivedBytes;
        private uint _currentTick;

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

        public void SetConnected(NetworkPeerId remoteId)
        {
            SessionStatus old;
            lock (_lock)
            {
                old = Status;
                RemotePeerId = remoteId;
                Status = SessionStatus.Connected;
            }
            OnStatusChanged?.Invoke(old, Status);
            OnPeerJoined?.Invoke(remoteId);
        }

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

        public void RecordSent(int byteCount)
        {
            lock (_lock)
            {
                _sentPackets++;
                _sentBytes += (ulong)byteCount;
            }
        }

        public void RecordReceived(int byteCount)
        {
            lock (_lock)
            {
                _receivedPackets++;
                _receivedBytes += (ulong)byteCount;
            }
        }

        public void AdvanceTick()
        {
            lock (_lock)
            {
                _currentTick++;
            }
        }
    }
}
