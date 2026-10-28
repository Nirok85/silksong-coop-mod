using System;

namespace SilksongCoop.Core.Protocol.Packets
{
    /// <summary>
    /// Identifies the type of death event in a death lifecycle packet.
    /// </summary>
    public enum DeathEventType : byte
    {
        SoloDeathOccurred = 0,
        SpectatorCountdownSync = 1,
        PartnerReviveTrigger = 2,
        TeamWipeOccurred = 3,
        BenchCheckpointUpdate = 4,
        /// <summary>
        /// The sender fell back to a SOLO bench respawn (for example when the partner was
        /// in another room during the revive countdown). The receiver must only mark the
        /// partner as alive again — it must NOT wipe its own living player.
        /// </summary>
        SoloBenchRespawn = 5
    }

    /// <summary>
    /// Network packet representing a death lifecycle event.
    /// </summary>
    public struct DeathLifecyclePacket
    {
        public PacketHeader Header;
        public DeathEventType EventType;
        public byte FallenPlayerIndex;
        public float CoordinateX;
        public float CoordinateY;
        public float RemainingSeconds;
        public ushort BenchSceneHash;
        public uint Epoch;

        /// <summary>
        /// Serializes this packet to the specified writer.
        /// </summary>
        /// <param name="writer">The packet writer to serialize to.</param>
        public void Serialize(ref PacketWriter writer)
        {
            writer.WriteHeader(Header);
            writer.WriteByte((byte)EventType);
            writer.WriteByte(FallenPlayerIndex);
            writer.WriteFloat(CoordinateX);
            writer.WriteFloat(CoordinateY);
            writer.WriteFloat(RemainingSeconds);
            writer.WriteUInt16(BenchSceneHash);
            writer.WriteUInt32(Epoch);
        }

        /// <summary>
        /// Deserializes a death lifecycle packet from the specified reader.
        /// </summary>
        /// <param name="reader">The packet reader to deserialize from.</param>
        /// <returns>The deserialized death lifecycle packet.</returns>
        public static DeathLifecyclePacket Deserialize(ref PacketReader reader)
        {
            return new DeathLifecyclePacket
            {
                Header = reader.ReadHeader(),
                EventType = (DeathEventType)reader.ReadByte(),
                FallenPlayerIndex = reader.ReadByte(),
                CoordinateX = reader.ReadFloat(),
                CoordinateY = reader.ReadFloat(),
                RemainingSeconds = reader.ReadFloat(),
                BenchSceneHash = reader.ReadUInt16(),
                Epoch = reader.ReadUInt32()
            };
        }
    }
}
