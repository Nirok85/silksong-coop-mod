using System;
using SilksongCoop.Core.Networking;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Tests for the peer identity used by OriginPeerId in world packets (3.0.1).
    /// </summary>
    public class TransportIdentityTests
    {
        [Fact]
        public void SetLocalPeerId_ReplacesTheConstructorIdentity()
        {
            using var transport = new LiteNetLibTransport(1, "Host");

            transport.SetLocalPeerId(2, "Client");

            Assert.Equal(2UL, transport.LocalPeerId.Value);
            Assert.Equal("Client", transport.LocalPeerId.DisplayName);
        }

        [Fact]
        public void SetLocalPeerId_RejectsTheReservedZeroId()
        {
            using var transport = new LiteNetLibTransport(1, "Host");

            Assert.Throws<ArgumentOutOfRangeException>(() => transport.SetLocalPeerId(0, "Invalid"));
        }
    }
}
