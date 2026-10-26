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

        public PacketHeader(PacketType packetId, byte channelId, uint tickSequence, uint timestamp)
        {
            Magic = ExpectedMagic;
            PacketId = packetId;
            ChannelId = channelId;
            TickSequence = tickSequence;
            Timestamp = timestamp;
        }

        public PacketHeader(ushort magic, PacketType packetId, byte channelId, uint tickSequence, uint timestamp)
        {
            Magic = magic;
            PacketId = packetId;
            ChannelId = channelId;
            TickSequence = tickSequence;
            Timestamp = timestamp;
        }

        public bool IsValidMagic => Magic == ExpectedMagic;

        public bool IsValidPacketType => Enum.IsDefined(typeof(PacketType), PacketId);

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

        public static PacketHeader ReadFrom(ReadOnlySpan<byte> source)
        {
            if (!TryReadFrom(source, out var header))
                throw new FormatException("Failed to read valid PacketHeader from span");
            return header;
        }

        public bool Equals(PacketHeader other) =>
            Magic == other.Magic &&
            PacketId == other.PacketId &&
            ChannelId == other.ChannelId &&
            TickSequence == other.TickSequence &&
            Timestamp == other.Timestamp;

        public override bool Equals(object? obj) => obj is PacketHeader other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Magic, PacketId, ChannelId, TickSequence, Timestamp);

        public static bool operator ==(PacketHeader left, PacketHeader right) => left.Equals(right);
        public static bool operator !=(PacketHeader left, PacketHeader right) => !left.Equals(right);
    }
}
