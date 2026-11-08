using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests.State
{
    public class SaveScopeTrackerTests
    {
        [Fact]
        public void FirstObservation_SeedsWithoutReportingAChange()
        {
            var tracker = new SaveScopeTracker();

            Assert.False(tracker.Observe(1, new object()));
        }

        [Fact]
        public void SameProfileAndSameSaveObject_IsStable()
        {
            var tracker = new SaveScopeTracker();
            var playerData = new object();

            Assert.False(tracker.Observe(1, playerData));
            Assert.False(tracker.Observe(1, playerData));
        }

        [Fact]
        public void DifferentProfile_ReportsAChange()
        {
            var tracker = new SaveScopeTracker();
            var playerData = new object();

            Assert.False(tracker.Observe(1, playerData));
            Assert.True(tracker.Observe(2, playerData));
        }

        [Fact]
        public void ReloadedSaveInTheSameSlot_ReportsAChange()
        {
            var tracker = new SaveScopeTracker();

            Assert.False(tracker.Observe(1, new object()));
            Assert.True(tracker.Observe(1, new object()));
        }

        [Fact]
        public void MenuWithoutSave_DoesNotDiscardTheKnownScope()
        {
            var tracker = new SaveScopeTracker();
            var playerData = new object();

            Assert.False(tracker.Observe(1, playerData));
            Assert.False(tracker.Observe(0, null));
            Assert.False(tracker.Observe(1, playerData));
        }

        [Fact]
        public void LatePlayerDataLoad_DoesNotReportAScopeChange()
        {
            var tracker = new SaveScopeTracker();

            Assert.False(tracker.Observe(1, null));
            Assert.False(tracker.Observe(1, new object()));
        }

        [Fact]
        public void Reset_ForgetsTheScope()
        {
            var tracker = new SaveScopeTracker();
            var playerData = new object();

            Assert.False(tracker.Observe(1, playerData));
            tracker.Reset();
            Assert.False(tracker.Observe(2, playerData));
        }
    }
}
