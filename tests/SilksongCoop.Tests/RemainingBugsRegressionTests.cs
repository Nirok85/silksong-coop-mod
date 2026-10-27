using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using SilksongCoop.Core.Networking;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Regression tests for the bugs fixed in the second deep audit (BUG-N1..BUG-N6).
    /// </summary>
    public class RemainingBugsRegressionTests
    {
        private const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        private static void WaitForConnection(LiteNetLibTransport host, LiteNetLibTransport client)
        {
            for (int i = 0; i < 40 && (!host.IsConnected || !client.IsConnected); i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(10);
            }

            Assert.True(host.IsConnected, "host must connect");
            Assert.True(client.IsConnected, "client must connect");
        }

        /// <summary>
        /// BUG-N1: the transform stream (unreliable) and the bench checkpoint (reliable) share
        /// channel 0. A single per-channel sequence counter made the first reliable packet
        /// jump far ahead of the receiver's expected sequence, so it sat in the reorder
        /// buffer forever and the bench checkpoint was never applied.
        /// </summary>
        [Fact]
        public void LiteNetLib_ReliableOrdered_AfterUnreliable_OnSameChannel_IsDelivered()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            using var client = new LiteNetLibTransport(2, "Client");

            host.StartHost(0);
            client.Connect($"127.0.0.1:{host.BoundPort}");
            WaitForConnection(host, client);

            bool gotReliable = false;
            host.OnDataReceived += (peer, data, offset, len) =>
            {
                if (len == 4 && BitConverter.ToInt32(data, offset) == 424242)
                    gotReliable = true;
            };

            // Simulate a transform stream on the same channel before the reliable packet.
            for (int i = 0; i < 60; i++)
            {
                byte[] unreliable = BitConverter.GetBytes(i);
                client.Send(host.LocalPeerId, unreliable, 0, unreliable.Length, DeliveryMethod.Unreliable, 0);
            }

            byte[] reliable = BitConverter.GetBytes(424242);
            client.Send(host.LocalPeerId, reliable, 0, reliable.Length, DeliveryMethod.ReliableOrdered, 0);

            for (int i = 0; i < 40 && !gotReliable; i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(5);
            }

            Assert.True(gotReliable, "reliable packet on a channel also used by unreliable traffic must be delivered");
        }

        /// <summary>
        /// BUG-N13 (related invariant): the host must keep its listening socket after a peer
        /// disconnects; otherwise the client auto-reconnect feature can never succeed.
        /// </summary>
        [Fact]
        public void LiteNetLib_HostStaysListening_AfterPeerDisconnects_NewPeerCanJoin()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            host.StartHost(0);

            using var firstClient = new LiteNetLibTransport(2, "Client1");
            firstClient.Connect($"127.0.0.1:{host.BoundPort}");
            WaitForConnection(host, firstClient);

            firstClient.Disconnect();

            for (int i = 0; i < 40 && host.IsConnected; i++)
            {
                host.PollEvents();
                Thread.Sleep(10);
            }

            Assert.False(host.IsConnected);

            using var secondClient = new LiteNetLibTransport(3, "Client2");
            secondClient.Connect($"127.0.0.1:{host.BoundPort}");
            WaitForConnection(host, secondClient);
        }

        /// <summary>
        /// Same invariant for the deterministic mock transport (kept in parity with the UDP one).
        /// </summary>
        [Fact]
        public void Mock_ReliableOrdered_AfterUnreliable_OnSameChannel_IsDelivered()
        {
            var (host, client) = MockNetworkTransport.CreatePair();

            bool gotReliable = false;
            client.OnDataReceived += (peer, data, offset, len) =>
            {
                if (len == 4 && BitConverter.ToInt32(data, offset) == 999)
                    gotReliable = true;
            };

            for (int i = 0; i < 20; i++)
            {
                byte[] unreliable = BitConverter.GetBytes(i);
                host.Send(client.LocalPeerId, unreliable, 0, unreliable.Length, DeliveryMethod.Unreliable, 0);
            }

            byte[] reliable = BitConverter.GetBytes(999);
            host.Send(client.LocalPeerId, reliable, 0, reliable.Length, DeliveryMethod.ReliableOrdered, 0);

            client.PollEvents();

            Assert.True(gotReliable);
        }

        /// <summary>
        /// BUG-N3: a room code left over from a previous hosting session must not be reused
        /// for a direct IP connection — otherwise the client advertises a shared secret it
        /// does not actually share and the host's HMAC challenge never validates.
        /// </summary>
        [Fact]
        public void LiteNetLib_DirectIpConnect_IgnoresStaleRoomCode()
        {
            string hostCode = RoomCodeCodec.Encode("127.0.0.1", 7777);
            string staleCode = RoomCodeCodec.Encode("127.0.0.1", 7778);

            using var host = new LiteNetLibTransport(1, "Host");
            host.SetRoomCode(hostCode); // host is protected by its real code
            host.StartHost(0);

            using var client = new LiteNetLibTransport(2, "Client");
            client.SetRoomCode(staleCode); // leftover from an earlier session
            client.Connect($"127.0.0.1:{host.BoundPort}");

            WaitForConnection(host, client);
        }

        /// <summary>
        /// BUG-N6: an 8-character IPv4 address (e.g. 10.0.0.1) used to be classified as a
        /// deprecated 8-char room code, so Connect() threw instead of dialling the address.
        /// </summary>
        [Fact]
        public void RoomCode_IsDeprecatedCode_DoesNotMisfireOnEightCharacterIp()
        {
            Assert.False(RoomCodeCodec.IsDeprecatedCode("10.0.0.1"));
            Assert.False(RoomCodeCodec.IsDeprecatedCode("1.20.0.1"));
            Assert.False(RoomCodeCodec.IsDeprecatedCode("127.0.0.1"));
            Assert.False(RoomCodeCodec.IsDeprecatedCode("192.168.1.5:7777"));
            Assert.False(RoomCodeCodec.IsDeprecatedCode(RoomCodeCodec.Encode("10.0.0.1", 7777)));

            string legacy = BuildLegacyEightCharCode(new byte[] { 10, 0, 0, 1 }, 7777);
            Assert.Equal(8, legacy.Length);
            Assert.True(RoomCodeCodec.IsDeprecatedCode(legacy));
        }

        /// <summary>
        /// BUG-N4: sequence number 0 was used as "nothing received yet", so duplicates of the
        /// first sequenced packet were delivered over and over.
        /// </summary>
        [Fact]
        public void Mock_Sequenced_DuplicateFirstSequence_IsDeliveredOnce()
        {
            var (host, client) = MockNetworkTransport.CreatePair();

            int received = 0;
            client.OnDataReceived += (peer, data, offset, len) => { if (len == 4) received++; };

            byte[] payload = BitConverter.GetBytes(7);
            var packet = new MockNetworkTransport.QueuedPacket
            {
                Channel = 0,
                Method = DeliveryMethod.Sequenced,
                Sequence = 0,
                Data = payload,
                DeliveryTimeTicks = 0,
                Sender = host.LocalPeerId
            };

            client.EnqueueIncoming(packet);
            client.EnqueueIncoming(packet);

            client.PollEvents();

            Assert.Equal(1, received);
        }

        /// <summary>
        /// The mock transport reports its simulated latency/loss through the new transport
        /// diagnostics properties.
        /// </summary>
        [Fact]
        public void Mock_ReportsSimulatedRoundTripAndLoss()
        {
            var (host, _) = MockNetworkTransport.CreatePair(latencyMs: 30, lossRate: 0.05f);

            Assert.Equal(60, host.RoundTripTimeMs);
            Assert.Equal(5, host.PacketLossPercent);
        }

        /// <summary>
        /// BUG-N12: the code body itself can start with "SK" (e.g. 204.192.0.1, which encodes to SK...). Pasting
        /// such a code without the "SK-" prefix must not lose the first two characters.
        /// </summary>
        [Fact]
        public void RoomCode_BareBodyStartingWithSkPrefix_StillDecodes()
        {
            var ip = IPAddress.Parse("204.192.0.1");
            string code = RoomCodeCodec.Encode(ip, 7777);
            string body = code.Substring(3); // strip "SK-"

            Assert.StartsWith("SK", body);
            Assert.True(RoomCodeCodec.TryDecode(body, out IPAddress decoded, out int port));
            Assert.Equal(ip, decoded);
            Assert.Equal(7777, port);
        }

        /// <summary>
        /// BUG-N14: the plugin applied remote-item logic to its own discoveries because
        /// OnItemUnlocked fires for local unlocks too. The dedicated remote event must not.
        /// </summary>
        [Fact]
        public void Inventory_LocalDiscovery_FiresOnlyLocalEvent_RemotePacketFiresRemoteEvent()
        {
            var service = new SilksongCoop.Core.State.InventorySyncService();

            int localEvents = 0;
            int remoteEvents = 0;
            service.OnItemUnlocked += (_, _) => localEvents++;
            service.OnRemoteItemUnlocked += (_, _) => remoteEvents++;

            service.OnLocalItemDiscovered("Hunter", SilksongCoop.Core.Protocol.Packets.ItemCategory.Crest);

            Assert.Equal(1, localEvents);
            Assert.Equal(0, remoteEvents);

            var packet = new SilksongCoop.Core.Protocol.Packets.ItemUnlockPacket
            {
                ItemId = "Wanderer",
                Category = SilksongCoop.Core.Protocol.Packets.ItemCategory.Crest,
                TransactionId = Guid.NewGuid()
            };

            service.HandleItemUnlockPacket(new NetworkPeerId(2, "Partner"), packet);

            Assert.Equal(2, localEvents);
            Assert.Equal(1, remoteEvents);
        }

        /// <summary>
        /// Builds a legal 8-character (deprecated) code: 4 IP bytes, 6-bit port offset and the
        /// 2-bit XOR checksum, Crockford-Base32 encoded.
        /// </summary>
        private static string BuildLegacyEightCharCode(byte[] ip, int port)
        {
            byte portOffset = (byte)(port - 7777);
            byte crc = (byte)((ip[0] ^ ip[1] ^ ip[2] ^ ip[3]) & 0x03);

            ulong value = ((ulong)ip[0] << 32) | ((ulong)ip[1] << 24) | ((ulong)ip[2] << 16) | ((ulong)ip[3] << 8);
            int packedPortAndCrc = ((portOffset & 0x3F) << 2) | (crc & 0x03);
            value |= (uint)packedPortAndCrc;

            var chars = new char[8];
            for (int i = 7; i >= 0; i--)
            {
                chars[i] = CrockfordAlphabet[(int)(value & 0x1F)];
                value >>= 5;
            }

            return new string(chars);
        }
    }
}
