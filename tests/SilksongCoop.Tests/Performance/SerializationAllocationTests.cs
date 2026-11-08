using System;
using SilksongCoop.Core.Interpolation;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests.Performance
{
    /// <summary>
    /// Allocation regression budgets for the wire and state hot paths. The assertions are
    /// intentionally exact (0 bytes) after a warm-up: these paths must not create garbage
    /// per packet/frame. If a future change needs a one-time allocation, the warm-up covers
    /// it; a per-call allocation is a real regression.
    /// </summary>
    public class SerializationAllocationTests
    {
        private const int WarmupIterations = 1000;
        private const int MeasuredIterations = 1000;

        [Fact]
        public void PacketHeader_WriteAndRead_AllocatesNothing()
        {
            Span<byte> buffer = stackalloc byte[PacketHeader.HeaderSize];
            var header = new PacketHeader(PacketType.TransformSync, 0, 42, 123456);

            for (int i = 0; i < WarmupIterations; i++)
            {
                header.WriteTo(buffer);
                Assert.True(PacketHeader.TryReadFrom(buffer, out _));
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            int sink = 0;
            for (int i = 0; i < MeasuredIterations; i++)
            {
                header.WriteTo(buffer);
                if (PacketHeader.TryReadFrom(buffer, out var parsed))
                    sink += parsed.ChannelId + 1;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
            Assert.Equal(MeasuredIterations, sink);
        }

        [Fact]
        public void TransformSyncPacket_SerializeDeserialize_AllocatesNothing()
        {
            Span<byte> buffer = stackalloc byte[64];
            var packet = new TransformSyncPacket
            {
                Header = new PacketHeader(PacketType.TransformSync, 0, 7, 999),
                PositionX = 12.5f,
                PositionY = -3.25f,
                VelocityX = 4f,
                VelocityY = -1f,
                FacingDirection = 1,
                StateFlags = TransformSyncPacket.FlagGrounded,
                AnimationClipHash = 1234,
                NormalizedTime = 500,
                SceneNameHash = 77
            };

            for (int i = 0; i < WarmupIterations; i++)
                Roundtrip(packet, buffer);

            long before = GC.GetAllocatedBytesForCurrentThread();
            int sink = 0;
            for (int i = 0; i < MeasuredIterations; i++)
            {
                var decoded = Roundtrip(packet, buffer);
                sink += decoded.AnimationClipHash & 1;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
            Assert.Equal(0, sink);
        }

        [Fact]
        public void EnemySyncService_SteadyStateHits_AllocateNothing()
        {
            var service = new EnemySyncService();
            const ulong key = 0x1234;
            int hp = 100_000;

            for (int i = 0; i < WarmupIterations; i++)
            {
                Assert.True(service.TryAcceptHit(key, --hp, dead: false));
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < MeasuredIterations; i++)
            {
                Assert.True(service.TryAcceptHit(key, --hp, dead: false));
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
            Assert.Equal(1, service.TrackedCount);
        }

        [Fact]
        public void SnapshotBuffer_AddAndRead_AllocatesNothing()
        {
            var buffer = new SnapshotBuffer(64);
            var snapshot = new TransformSnapshot
            {
                TimestampMs = 0,
                PositionX = 1,
                PositionY = 2,
                VelocityX = 0,
                VelocityY = 0
            };

            for (int i = 0; i < WarmupIterations; i++)
            {
                snapshot.TimestampMs = i;
                buffer.AddSnapshot(snapshot);
                Assert.True(buffer.TryGetLatest(out _));
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            int sink = 0;
            for (int i = 0; i < MeasuredIterations; i++)
            {
                snapshot.TimestampMs = WarmupIterations + i;
                buffer.AddSnapshot(snapshot);
                if (buffer.TryGetSnapshotsForTime(snapshot.TimestampMs, out _, out _, out _))
                    sink++;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
            Assert.Equal(MeasuredIterations, sink);
        }

        private static TransformSyncPacket Roundtrip(
            TransformSyncPacket packet,
            Span<byte> buffer)
        {
            var writer = new PacketWriter(buffer);
            packet.Serialize(ref writer);
            var reader = new PacketReader(buffer.Slice(0, writer.Position));
            return TransformSyncPacket.Deserialize(ref reader);
        }
    }
}
