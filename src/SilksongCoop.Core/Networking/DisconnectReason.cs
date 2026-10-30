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
        ConnectionFailed,
        /// <summary>The remote peer closed the session (UDP disconnect frame or Steam lobby leave).</summary>
        PeerDisconnected
    }
}
