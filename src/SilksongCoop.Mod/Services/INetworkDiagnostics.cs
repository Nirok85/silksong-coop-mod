namespace SilksongCoop.Mod.Services
{
    /// <summary>
    /// Narrow interface over <see cref="UI.NetworkDiagnostics"/> so transport/orchestrator
    /// code does not depend on the concrete diagnostics UI.
    /// </summary>
    public interface INetworkDiagnostics
    {
        void RecordPing(float pingMs);
        void RecordLossPercent(float percent);
        void RecordSent(int byteCount);
    }
}
