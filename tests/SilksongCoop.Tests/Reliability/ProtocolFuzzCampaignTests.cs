using System;
using System.Net;
using System.Text;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using Xunit;

namespace SilksongCoop.Tests.Reliability
{
    /// <summary>
    /// Deterministic fuzz campaigns for the wire parser and room-code codec. These run in
    /// the normal test suite (no external fuzzing tool) with a fixed seed, so a failure is
    /// reproducible. Random bytes are allowed to fail parsing; they must never escape as
    /// an unexpected exception type or corrupt the decoder state.
    /// </summary>
    public class ProtocolFuzzCampaignTests
    {
        private static readonly PacketType[] KnownTypes =
        {
            PacketType.TransformSync,
            PacketType.ActionTrigger,
            PacketType.VitalsSync,
            PacketType.CurrencySync,
            PacketType.ItemUnlockSync,
            PacketType.PlayerDeath,
            PacketType.BenchCheckpointSync,
            PacketType.SceneTransition,
            PacketType.EnemySync,
            PacketType.EnemySnapshot,
            PacketType.WorldEvent,
            PacketType.PersistentState,
            PacketType.QuestState
        };

        /// <summary>
        /// Default campaign size for a normal test run. The nightly workflow sets
        /// SILKSONG_FUZZ_ITERATIONS to exercise the same deterministic seed for longer.
        /// </summary>
        private static int Iterations(int defaultCount)
        {
            string? raw = Environment.GetEnvironmentVariable("SILKSONG_FUZZ_ITERATIONS");
            return int.TryParse(raw, out int configured)
                ? Math.Clamp(configured, 1_000, 2_000_000)
                : defaultCount;
        }

        [Fact]
        public void PacketDeserializers_FuzzedHeadersAndBodies_OnlyCleanUnderflowEscapes()
        {
            int iterations = Iterations(20_000);
            var random = new Random(20261109);
            byte[] buffer = new byte[512];

            for (int iteration = 0; iteration < iterations; iteration++)
            {
                int length = random.Next(0, buffer.Length + 1);
                random.NextBytes(buffer.AsSpan(0, length));

                // Half of the inputs get a structurally valid header so the body
                // deserializers are actually reached instead of being rejected by magic.
                if (iteration % 2 == 0 && length >= PacketHeader.HeaderSize)
                {
                    var header = new PacketHeader(
                        KnownTypes[random.Next(KnownTypes.Length)],
                        (byte)random.Next(0, 8),
                        (uint)random.Next(),
                        (uint)random.Next());
                    header.WriteTo(buffer);
                }

                var span = buffer.AsSpan(0, length);
                if (!PacketHeader.TryReadFrom(span, out var parsed)) continue;
                if (!parsed.IsValidPacketType) continue;

                try
                {
                    Deserialize(parsed.PacketId, span);
                }
                catch (InvalidOperationException)
                {
                    // Expected for a truncated body.
                }
                catch (FormatException)
                {
                    // Expected for a structurally invalid body, such as a count
                    // outside the protocol's declared range.
                }
            }
        }

        [Fact]
        public void RoomCodeDecoder_RandomStrings_NeverThrowsAndRoundTripsValidInput()
        {
            int iterations = Iterations(20_000);
            var random = new Random(77123);
            const string alphabet = "SK-0123456789ABCDEFGHJKMNPQRSTVWXYZabcdefghjkmnpqrstvwxyz !@#$%^&*()_+";

            for (int iteration = 0; iteration < iterations; iteration++)
            {
                int length = random.Next(0, 48);
                var chars = new char[length];
                for (int i = 0; i < length; i++)
                    chars[i] = alphabet[random.Next(alphabet.Length)];

                string input = new string(chars);
                var ex = Record.Exception(() =>
                {
                    RoomCodeCodec.TryCanonicalize(input, out _);
                    RoomCodeCodec.TryDecode(input, out _, out _);
                    RoomCodeCodec.TryDecodeToString(input, out _, out _);
                    RoomCodeCodec.IsDeprecatedCode(input);
                });

                Assert.Null(ex);
            }

            int roundtripIterations = Math.Max(2_000, iterations / 10);
            for (int iteration = 0; iteration < roundtripIterations; iteration++)
            {
                var addressBytes = new byte[4];
                random.NextBytes(addressBytes);
                var ip = new IPAddress(addressBytes);
                int port = random.Next(0, 65_536);

                string code = RoomCodeCodec.Encode(ip, port);
                Assert.True(RoomCodeCodec.TryDecode(code, out var decodedIp, out int decodedPort),
                    $"valid code failed to decode: {code}");
                Assert.Equal(ip, decodedIp);
                Assert.Equal(port, decodedPort);

                Assert.True(RoomCodeCodec.TryCanonicalize(code, out string canonical));
                Assert.True(RoomCodeCodec.TryDecode(canonical, out _, out _));
            }
        }

        [Fact]
        public void PacketReader_RandomStrings_TruncationNeverEscapesAsUnexpectedType()
        {
            int iterations = Iterations(10_000);
            var random = new Random(5150);
            byte[] buffer = new byte[256];

            for (int iteration = 0; iteration < iterations; iteration++)
            {
                int length = random.Next(0, buffer.Length + 1);
                random.NextBytes(buffer.AsSpan(0, length));

                try
                {
                    var reader = new PacketReader(buffer.AsSpan(0, length));
                    _ = reader.ReadString();
                    _ = reader.ReadGuid();
                }
                catch (InvalidOperationException)
                {
                    // Clean bounds failure.
                }
            }
        }

        private static void Deserialize(PacketType type, ReadOnlySpan<byte> span)
        {
            var reader = new PacketReader(span);
            switch (type)
            {
                case PacketType.TransformSync:
                    PacketValidator.ValidateTransformSync(TransformSyncPacket.Deserialize(ref reader));
                    break;
                case PacketType.ActionTrigger:
                    ActionTriggerPacket.Deserialize(ref reader);
                    break;
                case PacketType.VitalsSync:
                    PacketValidator.ValidateVitalsSync(VitalsSyncPacket.Deserialize(ref reader));
                    break;
                case PacketType.CurrencySync:
                    PacketValidator.ValidateCurrencySync(CurrencySyncPacket.Deserialize(ref reader));
                    break;
                case PacketType.ItemUnlockSync:
                    ItemUnlockPacket.Deserialize(ref reader);
                    break;
                case PacketType.PlayerDeath:
                    DeathLifecyclePacket.Deserialize(ref reader);
                    break;
                case PacketType.BenchCheckpointSync:
                    BenchCheckpointSyncPacket.Deserialize(ref reader);
                    break;
                case PacketType.SceneTransition:
                    SceneTransitionPacket.Deserialize(ref reader);
                    break;
                case PacketType.EnemySync:
                    PacketValidator.ValidateEnemySync(EnemySyncPacket.Deserialize(ref reader));
                    break;
                case PacketType.EnemySnapshot:
                    EnemySnapshotPacket.Deserialize(ref reader);
                    break;
                case PacketType.WorldEvent:
                    WorldEventPacket.Deserialize(ref reader);
                    break;
                case PacketType.PersistentState:
                    PersistentStatePacket.Deserialize(ref reader);
                    break;
                case PacketType.QuestState:
                    QuestStatePacket.Deserialize(ref reader);
                    break;
            }
        }
    }
}
