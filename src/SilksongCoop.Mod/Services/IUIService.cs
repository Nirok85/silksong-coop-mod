namespace SilksongCoop.Mod.Services
{
    /// <summary>
    /// Manages UI components.
    /// </summary>
    public interface IUIService
    {
        UI.CoopMenuUI? MenuUI { get; }
        void ShowStatusMessage(string message);
        void ShowToast(string message, UI.ToastNotifier.ToastKind kind = UI.ToastNotifier.ToastKind.Info);
    }
}
