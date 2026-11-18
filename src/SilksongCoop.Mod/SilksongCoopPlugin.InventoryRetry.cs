using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;

namespace SilksongCoop.Mod
{
    public partial class SilksongCoopPlugin
    {
        private void ProcessRemoteUnlock(NetworkPeerId peer, ItemUnlockPacket packet)
            => InventoryCoordinator.ProcessRemoteUnlock(peer, packet);

        private void ClearInventoryRetries() => InventoryCoordinator.ClearInventoryRetries();
    }
}
