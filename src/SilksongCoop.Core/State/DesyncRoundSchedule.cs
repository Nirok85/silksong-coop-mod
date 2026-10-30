using System;

namespace SilksongCoop.Core.State
{
    /// <summary>
    /// Derives the desync sampling round from wall-clock time so both peers label the same
    /// instant with the same round number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The previous implementation let each peer count its own five-second timer. Frame-rate
    /// differences, stalls and suspension made the counters drift apart; once they diverged,
    /// equal-numbered rounds no longer described the same moment and comparisons either
    /// produced false positives or stopped matching entirely.
    /// </para>
    /// <para>
    /// The round is <c>floor(unixTimeMs / intervalMs)</c>. Both peers therefore compute the
    /// same label in the same wall-clock bucket, and a missed interval on one side does not
    /// shift every later round. Each peer samples on its first frame after the bucket
    /// boundary, so a stalled peer can still sample a little later; a legitimate state change
    /// between those two instants may be reported once and is bounded by the report cooldown.
    /// This assumes the two machines have reasonably synchronised system clocks (NTP).
    /// </para>
    /// </remarks>
    public static class DesyncRoundSchedule
    {
        /// <summary>Smallest supported sampling interval.</summary>
        public const int MinIntervalMs = 1000;

        /// <summary>
        /// Computes the shared round number for an absolute Unix timestamp.
        /// </summary>
        /// <param name="unixTimeMs">Current UTC time in milliseconds since the Unix epoch.</param>
        /// <param name="intervalMs">Sampling interval; values below <see cref="MinIntervalMs"/> are clamped.</param>
        public static uint ComputeRound(long unixTimeMs, int intervalMs)
        {
            long interval = Math.Max(MinIntervalMs, intervalMs);

            // Integer division truncates toward zero; for pre-1970 timestamps that would
            // round the wrong way, so use floor semantics explicitly.
            long round = unixTimeMs >= 0
                ? unixTimeMs / interval
                : -((-unixTimeMs + interval - 1) / interval);

            return unchecked((uint)round);
        }
    }
}
