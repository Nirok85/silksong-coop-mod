using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using SilksongCoop.Core.Networking;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Regression tests for room-code handshake authentication. A client must never accept
    /// a HandshakeAck before the HMAC challenge has produced its session key.
    /// </summary>
    public class HandshakeSecurityTests
    {
        [Fact]
        public void RoomCodeClient_RejectsHandshakeAckBeforeChallenge()
        {
            // A fake host that never issues a Challenge. It only answers the client's
            // handshake with a syntactically valid HandshakeAck.
            using var fakeHost = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)fakeHost.Client.LocalEndPoint!).Port;
            fakeHost.Client.ReceiveTimeout = 3000;

            using var client = new LiteNetLibTransport(2, "Client");
            string code = RoomCodeCodec.Encode(IPAddress.Loopback, port);
            client.Connect(code);

            var from = new IPEndPoint(IPAddress.Any, 0);
            byte[] handshake = fakeHost.Receive(ref from);
            Assert.True(handshake.Length >= 8);
            Assert.Equal((byte)0x01, handshake[0]); // UdpFrameType.Handshake

            byte[] ack = BuildHandshakeAck(hostPeerId: 1);
            fakeHost.Send(ack, ack.Length, from);

            for (int i = 0; i < 10; i++)
            {
                client.PollEvents();
                Thread.Sleep(10);
            }

            Assert.False(client.IsConnected);
            Assert.False(client.RemotePeerId.HasValue);
        }

        [Fact]
        public void RoomCodeHandshake_WithMatchingCode_StillConnects()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            host.StartHost(0);
            Assert.True(host.BoundPort > 0);

            string code = RoomCodeCodec.Encode(IPAddress.Loopback, host.BoundPort);
            host.SetRoomCode(code);

            using var client = new LiteNetLibTransport(2, "Client");
            client.Connect(code);

            for (int i = 0; i < 50 && (!host.IsConnected || !client.IsConnected); i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(10);
            }

            Assert.True(host.IsConnected);
            Assert.True(client.IsConnected);
            Assert.Equal((ulong)2, host.RemotePeerId!.Value.Value);
            Assert.Equal((ulong)1, client.RemotePeerId!.Value.Value);
        }

        private static byte[] BuildHandshakeAck(ulong hostPeerId)
        {
            // Envelope: type(1) channel(1) method(1) sequence(4) flags(1) payload
            // Payload: peer id (8) + protocol version (2), no MAC.
            byte[] ack = new byte[8 + 10];
            ack[0] = 0x02; // UdpFrameType.HandshakeAck
            Buffer.BlockCopy(BitConverter.GetBytes(hostPeerId), 0, ack, 8, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(LiteNetLibTransport.ProtocolVersion), 0, ack, 16, 2);
            return ack;
        }
    }
}
