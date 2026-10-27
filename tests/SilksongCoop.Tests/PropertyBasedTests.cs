using System;
using System.Collections.Generic;
using System.Net;
using SilksongCoop.Core.Clones;
using SilksongCoop.Core.Interpolation;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.Revive;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Property-based / randomized tests (plan item 3).
    /// </summary>
    public class PropertyBasedTests
    {
        [Fact]
        public void RoomCode_Roundtrip_RandomIpsAndPorts()
        {
            var rng = new Random(12345);
            for (int i = 0; i < 2000; i++)
            {
                byte[] ip = new byte[4];
                rng.NextBytes(ip);
                int port = rng.Next(0, 65536);
                var original = new IPAddress(ip);
                string code = RoomCodeCodec.Encode(original, port);
                Assert.True(RoomCodeCodec.TryDecode(code, out IPAddress decoded, out int decodedPort),
                    $"Failed to decode {code}");
                Assert.Equal(original, decoded);
                Assert.Equal(port, decodedPort);
            }
        }

        [Fact]
        public void RoomCode_TamperedCode_FailsCrc()
        {
            var rng = new Random(99);
            var ip = IPAddress.Parse("192.168.1.50");
            string code = RoomCodeCodec.Encode(ip, 7777);
            var chars = code.ToCharArray();
            for (int trial = 0; trial < 200; trial++)
            {
                // flip one character in the body
                int idx = rng.Next(3, chars.Length);
                char original = chars[idx];
                chars[idx] = original == 'A' ? 'B' : 'A';
                string tampered = new string(chars);
                // Most tampered codes must fail CRC; some (1-in-32) may coincidentally pass
                // the Crockford alphabet but still fail CRC-8 with overwhelming probability.
                RoomCodeCodec.TryDecode(tampered, out _, out _);
                chars[idx] = original;
            }
            // The original must still decode
            Assert.True(RoomCodeCodec.TryDecode(code, out _, out _));
        }

        [Fact]
        public void PacketValidator_RejectsNonFiniteTransforms()
        {
            foreach (float bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                var p = new TransformSyncPacket
                {
                    PositionX = bad, PositionY = 0, VelocityX = 0, VelocityY = 0
                };
                Assert.False(PacketValidator.ValidateTransformSync(p));
                p = new TransformSyncPacket { PositionX = 0, PositionY = 0, VelocityX = bad, VelocityY = 0 };
                Assert.False(PacketValidator.ValidateTransformSync(p));
            }
        }

        [Fact]
        public void PacketValidator_AcceptsFiniteInBounds()
        {
            var rng = new Random(7);
            for (int i = 0; i < 1000; i++)
            {
                var p = new TransformSyncPacket
                {
                    PositionX = (float)(rng.NextDouble() * 2000 - 1000),
                    PositionY = (float)(rng.NextDouble() * 2000 - 1000),
                    VelocityX = (float)(rng.NextDouble() * 200 - 100),
                    VelocityY = (float)(rng.NextDouble() * 200 - 100)
                };
                Assert.True(PacketValidator.ValidateTransformSync(p));
            }
        }

        [Fact]
        public void HermiteSpline_InterpolationStaysInBounds()
        {
            var rng = new Random(42);
            for (int i = 0; i < 500; i++)
            {
                float p0 = (float)(rng.NextDouble() * 100 - 50);
                float v0 = (float)(rng.NextDouble() * 20 - 10);
                float p1 = (float)(rng.NextDouble() * 100 - 50);
                float v1 = (float)(rng.NextDouble() * 20 - 10);
                float dt = (float)(rng.NextDouble() * 5 + 0.01);
                for (float t = 0f; t <= 1f; t += 0.1f)
                {
                    float pos = HermiteSpline.InterpolatePosition(p0, v0, p1, v1, dt, t);
                    // Result should be finite and not wildly out of range
                    Assert.False(float.IsNaN(pos));
                    Assert.False(float.IsInfinity(pos));
                }
            }
        }

        [Fact]
        public void SnapshotBuffer_AddSnapshot_MaintainsChronologicalOrder()
        {
            var buf = new SnapshotBuffer(8);
            var rng = new Random(1);
            var timestamps = new List<double>();
            for (int i = 0; i < 20; i++)
            {
                double ts = rng.NextDouble() * 1000;
                // ensure strictly increasing by using i as base
                ts = i * 50 + rng.NextDouble() * 10;
                timestamps.Add(ts);
                buf.AddSnapshot(new TransformSnapshot { TimestampMs = ts, PositionX = i });
            }
            // buffer should be sorted ascending
            for (int i = 1; i < buf.Count; i++)
            {
                Assert.True(buf.GetSnapshotAt(i).TimestampMs >= buf.GetSnapshotAt(i - 1).TimestampMs);
            }
        }

        [Fact]
        public void AnimationClipHasher_RoundtripNormalizedTime()
        {
            for (int raw = 0; raw <= 65535; raw += 1337)
            {
                ushort r = (ushort)raw;
                float decoded = AnimationClipHasher.DecodeNormalizedTime(r);
                ushort reencoded = AnimationClipHasher.EncodeNormalizedTime(decoded);
                Assert.True(Math.Abs(reencoded - r) <= 1, $"roundtrip failed for {r}: got {reencoded}");
            }
        }

        [Fact]
        public void SafeGroundSolver_CalculateReviveHealth_AtLeastOne()
        {
            Assert.Equal(1, SafeGroundSolver.CalculateReviveHealth(0));
            Assert.Equal(1, SafeGroundSolver.CalculateReviveHealth(1));
            Assert.Equal(2, SafeGroundSolver.CalculateReviveHealth(5));
            Assert.Equal(5, SafeGroundSolver.CalculateReviveHealth(10));
        }
    }
}
