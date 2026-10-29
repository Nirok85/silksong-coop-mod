using System;
using System.Buffers.Binary;
using SilksongCoop.Core.Protocol;
using Xunit;

namespace SilksongCoop.Tests
{
    public class PacketBatchTests
    {
        private static byte[] Frame(PacketType type, byte[] payload)
        {
            var buffer = new byte[PacketHeader.HeaderSize + payload.Length];
            new PacketHeader(type, 0, 1, 1234).WriteTo(buffer);
            Buffer.BlockCopy(payload, 0, buffer, PacketHeader.HeaderSize, payload.Length);
            return buffer;
        }

        [Fact]
        public void WriteHeader_SetsBatchTypeAndCount()
        {
            var buffer = new byte[PacketBatch.HeaderSize];
            PacketBatch.WriteHeader(buffer, 3, 42, 7);

            Assert.True(PacketHeader.TryReadFrom(buffer, out var header));
            Assert.Equal(PacketType.Batch, header.PacketId);
            Assert.Equal((byte)3, header.ChannelId);
            Assert.Equal(42u, header.Timestamp);
            Assert.True(PacketBatch.TryReadCount(buffer, out byte count));
            Assert.Equal((byte)7, count);
        }

        [Fact]
        public void Entries_RoundTripInOrder()
        {
            var buffer = new byte[256];
            var first = Frame(PacketType.TransformSync, new byte[] { 1, 2, 3, 4 });
            var second = Frame(PacketType.TransformSync, new byte[] { 5, 6, 7 });

            Assert.True(PacketBatch.TryWriteEntry(buffer, PacketBatch.HeaderSize, first, out int wroteFirst));
            int offset = PacketBatch.HeaderSize + wroteFirst;
            Assert.True(PacketBatch.TryWriteEntry(buffer, offset, second, out int wroteSecond));
            int total = offset + wroteSecond;

            PacketBatch.WriteHeader(buffer, 0, 0, 2);
            Assert.True(PacketBatch.TryReadCount(buffer.AsSpan(0, total), out byte count));
            Assert.Equal((byte)2, count);

            int position = PacketBatch.HeaderSize;
            Assert.True(PacketBatch.TryReadEntry(buffer.AsSpan(0, total), ref position, out var entry1));
            Assert.True(first.AsSpan().SequenceEqual(entry1));

            Assert.True(PacketBatch.TryReadEntry(buffer.AsSpan(0, total), ref position, out var entry2));
            Assert.True(second.AsSpan().SequenceEqual(entry2));
            Assert.Equal(total, position);
        }

        [Fact]
        public void TryWriteEntry_RejectsOversizedPayload()
        {
            var buffer = new byte[16];
            var payload = new byte[32];
            Assert.False(PacketBatch.TryWriteEntry(buffer, PacketBatch.HeaderSize, payload, out int written));
            Assert.Equal(0, written);
        }

        [Fact]
        public void TryReadEntry_RejectsTruncatedOrEmptyEntries()
        {
            var buffer = new byte[PacketBatch.HeaderSize + 4];
            PacketBatch.WriteHeader(buffer, 0, 0, 1);

            int position = PacketBatch.HeaderSize;
            Assert.False(PacketBatch.TryReadEntry(buffer, ref position, out _));
        }

        [Fact]
        public void TryReadCount_RejectsNonBatchPacket()
        {
            var buffer = new byte[PacketBatch.HeaderSize];
            new PacketHeader(PacketType.TransformSync, 0, 0, 0).WriteTo(buffer);
            Assert.False(PacketBatch.TryReadCount(buffer, out _));
        }

        [Fact]
        public void TryReadCount_RejectsShortBuffer()
        {
            var buffer = new byte[PacketHeader.HeaderSize];
            new PacketHeader(PacketType.Batch, 0, 0, 0).WriteTo(buffer);
            Assert.False(PacketBatch.TryReadCount(buffer, out _));
        }

        [Fact]
        public void EntryLengthPrefix_IsLittleEndianUInt16()
        {
            var buffer = new byte[PacketBatch.HeaderSize + PacketBatch.EntryLengthSize + 3];
            var payload = new byte[] { 9, 8, 7 };
            Assert.True(PacketBatch.TryWriteEntry(buffer, PacketBatch.HeaderSize, payload, out _));

            ushort stored = BinaryPrimitives.ReadUInt16LittleEndian(
                buffer.AsSpan(PacketBatch.HeaderSize, PacketBatch.EntryLengthSize));
            Assert.Equal((ushort)3, stored);
        }
    }
}
