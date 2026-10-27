using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Empirical adversarial stress tests challenging protocol robustness,
    /// corrupt payload injection resilience, and MockNetworkTransport stability
    /// under high latency, severe jitter, and 50% packet loss.
    /// </summary>
    public class AdversarialProtocolStressTests
    {
        #region 1. Corrupt Payload Injection & Robustness

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(8)]
        [InlineData(11)]
        public void TruncatedHeaders_VariousLengths_RejectedSafelyWithoutCrash(int length)
        {
            byte[] truncated = new byte[length];
            Array.Fill<byte>(truncated, 0x53); // Fill with valid first magic byte

            // 1. PacketHeader.TryReadFrom must return false without throwing
            bool success = PacketHeader.TryReadFrom(truncated, out var header);
            Assert.False(success);
            Assert.Equal(default, header);

            // 2. PacketReader.TryReadHeader must return false without advancing position
            var reader = new PacketReader(truncated);
            Assert.False(reader.TryReadHeader(out var readerHeader));
            Assert.Equal(0, reader.Position);

            // 3. PacketReader.ReadHeader must throw InvalidOperationException gracefully
            bool threw = false;
            try
            {
                var r = new PacketReader(truncated);
                r.ReadHeader();
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            Assert.True(threw);
        }

        [Fact]
        public void InvalidMagicBytes_AllVariationsAndFuzz_RejectedSafely()
        {
            ushort[] invalidMagics = new ushort[]
            {
                0x0000,
                0xFFFF,
                0x1234,
                0x5353, // 'S', 'S'
                0x4B4B, // 'K', 'K'
                0x5300,
                0x004B,
                0x4B53 ^ 0xFFFF
            };

            Span<byte> buffer = stackalloc byte[PacketHeader.HeaderSize];

            foreach (var magic in invalidMagics)
            {
                buffer[0] = (byte)(magic & 0xFF);
                buffer[1] = (byte)((magic >> 8) & 0xFF);
                buffer[2] = (byte)PacketType.TransformSync;
                buffer[3] = 0; // channel 0

                bool success = PacketHeader.TryReadFrom(buffer, out var header);
                Assert.False(success, $"Magic 0x{magic:X4} should have been rejected");

                var reader = new PacketReader(buffer);
                Assert.False(reader.TryReadHeader(out _));
                Assert.Equal(0, reader.Position);
            }

            // Pseudo-random fuzzing over 256 invalid magic combinations
            var rng = new Random(42);
            for (int i = 0; i < 256; i++)
            {
                byte b0 = (byte)rng.Next(256);
                byte b1 = (byte)rng.Next(256);
                if (b0 == PacketHeader.MagicByte0 && b1 == PacketHeader.MagicByte1)
                    continue;

                buffer[0] = b0;
                buffer[1] = b1;

                Assert.False(PacketHeader.TryReadFrom(buffer, out _));
            }
        }

        [Theory]
        [InlineData(0x00)]
        [InlineData(0x06)]
        [InlineData(0x0E)]
        [InlineData(0x1F)]
        [InlineData(0x2F)]
        [InlineData(0x3F)]
        [InlineData(0x4F)]
        [InlineData(0x60)]
        [InlineData(0xFE)]
        [InlineData(0xFF)]
        public void UnknownPacketTypes_ParsesHeader_ApplicationDispatcherIgnoresSafely(byte unknownType)
        {
            Span<byte> buffer = stackalloc byte[PacketHeader.HeaderSize + 16];
            buffer[0] = PacketHeader.MagicByte0;
            buffer[1] = PacketHeader.MagicByte1;
            buffer[2] = unknownType;
            buffer[3] = 1; // Channel

            bool headerRead = PacketHeader.TryReadFrom(buffer, out var header);
            Assert.True(headerRead);
            Assert.Equal((PacketType)unknownType, header.PacketId);

            // Simulate application packet dispatcher router
            bool handled = false;
            var reader = new PacketReader(buffer);
            if (reader.TryReadHeader(out var h))
            {
                switch (h.PacketId)
                {
                    case PacketType.TransformSync:
                    case PacketType.ActionTrigger:
                    case PacketType.VitalsSync:
                    case PacketType.CurrencySync:
                    case PacketType.ItemUnlockSync:
                    case PacketType.PlayerDeath:
                        handled = true;
                        break;
                    default:
                        // Unknown packet type safely discarded without crash
                        handled = false;
                        break;
                }
            }

            Assert.False(handled, $"Unknown packet type 0x{unknownType:X2} should not match recognized types");
        }

        [Fact]
        public void TruncatedPacketBodies_AllPacketTypes_ThrowCleanBufferUnderflow()
        {
            // 1. TransformSyncPacket (expected size = 41 bytes)
            var transform = new TransformSyncPacket
            {
                Header = new PacketHeader(PacketType.TransformSync, 0, 1, 100),
                PositionX = 10f, PositionY = 20f, VelocityX = 1f, VelocityY = 2f,
                FacingDirection = 1, StateFlags = 1, AnimationClipHash = 1234, NormalizedTime = 500,
                SceneNameHash = 9999
            };
            Span<byte> tBuf = stackalloc byte[64];
            var tw = new PacketWriter(tBuf);
            transform.Serialize(ref tw);
            int tLen = tw.Position;
            Assert.Equal(41, tLen);

            // Truncate at every length from 12 to 40
            for (int len = PacketHeader.HeaderSize; len < tLen; len++)
            {
                bool threw = false;
                try
                {
                    var r = new PacketReader(tBuf.Slice(0, len));
                    TransformSyncPacket.Deserialize(ref r);
                }
                catch (InvalidOperationException)
                {
                    threw = true;
                }
                Assert.True(threw, $"TransformSyncPacket at length {len} should throw underflow");
            }

            // 2. ActionTriggerPacket (expected size = 33 bytes)
            var action = new ActionTriggerPacket
            {
                Header = new PacketHeader(PacketType.ActionTrigger, 1, 1, 100),
                Action = ActionType.SlashNormal, ActionSequenceId = 1,
                OriginX = 0f, OriginY = 0f, DirectionX = 1f, DirectionY = 0f, Parameter = 0
            };
            Span<byte> aBuf = stackalloc byte[64];
            var aw = new PacketWriter(aBuf);
            action.Serialize(ref aw);
            int aLen = aw.Position;

            for (int len = PacketHeader.HeaderSize; len < aLen; len++)
            {
                bool threw = false;
                try
                {
                    var r = new PacketReader(aBuf.Slice(0, len));
                    ActionTriggerPacket.Deserialize(ref r);
                }
                catch (InvalidOperationException)
                {
                    threw = true;
                }
                Assert.True(threw, $"ActionTriggerPacket at length {len} should throw underflow");
            }

            // 3. CurrencySyncPacket (expected size = 39 bytes)
            var currency = new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 1, 100),
                Currency = CurrencyType.Rosaries, TotalWalletAmount = 100, DeltaAmount = 10,
                PickupUniqueId = 55, TransactionId = Guid.NewGuid()
            };
            Span<byte> cBuf = stackalloc byte[64];
            var cw = new PacketWriter(cBuf);
            currency.Serialize(ref cw);
            int cLen = cw.Position;

            for (int len = PacketHeader.HeaderSize; len < cLen; len++)
            {
                bool threw = false;
                try
                {
                    var r = new PacketReader(cBuf.Slice(0, len));
                    CurrencySyncPacket.Deserialize(ref r);
                }
                catch (InvalidOperationException)
                {
                    threw = true;
                }
                Assert.True(threw, $"CurrencySyncPacket at length {len} should throw underflow");
            }

            // 4. VitalsSyncPacket (expected size = 17 bytes)
            var vitals = new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 1, 100),
                CurrentHealth = 5, CurrentSilk = 9, MaxHealth = 5, MaxSilkSpools = 3, NeedleUpgradeLevel = 1
            };
            Span<byte> vBuf = stackalloc byte[64];
            var vw = new PacketWriter(vBuf);
            vitals.Serialize(ref vw);
            int vLen = vw.Position;

            for (int len = PacketHeader.HeaderSize; len < vLen; len++)
            {
                bool threw = false;
                try
                {
                    var r = new PacketReader(vBuf.Slice(0, len));
                    VitalsSyncPacket.Deserialize(ref r);
                }
                catch (InvalidOperationException)
                {
                    threw = true;
                }
                Assert.True(threw, $"VitalsSyncPacket at length {len} should throw underflow");
            }

            // 5. DeathLifecyclePacket (expected size = 30 bytes with Epoch)
            var death = new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 1, 100),
                EventType = DeathEventType.SoloDeathOccurred, FallenPlayerIndex = 0,
                CoordinateX = 5f, CoordinateY = 5f, RemainingSeconds = 15f, BenchSceneHash = 111,
                Epoch = 1
            };
            Span<byte> dBuf = stackalloc byte[64];
            var dw = new PacketWriter(dBuf);
            death.Serialize(ref dw);
            int dLen = dw.Position;

            for (int len = PacketHeader.HeaderSize; len < dLen; len++)
            {
                bool threw = false;
                try
                {
                    var r = new PacketReader(dBuf.Slice(0, len));
                    DeathLifecyclePacket.Deserialize(ref r);
                }
                catch (InvalidOperationException)
                {
                    threw = true;
                }
                Assert.True(threw, $"DeathLifecyclePacket at length {len} should throw underflow");
            }
        }

        [Fact]
        public void RoomCodeCodec_ExhaustiveSingleBitFlips_All56BitsDetectedByCrc8()
        {
            var ip = IPAddress.Parse("172.16.42.100");
            int port = 7777;
            string validCode = RoomCodeCodec.Encode(ip, port);

            // Reconstruct the 7-byte raw payload
            byte[] ipBytes = ip.GetAddressBytes();
            byte[] raw = new byte[7];
            Array.Copy(ipBytes, 0, raw, 0, 4);
            raw[4] = (byte)((port >> 8) & 0xFF);
            raw[5] = (byte)(port & 0xFF);
            raw[6] = RoomCodeCodec.ComputeCrc8(raw.AsSpan(0, 6));

            // Systematically test every single bit flip across all 56 bits
            int detectedCount = 0;
            const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

            for (int byteIdx = 0; byteIdx < 7; byteIdx++)
            {
                for (int bitIdx = 0; bitIdx < 8; bitIdx++)
                {
                    byte[] corruptedRaw = (byte[])raw.Clone();
                    corruptedRaw[byteIdx] ^= (byte)(1 << bitIdx);

                    // Encode corrupted 56-bit payload into Crockford Base32
                    ulong val = 0;
                    for (int i = 0; i < 7; i++)
                    {
                        val = (val << 8) | corruptedRaw[i];
                    }
                    val <<= 4; // 60-bit alignment

                    char[] chars = new char[12];
                    for (int i = 11; i >= 0; i--)
                    {
                        chars[i] = CrockfordAlphabet[(int)(val & 0x1F)];
                        val >>= 5;
                    }

                    string corruptedCode = $"SK-{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}";

                    bool success = RoomCodeCodec.TryDecode(corruptedCode, out _, out _);
                    Assert.False(success, $"CRC-8 failed to catch bit flip at byte {byteIdx}, bit {bitIdx}");
                    detectedCount++;
                }
            }

            Assert.Equal(56, detectedCount);
        }

        [Theory]
        [InlineData("SK-!!!!-????-$$$$")]
        [InlineData("SK-1234-5678-90AB-CDEF")] // Too long
        [InlineData("SK-1234")]               // Too short
        [InlineData("SK-UUUU-UUUU-UUUU")]       // 'U' is invalid in Crockford
        [InlineData("SK-1234-5678-90A")]        // 11 chars
        [InlineData("SK-1234-5678-90ABC")]      // 13 chars
        [InlineData("SK-🦀⚔️-0000-0000")]       // Emojis / Unicode
        [InlineData("SK-\0\0\0\0-\0\0\0\0-\0\0\0\0")] // Null chars
        [InlineData("not a room code at all")]
        public void RoomCodeCodec_AdversarialInputs_NeverThrowAndReturnFalse(string malformedInput)
        {
            bool success = RoomCodeCodec.TryDecode(malformedInput, out IPAddress ip, out int port);
            Assert.False(success);
            Assert.Equal(IPAddress.None, ip);
            Assert.Equal(0, port);

            bool strSuccess = RoomCodeCodec.TryDecodeToString(malformedInput, out string ipStr, out int strPort);
            Assert.False(strSuccess);
            Assert.Empty(ipStr);
            Assert.Equal(0, strPort);
        }

        [Fact]
        public void RoomCodeCodec_NonZeroPadBits_StrictlyRejected()
        {
            // Construct a 12-character Crockford string where the lowest 4 bits (padding) are non-zero
            const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
            var ip = IPAddress.Parse("192.168.1.1");
            int port = 7777;
            string code = RoomCodeCodec.Encode(ip, port);

            // The last character encodes the lowest 5 bits (1 bit payload + 4 bits padding)
            // Manipulating the last character to introduce non-zero pad bits
            char lastChar = code[^1];
            int lastVal = CrockfordAlphabet.IndexOf(lastChar);

            // Toggle pad bits (bits 0..3)
            for (int pad = 1; pad < 16; pad++)
            {
                int corruptedLastVal = (lastVal & ~0x0F) | pad;
                if (corruptedLastVal < 32)
                {
                    char[] codeChars = code.ToCharArray();
                    codeChars[^1] = CrockfordAlphabet[corruptedLastVal];
                    string tampered = new string(codeChars);

                    bool success = RoomCodeCodec.TryDecode(tampered, out _, out _);
                    Assert.False(success, $"Padding bits {pad} should be rejected");
                }
            }
        }

        [Fact]
        public void FuzzTest_RandomByteStreams_NoUnhandledExceptions()
        {
            var rng = new Random(999);
            byte[] fuzzedBuffer = new byte[1024];

            for (int iteration = 0; iteration < 1000; iteration++)
            {
                int length = rng.Next(0, fuzzedBuffer.Length + 1);
                rng.NextBytes(fuzzedBuffer.AsSpan(0, length));

                ReadOnlySpan<byte> span = fuzzedBuffer.AsSpan(0, length);

                // 1. PacketHeader TryReadFrom
                bool hOk = PacketHeader.TryReadFrom(span, out var header);

                // 2. PacketReader TryReadHeader
                var reader = new PacketReader(span);
                bool rhOk = reader.TryReadHeader(out var rHeader);
                Assert.Equal(hOk, rhOk);

                // 3. If header read succeeded, try reading packet struct safely
                if (hOk)
                {
                    reader = new PacketReader(span);
                    try
                    {
                        switch (header.PacketId)
                        {
                            case PacketType.TransformSync:
                                TransformSyncPacket.Deserialize(ref reader);
                                break;
                            case PacketType.ActionTrigger:
                                ActionTriggerPacket.Deserialize(ref reader);
                                break;
                            case PacketType.VitalsSync:
                                VitalsSyncPacket.Deserialize(ref reader);
                                break;
                            case PacketType.CurrencySync:
                                CurrencySyncPacket.Deserialize(ref reader);
                                break;
                            case PacketType.PlayerDeath:
                                DeathLifecyclePacket.Deserialize(ref reader);
                                break;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // Cleanly handled buffer underflow
                    }
                }
            }
        }

        #endregion

        #region 2. MockNetworkTransport Adversarial Stress Testing

        [Fact]
        public void MockTransport_ExtremeStress_50PercentLoss_250msLatency_125msJitter_ReliableOrderedPreservesSequence()
        {
            const int packetCount = 100;
            const int latencyMs = 250;
            const int jitterMs = 125; // Delay ranges between 125ms and 375ms
            const float lossRate = 0.50f; // 50% packet loss

            var (host, client) = MockNetworkTransport.CreatePair(
                latencyMs: latencyMs,
                jitterMs: jitterMs,
                lossRate: lossRate,
                useVirtualTime: true,
                seed: 777);

            var receivedReliableOnClient = new List<int>();
            int unreliableReceivedCount = 0;
            var receivedSequencedOnClient = new List<int>();

            client.OnDataReceived += (peer, data, offset, length) =>
            {
                var reader = new PacketReader(data.AsSpan(offset, length));
                if (reader.TryReadHeader(out var header))
                {
                    int val = reader.ReadInt32();
                    switch (header.ChannelId)
                    {
                        case 0: // Unreliable channel
                            unreliableReceivedCount++;
                            break;
                        case 1: // ReliableOrdered channel
                            receivedReliableOnClient.Add(val);
                            break;
                        case 2: // Sequenced channel
                            receivedSequencedOnClient.Add(val);
                            break;
                    }
                }
            };

            // Host sends alternating packets across all 3 channels
            Span<byte> sendBuf = stackalloc byte[32];
            for (int i = 0; i < packetCount; i++)
            {
                // Channel 0: Unreliable (loss simulation active)
                var w0 = new PacketWriter(sendBuf);
                w0.WriteHeader(new PacketHeader(PacketType.TransformSync, 0, (uint)i, (uint)i * 10));
                w0.WriteInt32(i);
                host.Send(client.LocalPeerId, sendBuf.ToArray(), 0, w0.Position, DeliveryMethod.Unreliable, 0);

                // Channel 1: ReliableOrdered (must survive severe latency/jitter reordering)
                var w1 = new PacketWriter(sendBuf);
                w1.WriteHeader(new PacketHeader(PacketType.ActionTrigger, 1, (uint)i, (uint)i * 10));
                w1.WriteInt32(i);
                host.Send(client.LocalPeerId, sendBuf.ToArray(), 0, w1.Position, DeliveryMethod.ReliableOrdered, 1);

                // Channel 2: Sequenced (out-of-order packets must be dropped)
                var w2 = new PacketWriter(sendBuf);
                w2.WriteHeader(new PacketHeader(PacketType.VitalsSync, 2, (uint)i, (uint)i * 10));
                w2.WriteInt32(i);
                host.Send(client.LocalPeerId, sendBuf.ToArray(), 0, w2.Position, DeliveryMethod.Sequenced, 2);
            }

            // Simulate virtual time stepping in 10ms ticks for 1500 steps (15 seconds simulated)
            for (int step = 0; step < 1500; step++)
            {
                host.AdvanceVirtualTime(10);
                client.AdvanceVirtualTime(10);
                host.PollEvents();
                client.PollEvents();
            }

            // --- Assertions ---

            // 1. Channel 1 (ReliableOrdered): ALL 100 packets MUST arrive in strict ascending sequence
            Assert.Equal(packetCount, receivedReliableOnClient.Count);
            for (int i = 0; i < packetCount; i++)
            {
                Assert.Equal(i, receivedReliableOnClient[i]);
            }

            // 2. Channel 0 (Unreliable): Packet loss must have dropped packets (50% loss nominal)
            Assert.True(unreliableReceivedCount < packetCount, $"Unreliable received {unreliableReceivedCount}/{packetCount}; expected loss under 50% loss rate");
            Assert.True(unreliableReceivedCount > 10, $"Unreliable received {unreliableReceivedCount}; expected at least some packets to arrive");

            // 3. Channel 2 (Sequenced): Sequences received must be strictly monotonically increasing
            Assert.NotEmpty(receivedSequencedOnClient);
            for (int i = 1; i < receivedSequencedOnClient.Count; i++)
            {
                Assert.True(receivedSequencedOnClient[i] > receivedSequencedOnClient[i - 1],
                    $"Sequenced violation: index {i} value {receivedSequencedOnClient[i]} <= previous {receivedSequencedOnClient[i - 1]}");
            }
        }

        [Fact]
        public void MockTransport_MultiChannel_FullConcurrencyAndCrossTalkIsolation()
        {
            // Paired transports with 150ms latency, 80ms jitter, 30% loss
            var (host, client) = MockNetworkTransport.CreatePair(
                latencyMs: 150,
                jitterMs: 80,
                lossRate: 0.30f,
                useVirtualTime: true,
                seed: 12345);

            var ch1Packets = new List<int>(); // Combat actions
            var ch2Packets = new List<int>(); // Vitals
            var ch3Packets = new List<int>(); // Currency
            var ch4Packets = new List<int>(); // Death lifecycle
            int ch0Count = 0;                 // Transforms

            client.OnDataReceived += (peer, data, offset, length) =>
            {
                var reader = new PacketReader(data.AsSpan(offset, length));
                if (reader.TryReadHeader(out var header))
                {
                    int val = reader.ReadInt32();
                    switch (header.ChannelId)
                    {
                        case 0: ch0Count++; break;
                        case 1: ch1Packets.Add(val); break;
                        case 2: ch2Packets.Add(val); break;
                        case 3: ch3Packets.Add(val); break;
                        case 4: ch4Packets.Add(val); break;
                    }
                }
            };

            const int ch1Total = 30;
            const int ch2Total = 20;
            const int ch3Total = 25;
            const int ch4Total = 15;
            const int ch0Total = 80;

            Span<byte> buffer = stackalloc byte[32];

            // Host broadcasts across all 5 channels in interleaved fashion
            for (int i = 0; i < ch0Total; i++)
            {
                var w = new PacketWriter(buffer);
                w.WriteHeader(new PacketHeader(PacketType.TransformSync, 0, (uint)i, (uint)i));
                w.WriteInt32(i);
                host.Send(client.LocalPeerId, buffer.ToArray(), 0, w.Position, DeliveryMethod.Unreliable, 0);

                if (i < ch1Total)
                {
                    var w1 = new PacketWriter(buffer);
                    w1.WriteHeader(new PacketHeader(PacketType.ActionTrigger, 1, (uint)i, (uint)i));
                    w1.WriteInt32(i);
                    host.Send(client.LocalPeerId, buffer.ToArray(), 0, w1.Position, DeliveryMethod.ReliableOrdered, 1);
                }

                if (i < ch2Total)
                {
                    var w2 = new PacketWriter(buffer);
                    w2.WriteHeader(new PacketHeader(PacketType.VitalsSync, 2, (uint)i, (uint)i));
                    w2.WriteInt32(i);
                    host.Send(client.LocalPeerId, buffer.ToArray(), 0, w2.Position, DeliveryMethod.ReliableOrdered, 2);
                }

                if (i < ch3Total)
                {
                    var w3 = new PacketWriter(buffer);
                    w3.WriteHeader(new PacketHeader(PacketType.CurrencySync, 3, (uint)i, (uint)i));
                    w3.WriteInt32(i);
                    host.Send(client.LocalPeerId, buffer.ToArray(), 0, w3.Position, DeliveryMethod.ReliableOrdered, 3);
                }

                if (i < ch4Total)
                {
                    var w4 = new PacketWriter(buffer);
                    w4.WriteHeader(new PacketHeader(PacketType.PlayerDeath, 4, (uint)i, (uint)i));
                    w4.WriteInt32(i);
                    host.Send(client.LocalPeerId, buffer.ToArray(), 0, w4.Position, DeliveryMethod.ReliableOrdered, 4);
                }
            }

            // Advance virtual time to drain all queues
            for (int step = 0; step < 1200; step++)
            {
                host.AdvanceVirtualTime(10);
                client.AdvanceVirtualTime(10);
                host.PollEvents();
                client.PollEvents();
            }

            // Assert exact sequence and zero loss on all 4 reliable channels
            Assert.Equal(ch1Total, ch1Packets.Count);
            for (int i = 0; i < ch1Total; i++) Assert.Equal(i, ch1Packets[i]);

            Assert.Equal(ch2Total, ch2Packets.Count);
            for (int i = 0; i < ch2Total; i++) Assert.Equal(i, ch2Packets[i]);

            Assert.Equal(ch3Total, ch3Packets.Count);
            for (int i = 0; i < ch3Total; i++) Assert.Equal(i, ch3Packets[i]);

            Assert.Equal(ch4Total, ch4Packets.Count);
            for (int i = 0; i < ch4Total; i++) Assert.Equal(i, ch4Packets[i]);

            // Channel 0 suffered loss without stalling reliable channels
            Assert.True(ch0Count < ch0Total);
            Assert.True(ch0Count > 0);
        }

        [Fact]
        public void MockTransport_HighVolumeFloodStress_500Packets_ZeroCorruption()
        {
            var (host, client) = MockNetworkTransport.CreatePair(
                latencyMs: 100,
                jitterMs: 50,
                lossRate: 0.20f,
                useVirtualTime: true,
                seed: 8888);

            const int totalPackets = 500;
            var receivedReliable = new List<int>();

            client.OnDataReceived += (peer, data, offset, length) =>
            {
                var reader = new PacketReader(data.AsSpan(offset, length));
                if (reader.TryReadHeader(out var header) && header.ChannelId == 1)
                {
                    receivedReliable.Add(reader.ReadInt32());
                }
            };

            Span<byte> buffer = stackalloc byte[32];
            for (int i = 0; i < totalPackets; i++)
            {
                var w = new PacketWriter(buffer);
                w.WriteHeader(new PacketHeader(PacketType.ActionTrigger, 1, (uint)i, (uint)i));
                w.WriteInt32(i);
                host.Send(client.LocalPeerId, buffer.ToArray(), 0, w.Position, DeliveryMethod.ReliableOrdered, 1);
            }

            for (int step = 0; step < 2000; step++)
            {
                host.AdvanceVirtualTime(10);
                client.AdvanceVirtualTime(10);
                host.PollEvents();
                client.PollEvents();
            }

            Assert.Equal(totalPackets, receivedReliable.Count);
            for (int i = 0; i < totalPackets; i++)
            {
                Assert.Equal(i, receivedReliable[i]);
            }
        }

        #endregion
    }
}
