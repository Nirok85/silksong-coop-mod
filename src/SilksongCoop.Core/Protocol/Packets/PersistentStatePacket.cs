using System;

namespace SilksongCoop.Core.Protocol.Packets
{
    /// <summary>
    /// Type of a persistent world value.
    /// </summary>
    public enum PersistentValueType : byte
    {
        Bool = 0,
        Int = 1
    }

    /// <summary>
    /// Replicates <c>PersistentBoolItem</c>/<c>PersistentIntItem</c> state (doors, levers,
    /// chests, caches and other scripted world flags). The item is identified by a stable
    /// hash of its scene name and persistent id.
    /// </summary>
    public struct PersistentStatePacket
    {
        public PacketHeader Header;
        public ulong ItemKey;
        public PersistentValueType ValueType;
        public int Value;
        /// <summary>Monotonic revision of this persistent value.</summary>
        public uint StateRevision;
        /// <summary>Steam/transport id of the peer that originated this value.</summary>
        public ulong OriginPeerId;

        /// <summary>
        /// Serializes this packet to the specified writer.
        /// </summary>
        public void Serialize(ref PacketWriter writer)
        {
            writer.WriteHeader(Header);
            writer.WriteUInt64(ItemKey);
            writer.WriteByte((byte)ValueType);
            writer.WriteInt32(Value);
            writer.WriteUInt32(StateRevision);
            writer.WriteUInt64(OriginPeerId);
        }

        /// <summary>
        /// Deserializes a persistent state packet from the specified reader.
        /// </summary>
        public static PersistentStatePacket Deserialize(ref PacketReader reader)
        {
            return new PersistentStatePacket
            {
                Header = reader.ReadHeader(),
                ItemKey = reader.ReadUInt64(),
                ValueType = (PersistentValueType)reader.ReadByte(),
                Value = reader.ReadInt32(),
                StateRevision = reader.ReadUInt32(),
                OriginPeerId = reader.ReadUInt64()
            };
        }
    }
}
