using SilksongCoop.Core.Networking;
using SilksongCoop.Mod.Networking;

namespace SilksongCoop.Mod.Services
{
    /// <summary>
    /// Manages network transports.
    /// </summary>
    public interface ITransportService
    {
        INetworkTransport? ActiveTransport { get; }
        LiteNetLibTransport? UdpTransport { get; }
        SteamP2PTransport? SteamTransport { get; }
        void SetActiveTransport(INetworkTransport transport);
    }
}
