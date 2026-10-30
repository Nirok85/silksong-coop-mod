using System;
using System.Diagnostics;

namespace SilksongCoop.Core.Networking
{
    /// <summary>
    /// Monotonic millisecond clock for packet timestamps that need a wall-clock-independent
    /// source. TransformSync deliberately uses Unity game time instead (the interpolation
    /// buffer is evaluated against <c>Time.timeAsDouble</c>).
    /// </summary>
    /// <remarks>
    /// <see cref="Environment.TickCount"/> is a signed 32-bit counter tied to system uptime.
    /// Its signed value turns negative after ~24.9 days, so arithmetic and casts around it
    /// are easy to get wrong, and the value changes meaning when the machine reboots.
    /// <see cref="Stopwatch"/> is monotonic and unaffected by system-clock adjustments. The
    /// value is truncated to 32 bits because the wire format stores a <see cref="uint"/>;
    /// consumers compare samples with unchecked subtraction, so the wrap is harmless as long
    /// as two samples are less than 2^31 ms (~24.8 days) apart.
    /// </remarks>
    public static class NetworkClock
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        /// <summary>
        /// Milliseconds since the clock was first used, truncated to 32 bits for the wire format.
        /// </summary>
        public static uint NowMs => unchecked((uint)Clock.ElapsedMilliseconds);
    }
}
