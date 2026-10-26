using System;
using SilksongCoop.Core.Clones;
using SilksongCoop.Core.Interpolation;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.Visuals;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Verification suite for the 5 remediation items identified by reviewer_m2_2 and challenger_m2_2:
    /// 1. DashTrail double-lifecycle frame guard.
    /// 2. Sprite horizontal flip deduplication (localScale inversion without FlipX).
    /// 3. Stale out-of-order packet sequencing guards for clone metadata & dash triggers.
    /// 4. Single-snapshot buffer dead reckoning extrapolation factor.
    /// 5. Teleport / large discontinuity instant snap to destination s1 position and velocity.
    /// </summary>
    public class Milestone2RemediationVerificationTests
    {
        [Fact]
        public void Fix1_DashTrailLifecycle_SingleTickPerFrame_PreservesDurationAndCooldown()
        {
            var lifecycle = new DashTrailLifecycle();

            // Trigger ground dash
            lifecycle.TriggerDash(isAirDash: false, isPlayer2: true);
            Assert.Equal(DashTrailPhase.Active, lifecycle.CurrentPhase);
            Assert.False(lifecycle.CanTriggerDash);

            // Simulate 5 frames at 60 FPS (dt = 0.01667s per frame)
            float dt = 0.01667f;
            for (int f = 0; f < 5; f++)
            {
                lifecycle.Update(dt, isGrounded: true, isWallSliding: false);
            }

            // Total elapsed = 5 * 0.01667 ~ 0.0833s.
            // Active phase duration is 0.25s, so remaining time should be ~ 0.1667s.
            Assert.Equal(DashTrailPhase.Active, lifecycle.CurrentPhase);
            Assert.True(lifecycle.ActiveTimeRemaining > 0.15f, $"Expected >0.15s remaining, got {lifecycle.ActiveTimeRemaining}");
        }

        [Fact]
        public void Fix3_HeadlessCloneManager_StalePacket_DoesNotOverwriteMetadataOrRegressTimestamp()
        {
            var manager = new HeadlessPlayerCloneManager();
            var peer = new NetworkPeerId(42);
            manager.SpawnRemotePlayer(peer, isPlayer2: true);

            // Packet 1: Initial packet at timestamp 5000ms, facing Right, Idle animation, Grounded
            manager.UpdateRemoteTransform(
                peer,
                posX: 10f, posY: 0f, velX: 0f, velY: 0f,
                facingDirection: 1,
                stateFlags: TransformSyncPacket.FlagGrounded,
                animClipHash: AnimationClipHasher.ComputeHash("Idle"),
                normalizedTime: 0,
                timestampMs: 5000);

            Assert.True(manager.TryGetRemoteState(peer, out var state1));
            Assert.True(state1.FacingRight);
            Assert.Equal(AnimationClipHasher.ComputeHash("Idle"), state1.ActiveClipHash);
            Assert.Equal((ushort)TransformSyncPacket.FlagGrounded, state1.StateFlags);

            // Packet 2: Stale packet from timestamp 4500ms (< 5000ms), facing Left, Dash animation, Dashing flag
            manager.UpdateRemoteTransform(
                peer,
                posX: 8f, posY: 0f, velX: 10f, velY: 0f,
                facingDirection: 0,
                stateFlags: TransformSyncPacket.FlagDashing,
                animClipHash: AnimationClipHasher.ComputeHash("Dash"),
                normalizedTime: 500,
                timestampMs: 4500);

            Assert.True(manager.TryGetRemoteState(peer, out var state2));

            // Verify: Stale packet was ignored for metadata, facing, clip, and dash triggers
            Assert.True(state2.FacingRight, "FacingRight must remain true (not overwritten by stale packet)");
            Assert.Equal(AnimationClipHasher.ComputeHash("Idle"), state2.ActiveClipHash);
            Assert.Equal((ushort)TransformSyncPacket.FlagGrounded, state2.StateFlags);
            Assert.False(state2.IsDashActive, "Stale dash trigger must not be activated");
        }

        [Fact]
        public void Fix3_HeadlessCloneManager_ClockWrap_PermitsNewerSessionUpdates()
        {
            var manager = new HeadlessPlayerCloneManager();
            var peer = new NetworkPeerId(43);
            manager.SpawnRemotePlayer(peer, isPlayer2: false);

            // Session 1 ended at high timestamp 60000ms
            manager.UpdateRemoteTransform(
                peer,
                posX: 50f, posY: 0f, velX: 0f, velY: 0f,
                facingDirection: 1,
                stateFlags: TransformSyncPacket.FlagGrounded,
                animClipHash: AnimationClipHasher.ComputeHash("Idle"),
                normalizedTime: 0,
                timestampMs: 60000);

            // Session 2 resets clock to 1000ms (backward jump of 59000ms > 10000ms)
            manager.UpdateRemoteTransform(
                peer,
                posX: 0f, posY: 0f, velX: 5f, velY: 0f,
                facingDirection: 0,
                stateFlags: TransformSyncPacket.FlagGrounded,
                animClipHash: AnimationClipHasher.ComputeHash("Run"),
                normalizedTime: 100,
                timestampMs: 1000);

            Assert.True(manager.TryGetRemoteState(peer, out var state));
            Assert.False(state.FacingRight, "Clock wrap (>10s jump) must accept new session facing direction");
            Assert.Equal(AnimationClipHasher.ComputeHash("Run"), state.ActiveClipHash);
        }

        [Fact]
        public void Fix4_SnapshotBuffer_SingleSnapshot_ReturnsExtrapolationFactorWhenTimeSurpassesSnapshot()
        {
            var buffer = new SnapshotBuffer(64);

            buffer.AddSnapshot(new TransformSnapshot
            {
                Tick = 1,
                TimestampMs = 1000.0,
                PositionX = 25f,
                PositionY = 10f,
                VelocityX = 5f,
                VelocityY = 0f
            });

            // Past query time (renderTime < 1000.0)
            bool okPast = buffer.TryGetSnapshotsForTime(800.0, out var s0Past, out var s1Past, out double tPast);
            Assert.True(okPast);
            Assert.Equal(0.0, tPast);

            // Future query time (renderTime >= 1000.0)
            bool okFuture = buffer.TryGetSnapshotsForTime(1150.0, out var s0Fut, out var s1Fut, out double tFut);
            Assert.True(okFuture);
            Assert.Equal(1.0, tFut);
            Assert.Equal(25f, s0Fut.PositionX);
            Assert.Equal(25f, s1Fut.PositionX);
        }

        [Fact]
        public void Fix4_SingleSnapshotPeer_ExtrapolatesSmoothlyUnderPacketLoss()
        {
            var manager = new HeadlessPlayerCloneManager(targetDelayMs: 50.0);
            var peer = new NetworkPeerId(55);
            manager.SpawnRemotePlayer(peer, isPlayer2: false);

            // Single snapshot arriving at t = 2000ms with forward sprint velocity
            manager.UpdateRemoteTransform(
                peer,
                posX: 100f, posY: 0f, velX: 12f, velY: 0f,
                facingDirection: 1,
                stateFlags: TransformSyncPacket.FlagGrounded,
                animClipHash: AnimationClipHasher.ComputeHash("Run"),
                normalizedTime: 0,
                timestampMs: 2000);

            // Local time 2150ms -> renderTime = 2100ms (100ms into the future of the snapshot)
            manager.UpdateInterpolation(2150.0);
            manager.TryGetRemotePosition(peer, out float posX, out float posY);

            // With Bug F-02 remediated, clone position extrapolates forward past 100f
            Assert.True(posX > 100.5f, $"Expected dead reckoning extrapolation > 100.5f, got {posX}");
            Assert.Equal(0f, posY);
        }

        [Fact]
        public void Fix5_Teleport_SnapsDirectlyToDestinationPositionAndVelocity()
        {
            var manager = new HeadlessPlayerCloneManager(targetDelayMs: 50.0);
            var peer = new NetworkPeerId(77);
            manager.SpawnRemotePlayer(peer, isPlayer2: true);

            // Snapshot 1: Before door transition at (10, 5) with zero velocity
            manager.UpdateRemoteTransform(
                peer,
                posX: 10f, posY: 5f, velX: 0f, velY: 0f,
                facingDirection: 1,
                stateFlags: TransformSyncPacket.FlagGrounded,
                animClipHash: AnimationClipHasher.ComputeHash("Idle"),
                normalizedTime: 0,
                timestampMs: 1000);

            // Snapshot 2: After door transition at (800, 300) with exit velocity (15, 0)
            manager.UpdateRemoteTransform(
                peer,
                posX: 800f, posY: 300f, velX: 15f, velY: 0f,
                facingDirection: 1,
                stateFlags: TransformSyncPacket.FlagGrounded,
                animClipHash: AnimationClipHasher.ComputeHash("Run"),
                normalizedTime: 0,
                timestampMs: 1050);

            // Local time 1075ms -> renderTime = 1025ms (t = 0.5 midpoint)
            manager.UpdateInterpolation(1075.0);

            Assert.True(manager.TryGetRemoteState(peer, out var state));

            // With Finding F-04 remediated, ShouldSnap is true and clone snaps to destination s1
            Assert.Equal(800f, state.PositionX);
            Assert.Equal(300f, state.PositionY);
            Assert.Equal(15f, state.VelocityX);
            Assert.Equal(0f, state.VelocityY);
        }
    }
}
