using System;

namespace SilksongCoop.Core.Protocol.Packets
{
    /// <summary>
    /// Network packet representing a scene transition event.
    /// </summary>
    public struct SceneTransitionPacket
    {
        public PacketHeader Header;
        public string TargetScene;
        public string EntryGate;
        public int SceneNameHash;
        public byte TransitionPhase;

        /// <summary>
        /// Serializes this packet to the specified writer.
        /// </summary>
        /// <param name="writer">The packet writer to serialize to.</param>
        public void Serialize(ref PacketWriter writer)
        {
            writer.WriteHeader(Header);
            writer.WriteString(TargetScene ?? string.Empty);
            writer.WriteString(EntryGate ?? string.Empty);
            writer.WriteInt32(SceneNameHash);
            writer.WriteByte(TransitionPhase);
        }

        /// <summary>
        /// Deserializes a scene transition packet from the specified reader.
        /// </summary>
        /// <param name="reader">The packet reader to deserialize from.</param>
        /// <returns>The deserialized scene transition packet.</returns>
        public static SceneTransitionPacket Deserialize(ref PacketReader reader)
        {
            return new SceneTransitionPacket
            {
                Header = reader.ReadHeader(),
                TargetScene = reader.ReadString() ?? string.Empty,
                EntryGate = reader.ReadString() ?? string.Empty,
                SceneNameHash = reader.ReadInt32(),
                TransitionPhase = reader.ReadByte()
            };
        }
    }
}
