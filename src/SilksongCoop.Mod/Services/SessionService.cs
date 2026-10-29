using System;
using SilksongCoop.Core.Events;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.State;
using SilksongCoop.Mod.Networking;
using UnityEngine;

namespace SilksongCoop.Mod.Services
{
    public class SessionService : ISessionService
    {
        private readonly IEventBus _eventBus;
        private readonly ITransportService _transportService;
        private readonly SilksongCoopPlugin _plugin;

        public CoopSessionState SessionState { get; }

        public SessionService(IEventBus eventBus, ITransportService transportService, SilksongCoopPlugin plugin)
        {
            _eventBus = eventBus;
            _transportService = transportService;
            _plugin = plugin;
            SessionState = new CoopSessionState();
        }

        public void StartHost(int port)
        {
            var udp = _transportService.UdpTransport;
            if (udp == null) return;
            _transportService.SetActiveTransport(udp);
            PrepareNewSession();
            udp.StartHost(port);
            SessionState.SetHosting(udp.LocalPeerId, $"UDP:{port}");
            _eventBus.Publish(new SessionStartedEvent { IsHost = true, TransportType = "UDP" });
        }

        public void StartHostSteam()
        {
            var steam = _transportService.SteamTransport;
            if (steam == null) return;
            _transportService.SetActiveTransport(steam);
            PrepareNewSession();
            steam.StartHost();
            SessionState.SetHosting(steam.LocalPeerId, steam.CurrentLobbyId.m_SteamID.ToString());
            _eventBus.Publish(new SessionStartedEvent { IsHost = true, TransportType = "Steam" });
        }

        public void Connect(string address)
        {
            var udp = _transportService.UdpTransport;
            if (udp == null) return;
            _transportService.SetActiveTransport(udp);
            PrepareNewSession();
            udp.Connect(address);
            SessionState.SetConnecting(udp.LocalPeerId, address);
        }

        public void ConnectSteam(string lobbyId)
        {
            var steam = _transportService.SteamTransport;
            if (steam == null) return;
            _transportService.SetActiveTransport(steam);
            PrepareNewSession();
            steam.Connect(lobbyId);
            SessionState.SetConnecting(steam.LocalPeerId, lobbyId);
        }

        public void Disconnect(DisconnectReason reason = DisconnectReason.GracefulDisconnect)
        {
            try
            {
                var transport = _transportService.ActiveTransport;
                if (transport != null)
                {
                    var method = transport.GetType().GetMethod("Disconnect", new[] { typeof(DisconnectReason) });
                    if (method != null)
                        method.Invoke(transport, new object[] { reason });
                    else
                        transport.Disconnect();
                }
            }
            catch { }
            SessionState.SetDisconnected(reason);
            _eventBus.Publish(new SessionEndedEvent { Reason = reason });
        }

        public void PrepareNewSession()
        {
            _plugin.CloneSpawner?.ResetAllClones();
            _plugin.InventorySync?.ResetSessionState();
            _plugin.EnemySync?.ResetSession();
            SessionState?.SetDisconnected(DisconnectReason.GracefulDisconnect);
        }
    }
}
