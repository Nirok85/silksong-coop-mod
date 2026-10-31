namespace SilksongCoop.Mod.Services
{
    /// <summary>
    /// Narrow interface over gameplay actions exposed by the plugin, so sync managers
    /// do not depend on the concrete <see cref="SilksongCoopPlugin"/> god object.
    /// </summary>
    public interface IGameplayActions
    {
        void ApplyRemoteItemUnlock(string itemId, SilksongCoop.Core.Protocol.Packets.ItemCategory category);
        void ReviveAtPartner(float x, float y);
        void TriggerTeamWipe();
    }
}
