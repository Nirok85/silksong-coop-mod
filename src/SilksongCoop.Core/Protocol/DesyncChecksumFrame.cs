using System;
using System.Buffers.Binary;

namespace SilksongCoop.Core.Protocol
{
    /// <summary>
    /// Fixed-size framing for desync checksums:
    /// [Magic 'C','S' (2B)][Round (4B)][Checksum (4B)].
    /// </summary>
    /// <remarks>
    /// Checksums used to be sent as bare UTF-8 strings, which made any malformed or
    /// truncated datagram look like a checksum. A dedicated magic plus a round number
    /// lets the receiver reject foreign frames and compare samples that describe the
    /// same moment on both peers.
    /// </remarks>
    public static class DesyncChecksumFrame
    {
        public const byte Magic0 = (byte)'C';
        public const byte Magic1 = (byte)'S';
        public const int FrameSize = 10;

        public static void Write(Span<byte> destination, uint round, uint checksum)
        {
            if (destination.Length < FrameSize)
                throw new ArgumentException("Destination span too small for a checksum frame", nameof(destination));

            destination[0] = Magic0;
            destination[1] = Magic1;
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(2, 4), round);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(6, 4), checksum);
        }

        public static bool TryRead(ReadOnlySpan<byte> source, out uint round, out uint checksum)
        {
            round = 0;
            checksum = 0;

            // A checksum frame is the whole datagram. Accepting trailing bytes would let a
            // larger payload whose first two bytes happen to be "CS" masquerade as a
            // checksum and be silently swallowed instead of reaching the packet parser.
            if (source.Length != FrameSize) return false;
            if (source[0] != Magic0 || source[1] != Magic1) return false;

            round = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(2, 4));
            checksum = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(6, 4));
            return true;
        }
    }
}
