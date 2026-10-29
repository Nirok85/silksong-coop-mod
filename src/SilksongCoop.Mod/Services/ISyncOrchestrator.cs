namespace SilksongCoop.Mod.Services
{
    /// <summary>
    /// Orchestrates synchronization between services.
    /// </summary>
    public interface ISyncOrchestrator
    {
        void Initialize();
        void Update();
        void OnSessionStarted(bool isHost);
        void OnSessionEnded();
    }
}
