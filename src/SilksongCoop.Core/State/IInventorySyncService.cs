using System;
using System.Collections.Generic;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;

namespace SilksongCoop.Core.State
{
    /// <summary>
    /// Synchronizes inventory unlocks and equipped loadouts between co-op peers.
    /// </summary>
    public interface IInventorySyncService
    {
        IReadOnlyCollection<string> UnlockedItems { get; }
        string LocalEquippedCrest { get; set; }
        IReadOnlyList<string> LocalEquippedTools { get; }
        bool IsEquippedLoadoutIsolated { get; }

        event Action<string, ItemCategory>? OnItemUnlocked;
        event Action<string, ItemCategory>? OnRemoteItemUnlocked;
        event Action<ItemUnlockPacket>? OnOutgoingItemUnlockPacket;
        event Action<NetworkPeerId?, ItemUnlockPacket>? OnOutgoingPeerItemUnlockPacket;

        void OnLocalItemDiscovered(string itemId, ItemCategory category, Guid? transactionId = null, bool force = false);
        void OnLocalItemDiscovered(int itemNumericId, ItemCategory category, Guid? transactionId = null, bool force = false);
        void HandleItemUnlockPacket(NetworkPeerId sender, ItemUnlockPacket packet);
        void SetLocalEquippedCrest(string crestId);
        void SetLocalEquippedTools(IEnumerable<string> toolIds);
        bool IsItemUnlocked(string itemId);
        bool IsTransactionProcessed(Guid transactionId);

        void ResetPeerState(NetworkPeerId peer);
        bool HasPeerReceivedItem(NetworkPeerId peer, string itemId);
        void MarkItemKnownByPeer(NetworkPeerId peer, string itemId);
        IReadOnlyCollection<string> GetKnownItemsForPeer(NetworkPeerId peer);

        void BroadcastInitialInventory(NetworkPeerId targetPeer);
        void BroadcastInitialInventory(NetworkPeerId? targetPeer = null, int maxBatchSize = 20);
        IReadOnlyList<IReadOnlyList<string>> CreateInitialInventoryBatches(int maxBatchSize = 20);
    }
}
