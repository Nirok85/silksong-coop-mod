using System;
using SilksongCoop.Core.Clones;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.State;
using SilksongCoop.Tests.TestHarness;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Tests for max-stats single source (plan item 10) and clone scene handling (item 13).
    /// </summary>
    public class MaxStatsAndCloneSceneTests
    {
        [Fact]
        public void MaxHealth_UpgradeOnOneSide_PropagatesToBoth()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, 0, 0);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, 0, 0);

            harness.HostVitals.OnLocalMaxHealthUpgrade(7);
            harness.DeliverAll();

            Assert.Equal(7, harness.HostVitals.SharedMaxHealth);
            Assert.Equal(7, harness.ClientVitals.SharedMaxHealth);
        }

        [Fact]
        public void MaxHealth_RemoteHigher_UpdatesLocalShared()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, 0, 0);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, 0, 0);

            // Client upgrades to 8 (higher than host's 5)
            harness.ClientVitals.OnLocalMaxHealthUpgrade(8);
            harness.DeliverAll();

            Assert.Equal(8, harness.HostVitals.SharedMaxHealth);
            Assert.Equal(8, harness.ClientVitals.SharedMaxHealth);
        }

        [Fact]
        public void MaxHealth_LowerRemote_DoesNotDowngrade()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(8, 8, 9, 1, 0, 0, 0);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, 0, 0);

            // Client's lower max should not downgrade host's higher max
            harness.ClientVitals.BroadcastCurrentVitals();
            harness.DeliverAll();

            Assert.Equal(8, harness.HostVitals.SharedMaxHealth);
        }

        [Fact]
        public void NeedleUpgrade_PropagatesToBoth()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, 0, 0);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, 0, 0);

            harness.HostVitals.OnLocalNeedleUpgrade(3);
            harness.DeliverAll();

            Assert.Equal(3, harness.HostVitals.SharedNeedleUpgradeLevel);
            Assert.Equal(3, harness.ClientVitals.SharedNeedleUpgradeLevel);
        }

        [Fact]
        public void SilkSpools_UpgradePropagatesToBoth()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 0, 0, 0);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 0, 0, 0);

            harness.ClientVitals.OnLocalMaxSilkUpgrade(3);
            harness.DeliverAll();

            Assert.Equal(3, harness.HostVitals.SharedMaxSilkSpools);
            Assert.Equal(3, harness.ClientVitals.SharedMaxSilkSpools);
        }

        [Fact]
        public void CloneScene_OlderTransformsCannotRestorePresenceAfterDeparture()
        {
            var scene = new SilksongCoop.Core.State.RemoteSceneSequence();
            Assert.True(scene.AcceptTransform(5, 20));
            Assert.True(scene.AcceptDeparture(6));
            Assert.False(scene.HasTransform);
            Assert.False(scene.AcceptTransform(5, 21));
            Assert.False(scene.HasTransform);
            Assert.True(scene.AcceptTransform(6, 22));
            Assert.True(scene.HasTransform);
            Assert.False(scene.AcceptDeparture(6));
            Assert.False(scene.AcceptTransform(6, 21));

        }
    }
}
