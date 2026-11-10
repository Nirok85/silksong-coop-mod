using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using SilksongCoop.Core.Networking;
using Xunit;

namespace SilksongCoop.Tests.Networking
{
    /// <summary>
    /// Regressions for the UDP audit: a duplicate handshake may not replace the peer id
    /// that owns the pending challenge, unsigned handshakes may not refresh the heartbeat,
    /// and an invalid MAC may not consume the real peer's rate-limit budget.
    /// </summary>
    public class UdpHandshakeRemediationTests
    {
        private const BindingFlags PrivateInstance =
            BindingFlags.NonPublic | BindingFlags.Instance;

        [Fact]
        public void V11Host_RejectsV10AndAcceptsMatchingPeer()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            host.StartHost(0);
            const string code = "SK-AUDIT-VERSION";
            host.SetRoomCode(code);
            using var client = new RawPeer();

            client.Send(BuildHandshake(2, version: 10), host.BoundPort);
            PumpHost(host);
            Assert.Equal(0, client.PendingBytes);
            Assert.False(host.IsConnected);

            CompleteHandshake(host, client, code, peerId: 2);
        }

        [Fact]
        public void DuplicateHandshake_CannotReplacePendingPeerId()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            host.StartHost(0);
            host.SetRoomCode("SK-AUDIT-PENDING-PEER");

            using var client = new RawPeer();
            ulong firstPeer = 2;
            ulong spoofedPeer = 3;

            client.Send(BuildHandshake(firstPeer), host.BoundPort);
            PumpHost(host);
            byte[] challenge = client.Receive();
            Assert.Equal((byte)0x08, challenge[0]); // UdpFrameType.Challenge
            byte[] nonce = challenge.AsSpan(8, 16).ToArray();

            // The same endpoint repeats the handshake with a different identity while the
            // first challenge is still unanswered. The nonce must stay bound to peer 2.
            client.Send(BuildHandshake(spoofedPeer), host.BoundPort);
            for (int i = 0; i < 5; i++)
            {
                host.PollEvents();
                Thread.Sleep(5);
            }

            byte[] signature = new ConnectionAuthenticator()
                .ComputeSignature(nonce, "SK-AUDIT-PENDING-PEER");
            client.Send(BuildResponse(nonce, firstPeer, signature), host.BoundPort);

            for (int i = 0; i < 100 && !host.IsConnected; i++)
            {
                host.PollEvents();
                Thread.Sleep(10);
            }

            Assert.True(host.IsConnected);
            Assert.NotNull(host.RemotePeerId);
            Assert.Equal(firstPeer, host.RemotePeerId!.Value.Value);
        }

        [Fact]
        public void UnsignedHandshake_DoesNotRefreshHeartbeat()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            host.StartHost(0);
            const string code = "SK-AUDIT-HEARTBEAT";
            host.SetRoomCode(code);

            using var client = new RawPeer();
            CompleteHandshake(host, client, code, peerId: 2);

            var lastReceivedField = typeof(LiteNetLibTransport)
                .GetField("_lastReceivedMs", PrivateInstance);
            Assert.NotNull(lastReceivedField);
            lastReceivedField!.SetValue(host, 0L);

            // A duplicate unsigned Handshake is still accepted for the lost-ack retry path,
            // but it proves nothing about the peer being alive.
            client.Send(BuildHandshake(2), host.BoundPort);
            for (int i = 0; i < 10; i++)
            {
                host.PollEvents();
                Thread.Sleep(10);
            }

            Assert.Equal(0L, (long)lastReceivedField.GetValue(host)!);
        }

        [Fact]
        public void InvalidMac_DoesNotConsumePeerRateLimitToken()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            host.StartHost(0);
            const string code = "SK-AUDIT-RATE-LIMIT";
            host.SetRoomCode(code);

            using var client = new RawPeer();
            CompleteHandshake(host, client, code, peerId: 2);

            var limiterField = typeof(LiteNetLibTransport)
                .GetField("_rateLimiter", PrivateInstance);
            Assert.NotNull(limiterField);
            var limiter = new RateLimiter(maxTokens: 1, refillRatePerSecond: 0);
            limiterField!.SetValue(host, limiter);

            client.Send(BuildBadMacData(), host.BoundPort);
            for (int i = 0; i < 10; i++)
            {
                host.PollEvents();
                Thread.Sleep(10);
            }

            Assert.True(limiter.TryConsume(new NetworkPeerId(2)));
        }

        private static void CompleteHandshake(
            LiteNetLibTransport host,
            RawPeer client,
            string code,
            ulong peerId)
        {
            client.Send(BuildHandshake(peerId), host.BoundPort);
            PumpHost(host);
            byte[] challenge = client.Receive();
            Assert.Equal((byte)0x08, challenge[0]);
            byte[] nonce = challenge.AsSpan(8, 16).ToArray();

            byte[] signature = new ConnectionAuthenticator().ComputeSignature(nonce, code);
            client.Send(BuildResponse(nonce, peerId, signature), host.BoundPort);

            for (int i = 0; i < 100 && !host.IsConnected; i++)
            {
                host.PollEvents();
                Thread.Sleep(10);
            }

            Assert.True(host.IsConnected);
            Assert.Equal(peerId, host.RemotePeerId!.Value.Value);
        }

        private static void PumpHost(LiteNetLibTransport host)
        {
            for (int i = 0; i < 20; i++)
            {
                host.PollEvents();
                Thread.Sleep(5);
            }
        }

        private static byte[] BuildHandshake(ulong peerId, ushort version = LiteNetLibTransport.ProtocolVersion)
        {
            byte[] payload = new byte[11];
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(0, 8), peerId);
            payload[8] = 1; // client carries a room code
            BinaryPrimitives.WriteUInt16LittleEndian(
                payload.AsSpan(9, 2),
                version);
            return BuildEnvelope(0x01, 0, payload);
        }

        private static byte[] BuildResponse(byte[] nonce, ulong peer, byte[] signature)
        {
            var payload = new byte[56];
            nonce.CopyTo(payload, 0);
            BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(16), peer);
            signature.CopyTo(payload, 24);
            return BuildEnvelope(0x09, 0, payload);
        }

        private static byte[] BuildBadMacData()
        {
            byte[] frame = BuildEnvelope(0x03, 0x01, new byte[] { 0x53, 0x4B, 0x01 });
            Array.Resize(ref frame, frame.Length + 16);
            return frame;
        }

        private static byte[] BuildEnvelope(byte type, byte flags, byte[] payload)
        {
            byte[] frame = new byte[8 + payload.Length];
            frame[0] = type;
            frame[1] = 0;
            frame[2] = (byte)DeliveryMethod.Unreliable;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(3, 4), 0);
            frame[7] = flags;
            payload.CopyTo(frame, 8);
            return frame;
        }

        private sealed class RawPeer : IDisposable
        {
            private readonly UdpClient _socket;

            public RawPeer()
            {
                _socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                _socket.Client.ReceiveTimeout = 3000;
            }

            public void Send(byte[] datagram, int port)
            {
                _socket.Send(datagram, datagram.Length, new IPEndPoint(IPAddress.Loopback, port));
            }

            public int PendingBytes => _socket.Available;

            public byte[] Receive()
            {
                var remote = new IPEndPoint(IPAddress.Any, 0);
                return _socket.Receive(ref remote);
            }

            public void Dispose() => _socket.Dispose();
        }
    }
}
