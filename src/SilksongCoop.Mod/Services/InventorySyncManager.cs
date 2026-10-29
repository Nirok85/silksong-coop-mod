using System;
using SilksongCoop.Core.Events;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using UnityEngine;

namespace SilksongCoop.Mod.Services
{
    /// <summary>
    /// Bridges InventorySyncService with transport, replacing inline callbacks in the plugin.
    /// </summary>
    public class InventorySyncManager : ISyncOrchestrator
    {
        private readonly ISessionService _sessionService;
        private readonly ITransportService _transportService;
        private readonly SilksongCoopPlugin _plugin;
        private readonly IInventorySyncService _inventorySync;

        public InventorySyncManager(ISessionService sessionService, ITransportService transportService, SilksongCoopPlugin plugin, IInventorySyncService inventorySync)
        {
            _sessionService = sessionService;
            _transportService = transportService;
            _plugin = plugin;
            _inventorySync = inventorySync;

            _inventorySync.OnOutgoingItemUnlockPacket += OnOutgoingItemUnlock;

            // OnRemoteItemUnlocked is only exposed by the concrete service: it fires for
            // unlocks received from the partner and must be applied locally.
            if (_inventorySync is Core.State.InventorySyncService concrete)
            {
                concrete.OnRemoteItemUnlocked += (itemId, category) => _plugin.ApplyRemoteItemUnlock(itemId, category);
            }
        }

        public void Initialize() { }
        public void Update() { }
        public void OnSessionStarted(bool isHost) { }
        public void OnSessionEnded() { }

        private void OnOutgoingItemUnlock(ItemUnlockPacket packet)
        {
            var transport = _transportService.ActiveTransport;
            var session = _sessionService.SessionState;
            if (transport == null || session?.IsConnected != true || !session.RemotePeerId.HasValue) return;

            try
            {
                byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(512);
                try
                {
                    var writer = new PacketWriter(buffer);
                    packet.Serialize(ref writer);
                    transport.Send(session.RemotePeerId.Value, buffer, 0, writer.Position, DeliveryMethod.ReliableOrdered, NetworkChannels.Currency);
                    _plugin.MenuUI?.Diagnostics?.RecordSent(writer.Position);
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[InventorySyncManager] Failed sending item unlock: {ex.Message}");
            }
        }
    }
}
