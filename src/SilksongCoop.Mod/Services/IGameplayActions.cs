using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;

namespace SilksongCoop.Mod.Services
{
    /// <summary>
    /// Actions for applying inventory and item unlocks received from remote peers.
    /// Segregated from revive and death lifecycle actions to adhere to the Interface Segregation Principle.
    /// </summary>
    public interface IInventoryGameplayActions
    {
        bool TryApplyRemoteItemUnlock(string itemId, ItemCategory category);
        void ApplyRemoteItemUnlock(string itemId, ItemCategory category);
    }

    /// <summary>
    /// Actions for reviving at partner and triggering team wipes during death lifecycle.
    /// Segregated from inventory actions to adhere to the Interface Segregation Principle.
    /// </summary>
    public interface IReviveGameplayActions
    {
        void ReviveAtPartner(float x, float y);
        bool TryReviveAtPartner(float x, float y, NetworkPeerId? targetPeer);
        void TriggerTeamWipe();
    }

    /// <summary>
    /// Composite interface preserving backwards compatibility for components
    /// that require both inventory and revive actions.
    /// </summary>
    public interface IGameplayActions : IInventoryGameplayActions, IReviveGameplayActions
    {
    }
}
