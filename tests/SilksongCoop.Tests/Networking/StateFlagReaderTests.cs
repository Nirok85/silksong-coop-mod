using System;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;
using Xunit;

namespace SilksongCoop.Tests.Networking
{
    public class StateFlagReaderTests
    {
        public sealed class PropertyState
        {
            public bool onGround { get; set; }
            public bool dashing { get; set; }
            public bool airDashing { get; set; }
            public bool shadowDashing { get; set; }
            public bool superDashing { get; set; }
            public bool wallSliding { get; set; }
            public bool jumping { get; set; }
            public bool doubleJumping { get; set; }
            public bool wallJumping { get; set; }
            public bool falling { get; set; }
            public bool attacking { get; set; }
            public bool invulnerable { get; set; }
            public bool swimming { get; set; }
        }

        public sealed class FieldState
        {
            public bool onGround;
            public bool dashing;
            public bool jumping;
            public bool falling;
            public bool attacking;
            public bool invulnerable;
            public bool swimming;
        }

        public sealed class EmptyState
        {
            public int unrelated;
        }

        public sealed class OtherState
        {
            public bool onGround { get; set; }
            public bool swimming { get; set; }
        }

        [Fact]
        public void Build_AllProperties_MapsEveryFlag()
        {
            var reader = new StateFlagReader();
            var state = new PropertyState
            {
                onGround = true,
                dashing = true,
                wallSliding = true,
                jumping = true,
                falling = true,
                attacking = true,
                invulnerable = true,
                swimming = true
            };

            ushort flags = reader.Build(state);

            Assert.True((flags & TransformSyncPacket.FlagGrounded) != 0);
            Assert.True((flags & TransformSyncPacket.FlagDashing) != 0);
            Assert.True((flags & TransformSyncPacket.FlagWallSliding) != 0);
            Assert.True((flags & TransformSyncPacket.FlagJumping) != 0);
            Assert.True((flags & TransformSyncPacket.FlagFalling) != 0);
            Assert.True((flags & TransformSyncPacket.FlagAttacking) != 0);
            Assert.True((flags & TransformSyncPacket.FlagInvulnerable) != 0);
            Assert.True((flags & TransformSyncPacket.FlagSwimming) != 0);
        }

        [Fact]
        public void Build_PublicFields_AreSupported()
        {
            var reader = new StateFlagReader();
            var state = new FieldState
            {
                onGround = true,
                dashing = true,
                jumping = true,
                falling = true
            };

            ushort flags = reader.Build(state);

            Assert.True((flags & TransformSyncPacket.FlagGrounded) != 0);
            Assert.True((flags & TransformSyncPacket.FlagDashing) != 0);
            Assert.True((flags & TransformSyncPacket.FlagJumping) != 0);
            Assert.True((flags & TransformSyncPacket.FlagFalling) != 0);
            Assert.False((flags & TransformSyncPacket.FlagAttacking) != 0);
        }

        [Fact]
        public void Build_UnknownState_ReturnsZero()
        {
            var reader = new StateFlagReader();
            Assert.Equal(0, reader.Build(new EmptyState()));
            Assert.Equal(0, reader.Build(null!));
        }

        [Fact]
        public void Build_StateTypeChange_ReResolvesAccessors()
        {
            var reader = new StateFlagReader();
            var a = new PropertyState { onGround = true };
            var b = new OtherState { swimming = true };

            Assert.True((reader.Build(a) & TransformSyncPacket.FlagGrounded) != 0);
            Assert.True((reader.Build(b) & TransformSyncPacket.FlagSwimming) != 0);
            Assert.False((reader.Build(b) & TransformSyncPacket.FlagGrounded) != 0);
        }

        [Fact]
        public void Build_AfterWarmup_DoesNotAllocate()
        {
            var reader = new StateFlagReader();
            var state = new PropertyState { onGround = true, dashing = true };

            for (int i = 0; i < 1000; i++)
                _ = reader.Build(state);

            long before = GC.GetAllocatedBytesForCurrentThread();
            int sink = 0;
            for (int i = 0; i < 1000; i++)
                sink += reader.Build(state);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
            Assert.True(sink > 0);
        }
    }
}
