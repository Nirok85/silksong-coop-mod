using System;
using System.Buffers.Binary;

namespace SilksongCoop.Core.Protocol
{
    /// <summary>
    /// Framing helpers for aggregated datagrams. A batch envelope is a normal
    /// <see cref="PacketHeader"/> with <see cref="PacketType.Batch"/> followed by a one-byte
    /// entry count and a sequence of length-prefixed packets:
    /// [Header (12B)][Count (1B)][Length (2B)][Payload]...
    /// </summary>
    public static class PacketBatch
    {
        public const int MaxEntries = 255;
        public const int CountSize = 1;
        public const int EntryLengthSize = 2;

        /// <summary>
        /// Number of bytes that precede the first batch entry, including the packet header.
        /// </summary>
        public const int HeaderSize = PacketHeader.HeaderSize + CountSize;

        /// <summary>
        /// Writes the batch header (packet header plus entry count) into the destination span.
        /// </summary>
        public static void WriteHeader(Span<byte> destination, byte channel, uint timestamp, byte count)
        {
            if (destination.Length < HeaderSize)
                throw new ArgumentException("Destination span too small for a batch header", nameof(destination));

            new PacketHeader(PacketType.Batch, channel, 0, timestamp).WriteTo(destination);
            destination[PacketHeader.HeaderSize] = count;
        }

        /// <summary>
        /// Writes a single length-prefixed entry at the given offset.
        /// </summary>
        /// <param name="destination">Target datagram buffer.</param>
        /// <param name="offset">Offset at which the entry starts.</param>
        /// <param name="payload">Serialized packet bytes.</param>
        /// <param name="bytesWritten">Total bytes written (length prefix plus payload).</param>
        /// <returns>True when the entry fits; otherwise false.</returns>
        public static bool TryWriteEntry(Span<byte> destination, int offset, ReadOnlySpan<byte> payload, out int bytesWritten)
        {
            bytesWritten = 0;
            if (payload.Length <= 0 || payload.Length > ushort.MaxValue) return false;
            if (offset < 0 || offset + EntryLengthSize + payload.Length > destination.Length) return false;

            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(offset, EntryLengthSize), (ushort)payload.Length);
            payload.CopyTo(destination.Slice(offset + EntryLengthSize));
            bytesWritten = EntryLengthSize + payload.Length;
            return true;
        }

        /// <summary>
        /// Reads the entry count from a batch envelope.
        /// </summary>
        public static bool TryReadCount(ReadOnlySpan<byte> source, out byte count)
        {
            count = 0;
            if (source.Length < HeaderSize) return false;
            if (!PacketHeader.TryReadFrom(source, out var header)) return false;
            if (header.PacketId != PacketType.Batch) return false;

            count = source[PacketHeader.HeaderSize];
            return true;
        }

        /// <summary>
        /// Reads the next length-prefixed entry, advancing <paramref name="offset"/>.
        /// </summary>
        public static bool TryReadEntry(ReadOnlySpan<byte> source, ref int offset, out ReadOnlySpan<byte> payload)
        {
            payload = default;
            if (offset < HeaderSize || offset + EntryLengthSize > source.Length) return false;

            int length = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset, EntryLengthSize));
            offset += EntryLengthSize;
            if (length <= 0 || offset + length > source.Length) return false;

            payload = source.Slice(offset, length);
            offset += length;
            return true;
        }
    }
}
