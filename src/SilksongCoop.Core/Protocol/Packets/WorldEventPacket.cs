using System;

namespace SilksongCoop.Core.Protocol.Packets
{
    /// <summary>
    /// Kinds of permanent world-state changes that must be mirrored on both clients.
    /// </summary>
    public enum WorldEventType : byte
    {
        /// <summary>A <c>Breakable</c> wall/object was destroyed.</summary>
        BreakableBroken = 0,

        /// <summary>A <c>Gate</c> was opened.</summary>
        GateOpened = 1
    }

    /// <summary>
    /// Replicates a permanent world change (breakable wall / gate). The object is identified
    /// by the same stable scene-path hash used for enemies.
    /// </summary>
    public struct WorldEventPacket
    {
        public PacketHeader Header;
        public ulong ObjectKey;
        public WorldEventType EventType;
        public float PositionX;
        public float PositionY;
        /// <summary>Monotonic revision of this world event, used for idempotency/ordering.</summary>
        public uint StateRevision;
        /// <summary>Steam/transport id of the peer that originated this event.</summary>
        public ulong OriginPeerId;

        /// <summary>
        /// Serializes this packet to the specified writer.
        /// </summary>
        public void Serialize(ref PacketWriter writer)
        {
            writer.WriteHeader(Header);
            writer.WriteUInt64(ObjectKey);
            writer.WriteByte((byte)EventType);
            writer.WriteFloat(PositionX);
            writer.WriteFloat(PositionY);
            writer.WriteUInt32(StateRevision);
            writer.WriteUInt64(OriginPeerId);
        }

        /// <summary>
        /// Deserializes a world event packet from the specified reader.
        /// </summary>
        public static WorldEventPacket Deserialize(ref PacketReader reader)
        {
            return new WorldEventPacket
            {
                Header = reader.ReadHeader(),
                ObjectKey = reader.ReadUInt64(),
                EventType = (WorldEventType)reader.ReadByte(),
                PositionX = reader.ReadFloat(),
                PositionY = reader.ReadFloat(),
                StateRevision = reader.ReadUInt32(),
                OriginPeerId = reader.ReadUInt64()
            };
        }
    }
}
