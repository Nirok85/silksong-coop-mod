namespace SilksongCoop.Core.Networking
{
    public enum DisconnectReason
    {
        None,
        GracefulDisconnect,
        Timeout,
        VersionMismatch,
        LobbyFull,
        ConnectionFailed,
        Kicked
    }
}
