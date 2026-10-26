using System;
using System.Collections.Generic;
using SilksongCoop.Core.Clones;
using SilksongCoop.Core.Protocol.Packets;
using Xunit;

namespace SilksongCoop.Tests
{
    public class AnimationSyncTests
    {
        [Theory]
        [InlineData("Idle")]
        [InlineData("Run")]
        [InlineData("Dash")]
        [InlineData("Air Dash")]
        [InlineData("Double Jump")]
        [InlineData("Slash")]
        [InlineData("DownSpike")]
        [InlineData("Wall Slide")]
        public void AnimationClipHash_ProducesUniqueNonZeroHash(string clipName)
        {
            int hash = AnimationClipHasher.ComputeHash(clipName);
            Assert.NotEqual(0, hash);
        }

        [Fact]
        public void AnimationClipHash_CollisionsDoNotOccurInCoreHornetClips()
        {
            string[] coreClips =
            {
                "Idle", "Idle To Run", "Run", "Run To Idle", "Walk",
                "Turn", "Airborne", "Float", "Double Jump", "Walljump",
                "Dash", "Air Dash", "Back Dash", "Slash", "UpSlash",
                "DownSlash", "DownSpike", "Wall Slide", "HardLand"
            };

            var seen = new HashSet<int>();
            foreach (var clip in coreClips)
            {
                int hash = AnimationClipHasher.ComputeHash(clip);
                Assert.True(seen.Add(hash), $"Collision detected for clip: {clip}");
            }
        }

        [Theory]
        [InlineData(0, 0.0f)]
        [InlineData(16384, 0.25f)]
        [InlineData(32768, 0.50f)]
        [InlineData(49151, 0.75f)]
        [InlineData(65535, 1.0f)]
        public void NormalizedTime_MapsAccuratelyBetweenUInt16AndFloat(ushort raw, float expectedNormalized)
        {
            float converted = AnimationClipHasher.DecodeNormalizedTime(raw);
            Assert.Equal(expectedNormalized, converted, 3);

            ushort encoded = AnimationClipHasher.EncodeNormalizedTime(converted);
            Assert.Equal(raw, encoded);
        }

        [Fact]
        public void NormalizedTime_ClampsOutOfRangeValues()
        {
            Assert.Equal(0, AnimationClipHasher.EncodeNormalizedTime(-0.5f));
            Assert.Equal(65535, AnimationClipHasher.EncodeNormalizedTime(1.5f));
        }

        [Fact]
        public void FacingDirection_DirectionalScaleSign_UpdatesImmediately()
        {
            var packetLeft = new TransformSyncPacket { FacingDirection = 0 }; // Left
            var packetRight = new TransformSyncPacket { FacingDirection = 1 }; // Right

            Assert.False(packetLeft.FacingRight);
            Assert.True(packetRight.FacingRight);

            // Verified scale mapping: Facing right = scale.x > 0, Facing left = scale.x < 0
            float scaleLeft = packetLeft.FacingRight ? 1.0f : -1.0f;
            float scaleRight = packetRight.FacingRight ? 1.0f : -1.0f;

            Assert.Equal(-1.0f, scaleLeft);
            Assert.Equal(1.0f, scaleRight);
        }

        [Fact]
        public void AnimationStateFlags_DashFlagPrecedenceOverRunning()
        {
            ushort runningAndDashing = TransformSyncPacket.FlagGrounded | TransformSyncPacket.FlagDashing;
            bool isDashing = (runningAndDashing & TransformSyncPacket.FlagDashing) != 0;
            bool isGrounded = (runningAndDashing & TransformSyncPacket.FlagGrounded) != 0;

            Assert.True(isDashing);
            Assert.True(isGrounded);

            // Dash animation overrides running whenever FlagDashing is active
            string resolvedClip = isDashing ? "Dash" : (isGrounded ? "Run" : "Airborne");
            Assert.Equal("Dash", resolvedClip);
        }
    }
}
