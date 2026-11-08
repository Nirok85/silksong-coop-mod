namespace SilksongCoop.Core.Protocol.Packets
{
    /// <summary>
    /// A packet that can serialize itself into a <see cref="PacketWriter"/>.
    /// Implemented by wire packets so send paths can share generic serialization and
    /// buffer management without reflection or per-type dispatch.
    /// </summary>
    public interface IPacket
    {
        /// <summary>
        /// Serializes this packet to the specified writer.
        /// </summary>
        /// <param name="writer">The packet writer to serialize to.</param>
        void Serialize(ref PacketWriter writer);
    }
}
