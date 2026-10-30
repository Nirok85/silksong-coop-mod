using System;
using SilksongCoop.Core.Events;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using SilksongCoop.Mod.State;

namespace SilksongCoop.Mod.Services
{
    /// <summary>
    /// Bridges DeathLifecycleCoordinator with transport and game state.
    /// </summary>
    public class DeathSyncManager : ISyncOrchestrator
    {
        private readonly ISessionService _sessionService;
        private readonly ITransportService _transportService;
        private readonly SilksongCoopPlugin _plugin;
        private readonly DeathLifecycleCoordinator _deathCoordinator;

        public DeathSyncManager(
            ISessionService sessionService,
            ITransportService transportService,
            SilksongCoopPlugin plugin,
            DeathLifecycleCoordinator deathCoordinator)
        {
            _sessionService = sessionService;
            _transportService = transportService;
            _plugin = plugin;
            _deathCoordinator = deathCoordinator;

            deathCoordinator.OnOutgoingDeathPacket += OnOutgoingDeathPacket;
            deathCoordinator.OnReviveAtPartnerRequested += OnReviveAtPartnerRequested;
            deathCoordinator.OnTeamWipeTriggered += OnTeamWipeTriggered;
        }

        public void Initialize() { }
        public void Update() { }
        public void OnSessionStarted(bool isHost) { }
        public void OnSessionEnded() { }

        private void OnOutgoingDeathPacket(DeathLifecyclePacket packet)
        {
            SendPacket(packet, DeliveryMethod.ReliableOrdered, NetworkChannels.Death);
        }

        private void SendPacket(DeathLifecyclePacket packet, DeliveryMethod method, byte channel)
        {
            var transport = _transportService.ActiveTransport;
            var session = _sessionService.SessionState;
            if (transport == null || session?.IsConnected != true || !session.RemotePeerId.HasValue) return;

            try
            {
                byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64);
                try
                {
                    var writer = new PacketWriter(buffer);
                    packet.Serialize(ref writer);
                    transport.Send(session.RemotePeerId.Value, buffer, 0, writer.Position, method, channel);
                    _plugin.MenuUI?.Diagnostics?.RecordSent(writer.Position);
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[DeathSyncManager] Failed sending death packet: {ex.Message}");
            }
        }

        private void OnReviveAtPartnerRequested(float x, float y)
        {
            _plugin.ReviveAtPartner(x, y);
        }

        private void OnTeamWipeTriggered()
        {
            _plugin.TriggerTeamWipe();
        }
    }
}
