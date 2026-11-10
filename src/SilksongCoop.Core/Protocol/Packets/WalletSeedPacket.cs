namespace SilksongCoop.Core.Protocol.Packets
{
    public struct WalletSeedPacket : IPacket
    {
        public PacketHeader Header;
        public int Rosaries;
        public int Shards;

        public void Serialize(ref PacketWriter writer)
        {
            writer.WriteHeader(Header);
            writer.WriteInt32(Rosaries);
            writer.WriteInt32(Shards);
        }
    }
}
