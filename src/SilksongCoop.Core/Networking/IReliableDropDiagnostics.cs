namespace SilksongCoop.Core.Networking
{
    /// <summary>
    /// Optional diagnostics implemented by transports that can refuse a reliable send.
    /// The transport interface keeps its <c>void Send</c> signature for compatibility, so
    /// this side channel lets higher layers notice that a reliable packet was dropped by
    /// backpressure and re-send idempotent state once the backlog drains.
    /// </summary>
    public interface IReliableDropDiagnostics
    {
        /// <summary>
        /// Number of reliable packets rejected since the transport was created. Monotonic;
        /// callers should compare it with a previously observed value.
        /// </summary>
        long DroppedReliablePackets { get; }
    }
}
