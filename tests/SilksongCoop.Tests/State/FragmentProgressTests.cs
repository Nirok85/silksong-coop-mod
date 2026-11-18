using SilksongCoop.Core.Progression;
using Xunit;

namespace SilksongCoop.Tests.State
{
    public class FragmentProgressTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void IndependentLocalAndRemoteMasks_AreBothPreserved(bool localFirst)
        {
            int earned = 5;
            int current = 5;
            if (localFirst)
            {
                current++;
                Assert.True(FragmentProgress.NoteLocal(ref earned, 1, current, 40));
            }
            Assert.True(FragmentProgress.TryApply(ref earned, 3, 1, 4, current, 6, 40, out var update));
            current = update.Maximum;
            if (!localFirst)
            {
                current++;
                Assert.True(FragmentProgress.NoteLocal(ref earned, 1, current, 40));
            }
            Assert.Equal(7, earned);
            Assert.Equal(7, current);
            Assert.Equal(0, update.Partial);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SameUpgradeViaSnapshotAndFragments_IsAppliedOnce(bool snapshotFirst)
        {
            int earned = 5;
            int current = snapshotFirst ? 6 : 5;
            Assert.True(FragmentProgress.TryApply(ref earned, 3, 1, 4,
                current, snapshotFirst ? 6 : 5, 40, out var update));
            Assert.Equal(6, earned);
            Assert.Equal(6, update.Maximum);
            Assert.Equal(0, update.Partial);
            Assert.True(FragmentProgress.TryApply(ref earned, 0, 0, 4,
                update.Maximum, 6, 40, out var afterSnapshot));
            Assert.Equal(6, afterSnapshot.Maximum);
        }

        [Fact]
        public void RawSilkCapacity_NinePlusTwoPersistentParts_BecomesTen()
        {
            int earned = 9;
            Assert.True(FragmentProgress.TryApply(ref earned, 1, 1, 2, 9, 9, 40, out var update));
            Assert.Equal(10, earned);
            Assert.Equal(10, update.Maximum);
            Assert.Equal(0, update.Partial);
            Assert.Equal(1, update.CompletedUpgrades);
        }

        [Fact]
        public void NativeReset_DoesNotRemoveRemotePartialOrCredits()
        {
            int earned = 6;
            Assert.True(FragmentProgress.TryApply(ref earned, 1, -4, 4, 6, 6, 40, out var update));
            Assert.Equal(6, earned);
            Assert.Equal(1, update.Partial);
            Assert.Equal(0, update.CompletedUpgrades);
        }

        [Theory]
        [InlineData(4, 4)]
        [InlineData(2, 2)]
        public void InFlightNativeConversion_IsRejectedWithoutMutation(int partial, int units)
        {
            int earned = 5;
            Assert.False(FragmentProgress.TryApply(ref earned, partial, 1, units, 5, 5, 40, out _));
            Assert.Equal(5, earned);
        }

        [Theory]
        [InlineData(-9)]
        [InlineData(9)]
        [InlineData(int.MaxValue)]
        [InlineData(int.MinValue)]
        public void OversizedDelta_IsRejectedWithoutMutation(int delta)
        {
            int earned = 5;
            Assert.False(FragmentProgress.TryApply(ref earned, 1, delta, 4, 5, 5, 40, out _));
            Assert.Equal(5, earned);
        }

        [Fact]
        public void SnapshotDoesNotInflateRecordedLocalEarnedAmount()
        {
            int earned = 5;
            Assert.True(FragmentProgress.NoteLocal(ref earned, 1, 7, 40));
            Assert.Equal(6, earned);
            Assert.True(FragmentProgress.TryApply(ref earned, 3, 1, 4, 7, 6, 40, out var update));
            Assert.Equal(7, earned);
            Assert.Equal(7, update.Maximum);
        }

        [Fact]
        public void LocalInitialization_UsesPreAwardMaximum()
        {
            int earned = -1;
            Assert.True(FragmentProgress.NoteLocal(ref earned, 1, 6, 40));
            Assert.Equal(6, earned);
        }

        [Fact]
        public void UninitializedRemoteApplication_IsRejected()
        {
            int earned = -1;
            Assert.False(FragmentProgress.TryApply(ref earned, 3, 1, 4, 5, 5, 40, out _));
            Assert.Equal(-1, earned);
        }

        [Fact]
        public void AtMaximum_ExtraFragmentsCannotOverflowEarnedMaximum()
        {
            int earned = int.MaxValue;
            Assert.True(FragmentProgress.TryApply(ref earned, 3, 8, 4,
                int.MaxValue, int.MaxValue, int.MaxValue, out var update));
            Assert.Equal(int.MaxValue, earned);
            Assert.Equal(int.MaxValue, update.Maximum);
            Assert.Equal(3, update.Partial);
        }
    }
}
