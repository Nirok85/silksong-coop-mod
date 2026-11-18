using SilksongCoop.Core.Progression;
using Xunit;

namespace SilksongCoop.Tests.State
{
    public class FragmentBaselineTests
    {
        [Theory]
        [InlineData(5, 3, 6, 0)]
        [InlineData(6, 0, 5, 3)]
        public void HealthBaseline_MergesTotalProgressWithoutPhantomPieces(
            int localMax, int localPartial, int remoteMax, int remotePartial)
        {
            int earned = localMax;
            Assert.True(FragmentProgress.TryMergeBaseline(ref earned, localPartial,
                remoteMax, remotePartial, 4, localMax, localMax, 64, out var merged));
            Assert.Equal(6, earned);
            Assert.Equal(6, merged.Maximum);
            Assert.Equal(0, merged.Partial);
        }

        [Theory]
        [InlineData(9, 1, 10, 0)]
        [InlineData(10, 0, 9, 1)]
        public void SilkBaseline_UsesRawMaximumAndTwoPersistentParts(
            int localMax, int localPartial, int remoteMax, int remotePartial)
        {
            int earned = localMax;
            Assert.True(FragmentProgress.TryMergeBaseline(ref earned, localPartial,
                remoteMax, remotePartial, 2, localMax, localMax, 64, out var merged));
            Assert.Equal(10, earned);
            Assert.Equal(10, merged.Maximum);
            Assert.Equal(0, merged.Partial);
        }

        [Fact]
        public void VitalsBeforeCoherentBaseline_DoesNotFabricateOldPartialProgress()
        {
            int earned = 5;
            Assert.True(FragmentProgress.TryMergeBaseline(ref earned, 3,
                6, 0, 4, 6, 6, 64, out var merged));
            Assert.Equal(6, earned);
            Assert.Equal(6, merged.Maximum);
            Assert.Equal(0, merged.Partial);
        }

        [Fact]
        public void RepeatedBaseline_IsIdempotent()
        {
            int earned = 5;
            Assert.True(FragmentProgress.TryMergeBaseline(ref earned, 1,
                6, 2, 4, 5, 5, 64, out var merged));
            Assert.True(FragmentProgress.TryMergeBaseline(ref earned, merged.Partial,
                6, 2, 4, merged.Maximum, merged.Maximum, 64, out var replay));
            Assert.Equal(6, earned);
            Assert.Equal(6, replay.Maximum);
            Assert.Equal(2, replay.Partial);
            Assert.Equal(0, replay.CompletedUpgrades);
        }

        [Theory]
        [InlineData(4, 0)]
        [InlineData(0, 4)]
        [InlineData(-1, 0)]
        public void InvalidOrTransientPartial_DoesNotMutateEarnedBaseline(int local, int remote)
        {
            int earned = 5;
            Assert.False(FragmentProgress.TryMergeBaseline(ref earned, local,
                6, remote, 4, 5, 5, 64, out _));
            Assert.Equal(5, earned);
        }

        [Fact]
        public void FailedNativeApplication_CanRetryFromUncommittedEarnedCopy()
        {
            int committed = 5;
            int pending = committed;
            Assert.True(FragmentProgress.TryApply(ref pending, 3, 1, 4, 5, 5, 64, out var first));
            Assert.Equal(6, first.Maximum);
            // The adapter has already changed the native maximum, but a later step failed.
            // Its earned value remains uncommitted; retry targets the same maximum.
            Assert.Equal(5, committed);
            pending = committed;
            Assert.True(FragmentProgress.TryApply(ref pending, 3, 1, 4, 6, 6, 64, out var retry));
            committed = pending;
            Assert.Equal(6, committed);
            Assert.Equal(6, retry.Maximum);
            Assert.Equal(0, retry.Partial);
        }
    }
}
