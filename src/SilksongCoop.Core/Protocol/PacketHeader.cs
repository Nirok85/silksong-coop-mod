using System;
using System.Buffers.Binary;

namespace SilksongCoop.Core.Protocol
{
    /// <summary>
    /// Fixed 12-byte zero-allocation binary framing header.
    /// [Magic (2B)][PacketId (1B)][ChannelId (1B)][TickSequence (4B)][Timestamp (4B)]
    /// </summary>
    public readonly struct PacketHeader : IEquatable<PacketHeader>
    {
        public const ushort ExpectedMagic = 0x4B53; // 'S' (0x53), 'K' (0x4B) in Little Endian
        public const byte MagicByte0 = 0x53; // 'S'
        public const byte MagicByte1 = 0x4B; // 'K'
        public const int HeaderSize = 12;

        public ushort Magic { get; }
        public PacketType PacketId { get; }
        public byte ChannelId { get; }
        public uint TickSequence { get; }
        public uint Timestamp { get; }

        /// <summary>
        /// Initializes a packet header with the expected magic value.
        /// </summary>
        /// <param name="packetId">The type of packet.</param>
        /// <param name="channelId">The channel identifier for ordering.</param>
        /// <param name="tickSequence">The sequence number for this channel.</param>
        /// <param name="timestamp">The packet timestamp in milliseconds.</param>
        public PacketHeader(PacketType packetId, byte channelId, uint tickSequence, uint timestamp)
        {
            Magic = ExpectedMagic;
            PacketId = packetId;
            ChannelId = channelId;
            TickSequence = tickSequence;
            Timestamp = timestamp;
        }

        /// <summary>
        /// Initializes a packet header with a specified magic value for validation.
        /// </summary>
        /// <param name="magic">The magic value to use.</param>
        /// <param name="packetId">The type of packet.</param>
        /// <param name="channelId">The channel identifier.</param>
        /// <param name="tickSequence">The sequence number.</param>
        /// <param name="timestamp">The timestamp in milliseconds.</param>
        public PacketHeader(ushort magic, PacketType packetId, byte channelId, uint tickSequence, uint timestamp)
        {
            Magic = magic;
            PacketId = packetId;
            ChannelId = channelId;
            TickSequence = tickSequence;
            Timestamp = timestamp;
        }

        public bool IsValidMagic => Magic == ExpectedMagic;

        /// <summary>
        /// Checks whether the packet ID is handled by this build. A switch avoids the
        /// reflection/boxing cost of <see cref="Enum.IsDefined(Type, object)"/> on the
        /// per-packet hot path.
        /// </summary>
        public bool IsValidPacketType => PacketId switch
        {
            PacketType.TransformSync => true,
            PacketType.ActionTrigger => true,
            PacketType.VitalsSync => true,
            PacketType.CurrencySync => true,
            PacketType.ItemUnlockSync => true,
            PacketType.PlayerDeath => true,
            PacketType.BenchCheckpointSync => true,
            PacketType.SceneTransition => true,
            PacketType.EnemySync => true,
            PacketType.EnemySnapshot => true,
            PacketType.WorldEvent => true,
            PacketType.PersistentState => true,
            _ => false
        };


        /// <summary>
        /// Writes the header to the destination span in little-endian format.
        /// </summary>
        /// <param name="destination">Span to write the 12-byte header into.</param>
        public void WriteTo(Span<byte> destination)
        {
            if (destination.Length < HeaderSize)
                throw new ArgumentException("Destination span too small for packet header", nameof(destination));

            destination[0] = MagicByte0;
            destination[1] = MagicByte1;
            destination[2] = (byte)PacketId;
            destination[3] = ChannelId;
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4, 4), TickSequence);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(8, 4), Timestamp);
        }

        /// <summary>
        /// Attempts to read a packet header from the source span.
        /// </summary>
        /// <param name="source">Span containing the header bytes.</param>
        /// <param name="header">Output header if successful.</param>
        /// <returns>True if a valid header was read; otherwise, false.</returns>
        public static bool TryReadFrom(ReadOnlySpan<byte> source, out PacketHeader header)
        {
            header = default;
            if (source.Length < HeaderSize)
                return false;

            if (source[0] != MagicByte0 || source[1] != MagicByte1)
                return false;

            ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(0, 2));
            PacketType packetId = (PacketType)source[2];
            byte channelId = source[3];
            uint seq = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4, 4));
            uint timestamp = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(8, 4));

            header = new PacketHeader(magic, packetId, channelId, seq, timestamp);
            return true;
        }

        /// <summary>
        /// Reads a packet header from the source span, throwing if invalid.
        /// </summary>
        /// <param name="source">Span containing the header bytes.</param>
        /// <returns>The parsed packet header.</returns>
        public static PacketHeader ReadFrom(ReadOnlySpan<byte> source)
        {
            if (!TryReadFrom(source, out var header))
                throw new FormatException("Failed to read valid PacketHeader from span");
            return header;
        }

        /// <summary>
        /// Determines whether this header equals another header.
        /// </summary>
        /// <param name="other">The other header to compare.</param>
        /// <returns>True if all header fields are equal.</returns>
        public bool Equals(PacketHeader other) =>
            Magic == other.Magic &&
            PacketId == other.PacketId &&
            ChannelId == other.ChannelId &&
            TickSequence == other.TickSequence &&
            Timestamp == other.Timestamp;

        public override bool Equals(object? obj) => obj is PacketHeader other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Magic, PacketId, ChannelId, TickSequence, Timestamp);

        /// <summary>
        /// Determines whether two packet headers are equal.
        /// </summary>
        public static bool operator ==(PacketHeader left, PacketHeader right) => left.Equals(right);
        
        /// <summary>
        /// Determines whether two packet headers are not equal.
        /// </summary>
        public static bool operator !=(PacketHeader left, PacketHeader right) => !left.Equals(right);
    }
}
