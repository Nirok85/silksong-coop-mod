using System;
using SilksongCoop.Core.Networking;
using SilksongCoop.Mod.Networking;
using UnityEngine;

namespace SilksongCoop.Mod.Services
{
    public class TransportService : ITransportService
    {
        public INetworkTransport? ActiveTransport { get; private set; }
        public LiteNetLibTransport? UdpTransport { get; private set; }
        public SteamP2PTransport? SteamTransport { get; private set; }

        public TransportService(LiteNetLibTransport udpTransport, SteamP2PTransport? steamTransport)
        {
            UdpTransport = udpTransport;
            SteamTransport = steamTransport;
            ActiveTransport = udpTransport;
        }

        public void SetActiveTransport(INetworkTransport transport)
        {
            if (transport == null) return;
            if (ReferenceEquals(ActiveTransport, transport)) return;

            if (ActiveTransport != null)
            {
                try { ActiveTransport.Disconnect(); }
                catch { }
            }

            ActiveTransport = transport;
        }
    }
}
