namespace SilksongCoop.Core.Networking
{
    /// <summary>
    /// Single source of truth for the wire protocol revision and its domain-separation label.
    /// </summary>
    /// <remarks>
    /// Every transport must enforce this value: UDP carries it in the handshake, and Steam
    /// prefixes it to each game datagram because Steam lobbies have no built-in protocol
    /// negotiation. Bump <see cref="Version"/> together with <see cref="SessionLabel"/>
    /// whenever packet layouts change incompatibly.
    /// </remarks>
    public static class NetworkProtocol
    {
        /// <summary>Current wire protocol revision.</summary>
        public const ushort Version = 8;

        /// <summary>Label mixed into the HMAC session key for this revision.</summary>
        public const string SessionLabel = "SilksongCoop-Session-v8";
    }
}
