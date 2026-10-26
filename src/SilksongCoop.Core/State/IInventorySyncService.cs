using System;
using System.Collections.Generic;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;

namespace SilksongCoop.Core.State
{
    public interface IInventorySyncService
    {
        IReadOnlyCollection<string> UnlockedItems { get; }
        string LocalEquippedCrest { get; set; }
        IReadOnlyList<string> LocalEquippedTools { get; }
        bool IsEquippedLoadoutIsolated { get; }

        event Action<string, ItemCategory>? OnItemUnlocked;
        event Action<ItemUnlockPacket>? OnOutgoingItemUnlockPacket;

        void OnLocalItemDiscovered(string itemId, ItemCategory category, Guid? transactionId = null);
        void OnLocalItemDiscovered(int itemNumericId, ItemCategory category, Guid? transactionId = null);
        void HandleItemUnlockPacket(NetworkPeerId sender, ItemUnlockPacket packet);
        void SetLocalEquippedCrest(string crestId);
        void SetLocalEquippedTools(IEnumerable<string> toolIds);
        bool IsItemUnlocked(string itemId);
        bool IsTransactionProcessed(Guid transactionId);
    }
}
