using SilksongCoop.Core.Interpolation;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// When the sender's game clock is frozen, several transform packets share one
    /// timestamp. The sequence must let the newest state replace the buffered snapshot.
    /// </summary>
    public class SnapshotFrozenClockTests
    {
        [Fact]
        public void NewerSequence_WithSameTimestamp_ReplacesLatestSnapshot()
        {
            var buffer = new SnapshotBuffer(8);

            buffer.AddSnapshot(new TransformSnapshot
            {
                Tick = 10,
                TimestampMs = 1000.0,
                PositionX = 5f,
                FacingRight = true
            });
            buffer.AddSnapshot(new TransformSnapshot
            {
                Tick = 11,
                TimestampMs = 1000.0,
                PositionX = 5.5f,
                FacingRight = false
            });

            Assert.Equal(1, buffer.Count);
            Assert.True(buffer.TryGetLatest(out var latest));
            Assert.Equal(5.5f, latest.PositionX);
            Assert.False(latest.FacingRight);
        }

        [Fact]
        public void OlderSequence_WithSameTimestamp_IsIgnored()
        {
            var buffer = new SnapshotBuffer(8);

            buffer.AddSnapshot(new TransformSnapshot { Tick = 20, TimestampMs = 2000.0, PositionX = 20f });
            buffer.AddSnapshot(new TransformSnapshot { Tick = 19, TimestampMs = 2000.0, PositionX = 19f });

            Assert.Equal(1, buffer.Count);
            Assert.True(buffer.TryGetLatest(out var latest));
            Assert.Equal(20f, latest.PositionX);
        }

        [Fact]
        public void IdenticalDuplicate_DoesNotGrowBuffer()
        {
            var buffer = new SnapshotBuffer(8);
            var snapshot = new TransformSnapshot { Tick = 30, TimestampMs = 3000.0, PositionX = 30f };

            buffer.AddSnapshot(snapshot);
            buffer.AddSnapshot(snapshot);
            buffer.AddSnapshot(snapshot);

            Assert.Equal(1, buffer.Count);
        }

        [Fact]
        public void TimestampAdvance_StillAppendsNewSnapshot()
        {
            var buffer = new SnapshotBuffer(8);

            buffer.AddSnapshot(new TransformSnapshot { Tick = 40, TimestampMs = 4000.0, PositionX = 40f });
            buffer.AddSnapshot(new TransformSnapshot { Tick = 41, TimestampMs = 4033.0, PositionX = 41f });

            Assert.Equal(2, buffer.Count);
        }
    }
}
