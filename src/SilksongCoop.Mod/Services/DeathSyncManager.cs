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
    public class DeathSyncManager : ISyncOrchestrator, IDisposable
    {
        private readonly ISessionService _sessionService;
        private readonly ITransportService _transportService;
        private readonly IGameplayActions _gameplay;
        private readonly DeathLifecycleCoordinator _deathCoordinator;

        public INetworkDiagnostics? Diagnostics { get; set; }

        public DeathSyncManager(
            ISessionService sessionService,
            ITransportService transportService,
            IGameplayActions gameplay,
            DeathLifecycleCoordinator deathCoordinator)
        {
            _sessionService = sessionService;
            _transportService = transportService;
            _gameplay = gameplay;
            _deathCoordinator = deathCoordinator;

            deathCoordinator.OnOutgoingDeathPacket += OnOutgoingDeathPacket;
            deathCoordinator.OnReviveAtPartnerRequested += OnReviveAtPartnerRequested;
            deathCoordinator.OnTeamWipeTriggered += OnTeamWipeTriggered;
        }

        public void Dispose()
        {
            _deathCoordinator.OnOutgoingDeathPacket -= OnOutgoingDeathPacket;
            _deathCoordinator.OnReviveAtPartnerRequested -= OnReviveAtPartnerRequested;
            _deathCoordinator.OnTeamWipeTriggered -= OnTeamWipeTriggered;
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
            if (transport == null || session?.IsConnected != true) return;

            // A refused send is not retried here (the next lifecycle transition re-broadcasts
            // the corrected state), but it must not be invisible: silent refusals were
            // indistinguishable from successful delivery.
            if (!PacketSender.SendToAll(transport, packet, method, channel, 64, Diagnostics, "[DeathSyncManager]"))
                SilksongCoopPlugin.LogWarn("[DeathSyncManager] Transport refused a death lifecycle packet.");
        }

        private void OnReviveAtPartnerRequested(float x, float y)
        {
            var targetPeer = _deathCoordinator.ReviveTargetPeer;
            bool revived = _gameplay.TryReviveAtPartner(x, y, targetPeer);
            if (!revived && _deathCoordinator.CurrentState == DeathState.RespawningAtPartner)
            {
                // Defensive: the gameplay layer is expected to start its own solo bench
                // fallback. If it ever returns without doing so, never leave the session
                // stuck in RespawningAtPartner without a packet.
                _deathCoordinator.FailReviveAtPartner();
                _deathCoordinator.BeginSoloBenchRespawn();
            }
        }

        private void OnTeamWipeTriggered()
        {
            _gameplay.TriggerTeamWipe();
        }
    }
}
