using System;
using SilksongCoop.Core.Protocol;
using Xunit;

namespace SilksongCoop.Tests
{
    public class DesyncChecksumFrameTests
    {
        [Fact]
        public void RoundTripsRoundAndChecksum()
        {
            var buffer = new byte[DesyncChecksumFrame.FrameSize];
            DesyncChecksumFrame.Write(buffer, 1234u, 0xDEADBEEF);

            Assert.True(DesyncChecksumFrame.TryRead(buffer, out uint round, out uint checksum));
            Assert.Equal(1234u, round);
            Assert.Equal(0xDEADBEEFu, checksum);
        }

        [Fact]
        public void RejectsForeignFrame()
        {
            var buffer = new byte[DesyncChecksumFrame.FrameSize];
            buffer[0] = (byte)'X';
            buffer[1] = (byte)'Y';

            Assert.False(DesyncChecksumFrame.TryRead(buffer, out _, out _));
        }

        [Fact]
        public void RejectsFrameWithTrailingBytes()
        {
            var buffer = new byte[DesyncChecksumFrame.FrameSize + 1];
            DesyncChecksumFrame.Write(buffer, 1u, 2u);

            Assert.False(DesyncChecksumFrame.TryRead(buffer, out _, out _));
        }

        [Fact]
        public void RejectsShortBuffer()
        {
            var buffer = new byte[DesyncChecksumFrame.FrameSize - 1];
            Assert.False(DesyncChecksumFrame.TryRead(buffer, out _, out _));
        }

        [Fact]
        public void WriteRejectsTooSmallDestination()
        {
            var buffer = new byte[DesyncChecksumFrame.FrameSize - 1];
            Assert.Throws<ArgumentException>(() => DesyncChecksumFrame.Write(buffer, 0u, 0u));
        }
    }
}
