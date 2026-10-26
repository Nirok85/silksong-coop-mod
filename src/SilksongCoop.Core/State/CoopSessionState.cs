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
            var old = Status;
            Role = SessionRole.Host;
            LocalPeerId = localId;
            CurrentRoomCode = roomCode;
            Status = SessionStatus.Hosting;
            OnStatusChanged?.Invoke(old, Status);
        }

        public void SetConnecting(NetworkPeerId localId, string targetCode)
        {
            var old = Status;
            Role = SessionRole.Client;
            LocalPeerId = localId;
            CurrentRoomCode = targetCode;
            Status = SessionStatus.Connecting;
            OnStatusChanged?.Invoke(old, Status);
        }

        public void SetConnected(NetworkPeerId remoteId)
        {
            var old = Status;
            RemotePeerId = remoteId;
            Status = SessionStatus.Connected;
            OnStatusChanged?.Invoke(old, Status);
            OnPeerJoined?.Invoke(remoteId);
        }

        public void SetSpectating()
        {
            if (!IsConnected) return;
            var old = Status;
            Status = SessionStatus.Spectating;
            OnStatusChanged?.Invoke(old, Status);
        }

        public void SetTeamWipe()
        {
            if (!IsConnected) return;
            var old = Status;
            Status = SessionStatus.TeamWipe;
            OnStatusChanged?.Invoke(old, Status);
        }

        public void ReturnToActive()
        {
            if (!IsConnected) return;
            var old = Status;
            Status = SessionStatus.Connected;
            OnStatusChanged?.Invoke(old, Status);
        }

        public void SetDisconnected(DisconnectReason reason = DisconnectReason.GracefulDisconnect)
        {
            var old = Status;
            var remote = RemotePeerId;

            Status = SessionStatus.Disconnected;
            Role = SessionRole.None;
            RemotePeerId = null;
            CurrentRoomCode = null;
            SteamLobbyId = null;

            OnStatusChanged?.Invoke(old, Status);
            if (remote.HasValue)
            {
                OnPeerLeft?.Invoke(remote.Value, reason);
            }
        }

        public void RecordSent(int byteCount)
        {
            SentPackets++;
            SentBytes += (ulong)byteCount;
        }

        public void RecordReceived(int byteCount)
        {
            ReceivedPackets++;
            ReceivedBytes += (ulong)byteCount;
        }

        public void AdvanceTick()
        {
            CurrentTick++;
        }
    }
}
