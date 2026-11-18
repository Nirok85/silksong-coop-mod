using System;
using SilksongCoop.Mod.State;
using SilksongCoop.Core.Events;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;

namespace SilksongCoop.Mod.Services
{
    /// <summary>
    /// Bridges InventorySyncService with transport, replacing inline callbacks in the plugin.
    /// </summary>
    public class InventorySyncManager : ISyncOrchestrator, IDisposable
    {
        private readonly ISessionService _sessionService;
        private readonly ITransportService _transportService;
        private readonly IInventoryGameplayActions _gameplay;
        private readonly IInventorySyncService _inventorySync;

        public INetworkDiagnostics? Diagnostics { get; set; }

        public InventorySyncManager(ISessionService sessionService, ITransportService transportService, IInventoryGameplayActions gameplay, IInventorySyncService inventorySync)
        {
            _sessionService = sessionService;
            _transportService = transportService;
            _gameplay = gameplay;
            _inventorySync = inventorySync;

            _inventorySync.OnOutgoingPeerItemUnlockPacket += OnOutgoingPeerItemUnlock;

            // Fires for unlocks received from the partner and must be applied locally.
            if (_inventorySync is InventorySyncService concrete) concrete.RemoteItemApplier = _gameplay.TryApplyRemoteItemUnlock;
            else _inventorySync.OnRemoteItemUnlocked += OnRemoteItemUnlocked;
        }

        public void Dispose()
        {
            _inventorySync.OnOutgoingPeerItemUnlockPacket -= OnOutgoingPeerItemUnlock;
            if (_inventorySync is InventorySyncService concrete) concrete.RemoteItemApplier = null;
            _inventorySync.OnRemoteItemUnlocked -= OnRemoteItemUnlocked;
        }

        private void OnRemoteItemUnlocked(string itemId, ItemCategory category)
            => _gameplay.ApplyRemoteItemUnlock(itemId, category);

        public void Initialize() { }
        public void Update() { }
        public void OnSessionStarted(bool isHost) { }
        public void OnSessionEnded() { }

        private void OnOutgoingPeerItemUnlock(NetworkPeerId? targetPeer, ItemUnlockPacket packet)
        {
            if (RemoteApplyScope.IsActive) return;
            var transport = _transportService.ActiveTransport;
            var session = _sessionService.SessionState;
            if (transport == null || session?.IsConnected != true) return;

            if (targetPeer.HasValue)
            {
                if (!PacketSender.SendTo(
                    transport, targetPeer.Value, packet, DeliveryMethod.ReliableOrdered, NetworkChannels.Currency,
                    512, Diagnostics, "[InventorySyncManager]"))
                    SilksongCoopPlugin.LogWarn($"[InventorySyncManager] Transport refused item unlock to peer {targetPeer.Value}.");
            }
            else
            {
                if (!PacketSender.SendToAll(
                    transport, packet, DeliveryMethod.ReliableOrdered, NetworkChannels.Currency,
                    512, Diagnostics, "[InventorySyncManager]"))
                    SilksongCoopPlugin.LogWarn("[InventorySyncManager] Transport refused an item unlock broadcast.");
            }
        }
    }
}
