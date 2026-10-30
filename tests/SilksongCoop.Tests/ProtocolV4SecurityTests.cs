using System;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Regression coverage for protocol v4/v5: 80-bit room-code secret, derived session key,
    /// MAC-authenticated post-handshake datagrams and the authenticated handshake ack.
    /// </summary>
    public class ProtocolV4SecurityTests
    {
        [Fact]
        public void ProtocolVersion_IsSeven_AndSessionKeyUsesV7DomainLabel()
        {
            Assert.Equal(7, LiteNetLibTransport.ProtocolVersion);

            const string code = "SK-PROTOCOL-FIVE";
            byte[] nonce = new byte[16];
            for (int i = 0; i < nonce.Length; i++) nonce[i] = (byte)(i + 1);

            byte[] key = new ConnectionAuthenticator().ComputeSessionKey(nonce, code);

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(code));
            byte[] label = Encoding.UTF8.GetBytes("SilksongCoop-Session-v7");
            byte[] message = new byte[label.Length + nonce.Length];
            Buffer.BlockCopy(label, 0, message, 0, label.Length);
            Buffer.BlockCopy(nonce, 0, message, label.Length, nonce.Length);

            Assert.Equal(hmac.ComputeHash(message), key);
        }

        [Fact]
        public void HandshakeAck_RequiresMacOnceSessionKeyIsDerived()
        {
            using var transport = new LiteNetLibTransport(1, "Host");

            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var keyField = typeof(LiteNetLibTransport).GetField("_sessionKey", flags);
            var needsMac = typeof(LiteNetLibTransport).GetMethod("NeedsMac", flags);
            var frameType = typeof(LiteNetLibTransport).GetNestedType("UdpFrameType", BindingFlags.NonPublic);
            Assert.NotNull(keyField);
            Assert.NotNull(needsMac);
            Assert.NotNull(frameType);

            object ackFrame = Enum.Parse(frameType!, "HandshakeAck");
            Assert.False((bool)needsMac!.Invoke(transport, new[] { ackFrame })!);

            // A no-code session never derives a key, so the legacy ack stays unauthenticated.
            keyField!.SetValue(transport, new byte[32]);
            Assert.True((bool)needsMac.Invoke(transport, new[] { ackFrame })!);

            keyField.SetValue(transport, null);
            Assert.False((bool)needsMac.Invoke(transport, new[] { ackFrame })!);
        }

        [Fact]
        public void RoomCode_WithSecret_UsesTwentyEightCharactersAndRoundtrips()
        {
            string code = RoomCodeCodec.EncodeWithSecret(IPAddress.Loopback, 7777);

            // SK-XXXX-XXXX-XXXX-YYYY-YYYY-YYYY-YYYY
            Assert.StartsWith("SK-", code);
            Assert.Equal(28, code.Substring(3).Replace("-", "").Length);
            Assert.True(RoomCodeCodec.TryDecode(code, out IPAddress ip, out int port));
            Assert.Equal(IPAddress.Loopback, ip);
            Assert.Equal(7777, port);

            // Canonicalization must preserve all 16 secret characters.
            Assert.True(RoomCodeCodec.TryCanonicalize(code.ToLowerInvariant(), out string canonical));
            Assert.Equal(code, canonical);
        }

        [Fact]
        public void RoomCode_LegacyTwentyCharacterCode_StillDecodes()
        {
            // A 2.1.0 code (12 address characters + 8 secret characters) must stay
            // parseable so a user pasting an old code gets a clear HMAC failure instead of
            // an "invalid code" error.
            string legacy = RoomCodeCodec.Encode(IPAddress.Loopback, 7777) + "-AAAA-AAAA";

            Assert.Equal(20, legacy.Substring(3).Replace("-", "").Length);
            Assert.True(RoomCodeCodec.TryDecode(legacy, out IPAddress ip, out int port));
            Assert.Equal(IPAddress.Loopback, ip);
            Assert.Equal(7777, port);
        }

        [Fact]
        public void SessionKey_IsDomainSeparatedFromChallengeSignature()
        {
            const string code = "SK-TEST-SESSION-KEY";
            var auth = new ConnectionAuthenticator();
            byte[] nonce = auth.GenerateNonce();

            byte[] signature = auth.ComputeSignature(nonce, code);
            byte[] key = auth.ComputeSessionKey(nonce, code);

            Assert.NotEqual(signature, key);
            Assert.Equal(key, auth.ComputeSessionKey(nonce, code));

            // A failed response must not hand out a key and must keep the nonce usable.
            byte[] wrong = auth.ComputeSignature(nonce, "SK-WRONG");
            Assert.False(auth.ValidateSignature(wrong, code, out byte[]? failedKey));
            Assert.Null(failedKey);

            Assert.True(auth.ValidateSignature(signature, code, out byte[]? derivedKey));
            Assert.NotNull(derivedKey);
            Assert.Equal(key, derivedKey);
        }

        [Fact]
        public void AuthenticatedUdpSession_DeliversDataFramesWithMac()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            host.StartHost(0);
            string code = RoomCodeCodec.EncodeWithSecret(IPAddress.Loopback, host.BoundPort);
            host.SetRoomCode(code);
            host.RequireRoomCode = true;

            using var client = new LiteNetLibTransport(2, "Client");
            client.SetRoomCode(code);
            client.Connect(code);

            for (int i = 0; i < 300 && (!host.IsConnected || !client.IsConnected); i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(10);
            }

            Assert.True(host.IsConnected, "host must accept the authenticated client");
            Assert.True(client.IsConnected, "client must accept the handshake ack");

            byte[] received = Array.Empty<byte>();
            host.OnDataReceived += (_, data, offset, length) =>
            {
                received = new byte[length];
                Buffer.BlockCopy(data, offset, received, 0, length);
            };

            byte[] payload = { 0x53, 0x4B, 0x01, 0x02, 0x03, 0x04 };
            Assert.NotNull(client.RemotePeerId);
            client.Send(client.RemotePeerId!.Value, payload, 0, payload.Length, DeliveryMethod.ReliableOrdered, NetworkChannels.Vitals);

            for (int i = 0; i < 200 && received.Length == 0; i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(10);
            }

            Assert.Equal(payload, received);

            // The reverse direction uses the same session key; a MAC bug on the host side
            // would leave the client unable to accept a single frame.
            byte[] backReceived = Array.Empty<byte>();
            client.OnDataReceived += (_, data, offset, length) =>
            {
                backReceived = new byte[length];
                Buffer.BlockCopy(data, offset, backReceived, 0, length);
            };

            byte[] reply = { 0x53, 0x4B, 0x09, 0x08, 0x07 };
            Assert.NotNull(host.RemotePeerId);
            host.Send(host.RemotePeerId!.Value, reply, 0, reply.Length, DeliveryMethod.ReliableOrdered, NetworkChannels.Vitals);

            for (int i = 0; i < 200 && backReceived.Length == 0; i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(10);
            }

            Assert.Equal(reply, backReceived);
        }

        [Fact]
        public void HostCanReturnToHostingWithSameRoomCodeAfterPeerTimeout()
        {
            var session = new CoopSessionState();
            var local = new NetworkPeerId(1, "Host");
            var remote = new NetworkPeerId(2, "Guest");

            session.SetHosting(local, "SK-SAME-CODE");
            session.SetConnected(remote, SessionRole.Host);
            Assert.True(session.IsConnected);
            Assert.True(session.IsHost);

            // The transport keeps the UDP socket bound and reports a timeout; the plugin
            // restores the hosting state with the original code.
            session.SetDisconnected(DisconnectReason.Timeout);
            Assert.Equal(SessionStatus.Disconnected, session.Status);

            session.SetHosting(local, "SK-SAME-CODE");
            Assert.Equal(SessionStatus.Hosting, session.Status);
            Assert.True(session.IsHost);
            Assert.Equal("SK-SAME-CODE", session.CurrentRoomCode);
        }
    }
}
