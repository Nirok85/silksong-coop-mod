namespace SilksongCoop.Core.Networking
{
    /// <summary>
    /// Shared backpressure thresholds for the reliable send path. The values describe how
    /// many reliable-ordered packets may be waiting for acknowledgement before senders must
    /// stop adding traffic or fall back to a resync.
    /// </summary>
    public static class NetworkLimits
    {
        /// <summary>
        /// Transport-wide cap: once this many reliable packets are unacknowledged, new
        /// reliable packets are dropped instead of breaking stream continuity.
        /// </summary>
        public const int ReliableBacklogCap = 512;

        /// <summary>
        /// Senders stop queueing snapshot traffic at this backlog so a snapshot batch can be
        /// retried instead of being silently rejected by the transport.
        /// </summary>
        public const int SnapshotBacklogThreshold = 448;

        /// <summary>
        /// World-state replay pauses while the backlog is above this threshold.
        /// </summary>
        public const int ReplayBacklogThreshold = 192;

        /// <summary>
        /// Deferred resyncs wait until the backlog drains to this threshold.
        /// </summary>
        public const int ResyncBacklogThreshold = 128;
    }
}
