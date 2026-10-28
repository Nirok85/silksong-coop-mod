using System;

namespace SilksongCoop.Core
{
    /// <summary>
    /// Minimal logging hook for the engine-agnostic Core assembly. The Unity plugin installs
    /// a sink that forwards to BepInEx, so transport/state errors are visible in
    /// <c>LogOutput.log</c> instead of being written to a debugger that release players do
    /// not have attached.
    /// </summary>
    public static class CoopLog
    {
        /// <summary>Optional sink installed by the host application (the mod plugin).</summary>
        public static Action<string>? Sink { get; set; }

        /// <summary>Writes a warning through the sink, or to the debugger when none is set.</summary>
        public static void Warn(string message)
        {
            var sink = Sink;
            if (sink != null)
            {
                try
                {
                    sink(message);
                    return;
                }
                catch
                {
                    // A broken log sink must never break networking.
                }
            }

            System.Diagnostics.Debug.WriteLine(message);
        }
    }
}
