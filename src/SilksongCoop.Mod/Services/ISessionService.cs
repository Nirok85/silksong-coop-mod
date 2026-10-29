using SilksongCoop.Core.Events;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.State;

namespace SilksongCoop.Mod.Services
{
    /// <summary>
    /// Manages session lifecycle and state.
    /// </summary>
    public interface ISessionService
    {
        CoopSessionState SessionState { get; }
        void StartHost(int port);
        void StartHostSteam();
        void Connect(string address);
        void ConnectSteam(string lobbyId);
        void Disconnect(DisconnectReason reason = DisconnectReason.GracefulDisconnect);
        void PrepareNewSession();
    }

    public class SessionStartedEvent : GameEvent
    {
        public bool IsHost { get; set; }
        public string TransportType { get; set; } = string.Empty;
    }

    public class SessionEndedEvent : GameEvent
    {
        public DisconnectReason Reason { get; set; }
    }

    public class PeerConnectedEvent : GameEvent
    {
        public NetworkPeerId PeerId { get; set; }
    }

    public class PeerDisconnectedEvent : GameEvent
    {
        public NetworkPeerId PeerId { get; set; }
        public DisconnectReason Reason { get; set; }
    }
}
