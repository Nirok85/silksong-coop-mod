namespace SilksongCoop.Core.Networking
{
    /// <summary>
    /// Specifies the reason for a network disconnection.
    /// </summary>
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
