namespace SilksongCoop.Core
{
    /// <summary>
    /// Engine-agnostic logger abstraction. The host application (the mod plugin) provides
    /// an implementation that forwards to its native logging system.
    /// </summary>
    public interface ICoopLogger
    {
        void Debug(string message);
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }
}
