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
        /// <summary>Severity levels used by <see cref="CoopLog"/>.</summary>
        public enum Level
        {
            Debug = 0,
            Info = 1,
            Warn = 2,
            Error = 3
        }

        /// <summary>Optional sink installed by the host application (the mod plugin).</summary>
        public static Action<string>? Sink { get; set; }

        /// <summary>
        /// Optional level-aware sink. When set it takes precedence over <see cref="Sink"/>
        /// and receives the severity together with the message.
        /// </summary>
        public static Action<Level, string>? LevelSink { get; set; }

        /// <summary>
        /// Optional structured logger. When set it takes precedence over the sinks and
        /// receives typed calls per severity.
        /// </summary>
        public static ICoopLogger? Logger { get; set; }

        private static void Write(Level level, string message)
        {
            var logger = Logger;
            if (logger != null)
            {
                try
                {
                    switch (level)
                    {
                        case Level.Debug: logger.Debug(message); break;
                        case Level.Info: logger.Info(message); break;
                        case Level.Warn: logger.Warn(message); break;
                        case Level.Error: logger.Error(message); break;
                    }
                    return;
                }
                catch
                {
                    // A broken log sink must never break networking.
                }
            }

            var levelSink = LevelSink;
            if (levelSink != null)
            {
                try
                {
                    levelSink(level, message);
                    return;
                }
                catch
                {
                    // A broken log sink must never break networking.
                }
            }

            var sink = Sink;
            if (sink != null)
            {
                try
                {
                    sink($"[{level}] {message}");
                    return;
                }
                catch
                {
                    // A broken log sink must never break networking.
                }
            }

            System.Diagnostics.Debug.WriteLine($"[{level}] {message}");
        }

        public static void Debug(string message) => Write(Level.Debug, message);
        public static void Info(string message) => Write(Level.Info, message);
        public static void Warn(string message) => Write(Level.Warn, message);
        public static void Error(string message) => Write(Level.Error, message);
    }
}
