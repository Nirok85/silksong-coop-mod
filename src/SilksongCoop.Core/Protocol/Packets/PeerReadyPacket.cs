namespace SilksongCoop.Core.Protocol.Packets
{
    /// <summary>Confirms that the receiver has loaded the save and can apply a catch-up.</summary>
    public struct PeerReadyPacket : IPacket
    {
        public PacketHeader Header;

        public System.Guid SaveInstance;

        public void Serialize(ref PacketWriter writer)
        {
            writer.WriteHeader(Header);
            writer.WriteGuid(SaveInstance);
        }
    }
}
