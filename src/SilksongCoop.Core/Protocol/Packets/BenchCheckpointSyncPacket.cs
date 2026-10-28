using System;

namespace SilksongCoop.Core.Protocol.Packets
{
    /// <summary>
    /// Network packet representing a bench checkpoint synchronization.
    /// </summary>
    public struct BenchCheckpointSyncPacket
    {
        public PacketHeader Header;
        public string SceneName;
        public string RespawnMarker;

        // Wire compatibility: the two values already travel in separate fields (int32 +
        // byte). They were simply named after the wrong semantics. `FacingDirection` has
        // always carried the spawn type and `AtBench` has always carried "facing right".
        // The aliases below give callers the correct names without changing the layout.
        public int FacingDirection;
        public bool AtBench;

        /// <summary>Bench spawn type passed to <c>PlayerData.SetBenchRespawn</c>.</summary>
        public int SpawnType
        {
            get => FacingDirection;
            set => FacingDirection = value;
        }

        /// <summary>Whether the player faces right at the checkpoint.</summary>
        public bool FacingRight
        {
            get => AtBench;
            set => AtBench = value;
        }

        /// <summary>
        /// Serializes this packet to the specified writer.
        /// </summary>
        /// <param name="writer">The packet writer to serialize to.</param>
        public void Serialize(ref PacketWriter writer)
        {
            writer.WriteHeader(Header);
            writer.WriteString(SceneName ?? string.Empty);
            writer.WriteString(RespawnMarker ?? string.Empty);
            writer.WriteInt32(FacingDirection);
            writer.WriteByte((byte)(AtBench ? 1 : 0));
        }

        /// <summary>
        /// Deserializes a bench checkpoint sync packet from the specified reader.
        /// </summary>
        /// <param name="reader">The packet reader to deserialize from.</param>
        /// <returns>The deserialized bench checkpoint sync packet.</returns>
        public static BenchCheckpointSyncPacket Deserialize(ref PacketReader reader)
        {
            return new BenchCheckpointSyncPacket
            {
                Header = reader.ReadHeader(),
                SceneName = reader.ReadString() ?? string.Empty,
                RespawnMarker = reader.ReadString() ?? string.Empty,
                FacingDirection = reader.ReadInt32(),
                AtBench = reader.ReadByte() != 0
            };
        }
    }
}
