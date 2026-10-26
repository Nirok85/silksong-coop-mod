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

        public int RoundTripTimeMs { get; set; }
        public uint SentPackets { get; set; }
        public uint ReceivedPackets { get; set; }
        public ulong SentBytes { get; set; }
        public ulong ReceivedBytes { get; set; }
        public uint CurrentTick { get; set; }

        public event Action<SessionStatus, SessionStatus>? OnStatusChanged;
        public event Action<NetworkPeerId>? OnPeerJoined;
        public event Action<NetworkPeerId, DisconnectReason>? OnPeerLeft;

        public bool IsHost => Role == SessionRole.Host;
        public bool IsClient => Role == SessionRole.Client;
        public bool IsConnected => Status == SessionStatus.Connected || Status == SessionStatus.Spectating || Status == SessionStatus.TeamWipe;

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
            bool connected;
            lock (_lock)
            {
                connected = IsConnected;
                if (!connected) return;
                old = Status;
                Status = SessionStatus.Spectating;
            }
            OnStatusChanged?.Invoke(old, Status);
        }

        public void SetTeamWipe()
        {
            SessionStatus old;
            bool connected;
            lock (_lock)
            {
                connected = IsConnected;
                if (!connected) return;
                old = Status;
                Status = SessionStatus.TeamWipe;
            }
            OnStatusChanged?.Invoke(old, Status);
        }

        public void ReturnToActive()
        {
            SessionStatus old;
            bool connected;
            lock (_lock)
            {
                connected = IsConnected;
                if (!connected) return;
                old = Status;
                Status = SessionStatus.Connected;
            }
            OnStatusChanged?.Invoke(old, Status);
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
                SentPackets++;
                SentBytes += (ulong)byteCount;
            }
        }

        public void RecordReceived(int byteCount)
        {
            lock (_lock)
            {
                ReceivedPackets++;
                ReceivedBytes += (ulong)byteCount;
            }
        }

        public void AdvanceTick()
        {
            lock (_lock)
            {
                CurrentTick++;
            }
        }
    }
}
