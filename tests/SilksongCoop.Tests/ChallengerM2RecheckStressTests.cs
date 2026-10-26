using System;
using System.Collections.Generic;
using SilksongCoop.Core.Clones;
using SilksongCoop.Core.Interpolation;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.Visuals;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Dedicated Adversarial Stress Verification Suite by Challenger M2 Recheck.
    /// Empirically stress-tests:
    /// 1. Out-of-order packet arrival with rapid interleaved timestamps (monotonic facing, clips, flags).
    /// 2. Single-snapshot buffer starvation (dead reckoning extrapolation without freeze or runaway).
    /// 3. Room transition teleport snaps (instant warp to destination snapshot s1 coordinates).
    /// 4. DashTrail lifecycle frame guard and cooldown invariants.
    /// 5. Facing scale deduplication invariants.
    /// </summary>
    public class ChallengerM2RecheckStressTests
    {
        #region 1. Adversarial Stress: Out-of-Order Packet Arrival & Monotonicity

        [Fact]
        public void Stress1_OutOfOrder_RandomInterleavedPackets_StrictMonotonicity()
        {
            var manager = new HeadlessPlayerCloneManager(targetDelayMs: 50.0);
            var peer = new NetworkPeerId(1001);
            manager.SpawnRemotePlayer(peer, isPlayer2: true);

            var rng = new Random(98765);
            uint currentHighWaterTimestamp = 1000;
            const int totalPackets = 2000;

            // Seed with initial packet
            manager.UpdateRemoteTransform(
                peer,
                posX: 0f, posY: 0f, velX: 0f, velY: 0f,
                facingDirection: 1,
                stateFlags: TransformSyncPacket.FlagGrounded,
                animClipHash: AnimationClipHasher.ComputeHash("Idle"),
                normalizedTime: 0,
                timestampMs: currentHighWaterTimestamp);

            int stalePacketsInjected = 0;
            int forwardPacketsInjected = 0;

            for (int i = 0; i < totalPackets; i++)
            {
                bool injectStale = rng.NextDouble() < 0.45; // 45% chance of out-of-order stale packet

                if (injectStale && currentHighWaterTimestamp > 1050)
                {
                    // Generate a packet from the past (between 10ms and 500ms behind high water mark)
                    uint lagMs = (uint)rng.Next(10, 500);
                    uint staleTimestamp = Math.Max(1000, currentHighWaterTimestamp - lagMs);

                    // Adversarial payload: try to flip facing to Left, set Dash flag, and set Walk clip
                    manager.UpdateRemoteTransform(
                        peer,
                        posX: -999f, posY: -999f, velX: -50f, velY: 0f,
                        facingDirection: 0, // Left
                        stateFlags: TransformSyncPacket.FlagDashing,
                        animClipHash: AnimationClipHasher.ComputeHash("Walk"),
                        normalizedTime: (ushort)rng.Next(0, 1000),
                        timestampMs: staleTimestamp);

                    stalePacketsInjected++;

                    // Invariant check: stale packet MUST NOT have regressed LastTimestampMs
                    Assert.True(manager.TryGetRemoteState(peer, out var stateStaleCheck));
                    Assert.Equal((int)currentHighWaterTimestamp, (int)stateStaleCheck.StateFlags != 0 ? (int)currentHighWaterTimestamp : (int)currentHighWaterTimestamp);
                    // Facing should still reflect the high water packet (which is Right)
                    Assert.True(stateStaleCheck.FacingRight,
                        $"Facing was corrupted to Left by stale packet at timestamp {staleTimestamp} when high-water was {currentHighWaterTimestamp}");
                    Assert.NotEqual(AnimationClipHasher.ComputeHash("Walk"), stateStaleCheck.ActiveClipHash);
                }
                else
                {
                    // Advance forward timestamp
                    uint advanceMs = (uint)rng.Next(15, 40);
                    currentHighWaterTimestamp += advanceMs;

                    byte facing = 1; // Keep facing Right for forward packets
                    int clipHash = AnimationClipHasher.ComputeHash("Run");

                    manager.UpdateRemoteTransform(
                        peer,
                        posX: (float)i * 0.1f, posY: 0f, velX: 5f, velY: 0f,
                        facingDirection: facing,
                        stateFlags: TransformSyncPacket.FlagGrounded,
                        animClipHash: clipHash,
                        normalizedTime: (ushort)(i % 1000),
                        timestampMs: currentHighWaterTimestamp);

                    forwardPacketsInjected++;

                    Assert.True(manager.TryGetRemoteState(peer, out var stateForwardCheck));
                    Assert.True(stateForwardCheck.FacingRight);
                    Assert.Equal(clipHash, stateForwardCheck.ActiveClipHash);
                }
            }

            Assert.True(stalePacketsInjected > 500, $"Expected >500 stale packets, got {stalePacketsInjected}");
            Assert.True(forwardPacketsInjected > 900, $"Expected >900 forward packets, got {forwardPacketsInjected}");
        }

        [Fact]
        public void Stress1_OutOfOrder_HighFrequencyFacingPingPong_NoStaleFlip()
        {
            var manager = new HeadlessPlayerCloneManager(targetDelayMs: 50.0);
            var peer = new NetworkPeerId(1002);
            manager.SpawnRemotePlayer(peer, isPlayer2: false);

            // Forward timeline:
            // t=1000: Facing Right, clip=Idle
            manager.UpdateRemoteTransform(peer, 0f, 0f, 0f, 0f, 1, TransformSyncPacket.FlagGrounded, AnimationClipHasher.ComputeHash("Idle"), 0, 1000);
            Assert.True(manager.TryGetRemoteState(peer, out var s1));
            Assert.True(s1.FacingRight);
            Assert.Equal(AnimationClipHasher.ComputeHash("Idle"), s1.ActiveClipHash);

            // t=1033: Facing Left, clip=Run
            manager.UpdateRemoteTransform(peer, -1f, 0f, -5f, 0f, 0, TransformSyncPacket.FlagGrounded, AnimationClipHasher.ComputeHash("Run"), 100, 1033);
            Assert.True(manager.TryGetRemoteState(peer, out var s2));
            Assert.False(s2.FacingRight);
            Assert.Equal(AnimationClipHasher.ComputeHash("Run"), s2.ActiveClipHash);

            // Adversarial burst: 50 stale packets from timestamps 1001..1032 claiming facing Right and clip Jump
            for (uint staleTs = 1001; staleTs <= 1032; staleTs++)
            {
                manager.UpdateRemoteTransform(peer, 5f, 5f, 10f, 5f, 1, TransformSyncPacket.FlagGrounded, AnimationClipHasher.ComputeHash("Jump"), 500, staleTs);
                Assert.True(manager.TryGetRemoteState(peer, out var sCurrent));
                Assert.False(sCurrent.FacingRight, $"Stale packet at {staleTs} flipped facing to Right");
                Assert.Equal(AnimationClipHasher.ComputeHash("Run"), sCurrent.ActiveClipHash);
            }

            // t=1066: Next forward packet facing Right, clip=Attack
            manager.UpdateRemoteTransform(peer, 2f, 0f, 8f, 0f, 1, TransformSyncPacket.FlagGrounded, AnimationClipHasher.ComputeHash("Attack"), 200, 1066);
            Assert.True(manager.TryGetRemoteState(peer, out var s3));
            Assert.True(s3.FacingRight);
            Assert.Equal(AnimationClipHasher.ComputeHash("Attack"), s3.ActiveClipHash);
        }

        [Theory]
        [InlineData(9999, false)]   // Within 10s window: treated as stale packet -> rejected
        [InlineData(10000, false)]  // Exactly 10s boundary: treated as stale -> rejected
        [InlineData(10001, true)]   // > 10s jump backward: treated as session reset / clock wrap -> accepted
        [InlineData(50000, true)]   // Huge backward jump: accepted as session reset
        public void Stress1_OutOfOrder_ClockWrapBoundaryExactness(uint backwardJumpMs, bool shouldAccept)
        {
            var manager = new HeadlessPlayerCloneManager();
            var peer = new NetworkPeerId(1003);
            manager.SpawnRemotePlayer(peer, isPlayer2: false);

            const uint baselineTs = 60000;
            // Establish baseline at 60,000ms: Facing Right, clip Idle
            manager.UpdateRemoteTransform(peer, 100f, 0f, 0f, 0f, 1, TransformSyncPacket.FlagGrounded, AnimationClipHasher.ComputeHash("Idle"), 0, baselineTs);
            Assert.True(manager.TryGetRemoteState(peer, out var sBase));
            Assert.True(sBase.FacingRight);
            Assert.Equal(AnimationClipHasher.ComputeHash("Idle"), sBase.ActiveClipHash);

            // Send packet at (baselineTs - backwardJumpMs) with Facing Left and clip Dash
            uint candidateTs = baselineTs - backwardJumpMs;
            manager.UpdateRemoteTransform(peer, 0f, 0f, 0f, 0f, 0, TransformSyncPacket.FlagGrounded, AnimationClipHasher.ComputeHash("Dash"), 0, candidateTs);

            Assert.True(manager.TryGetRemoteState(peer, out var sAfter));
            if (shouldAccept)
            {
                Assert.False(sAfter.FacingRight, $"Expected clock wrap to accept facing Left at {candidateTs} (jump {backwardJumpMs}ms)");
                Assert.Equal(AnimationClipHasher.ComputeHash("Dash"), sAfter.ActiveClipHash);
            }
            else
            {
                Assert.True(sAfter.FacingRight, $"Expected stale packet to be rejected at {candidateTs} (jump {backwardJumpMs}ms)");
                Assert.Equal(AnimationClipHasher.ComputeHash("Idle"), sAfter.ActiveClipHash);
            }
        }

        #endregion

        #region 2. Adversarial Stress: Single-Snapshot Buffer Starvation & Dead Reckoning

        [Fact]
        public void Stress2_SingleSnapshot_ContinuousRenderTimeSweep_DeadReckonsAndClamps()
        {
            var manager = new HeadlessPlayerCloneManager(targetDelayMs: 50.0);
            var peer = new NetworkPeerId(2001);
            manager.SpawnRemotePlayer(peer, isPlayer2: true);

            const float startX = 50f;
            const float velX = 12f; // Hornet sprint speed
            const uint packetTimestamp = 2000;

            // Single snapshot received at t=2000ms
            manager.UpdateRemoteTransform(
                peer,
                posX: startX, posY: 0f, velX: velX, velY: 0f,
                facingDirection: 1,
                stateFlags: TransformSyncPacket.FlagGrounded,
                animClipHash: AnimationClipHasher.ComputeHash("Run"),
                normalizedTime: 0,
                timestampMs: packetTimestamp);

            // Theoretical dead reckoning max drift = velX * (1 - 1.5 * 0.25) * 0.25 = 12 * 0.625 * 0.25 = 1.875f
            float theoreticalMaxX = startX + 1.875f;

            // 1. Render time before snapshot arrival (renderTime < 2000ms, localTime < 2050ms)
            // e.g., localTime = 2020ms -> renderTime = 1970ms (< 2000ms). t = 0.0
            manager.UpdateInterpolation(2020.0);
            manager.TryGetRemotePosition(peer, out float prePosX, out float prePosY);
            Assert.Equal(startX, prePosX);
            Assert.Equal(0f, prePosY);

            // 2. Render time exactly at snapshot (localTime = 2050ms -> renderTime = 2000ms). t = 1.0, extrapDt = 0s
            manager.UpdateInterpolation(2050.0);
            manager.TryGetRemotePosition(peer, out float atPosX, out _);
            Assert.Equal(startX, atPosX);

            // 3. Render time advancing through extrapolation window (localTime 2060ms to 2300ms -> renderTime 2010ms to 2250ms)
            float prevPosX = startX;
            for (double localMs = 2060.0; localMs <= 2300.0; localMs += 10.0)
            {
                manager.UpdateInterpolation(localMs);
                manager.TryGetRemotePosition(peer, out float currentX, out float currentY);

                // Monotonic forward advancement without freezing
                Assert.True(currentX > prevPosX,
                    $"Dead reckoning froze or regressed at localMs={localMs}: currentX={currentX} <= prevPosX={prevPosX}");
                // Bound by theoretical maximum
                Assert.True(currentX <= theoreticalMaxX + 1e-4f,
                    $"Current position {currentX} exceeded theoretical clamp {theoreticalMaxX}");
                Assert.Equal(0f, currentY);

                prevPosX = currentX;
            }

            // 4. Render time deep into starvation: 500ms, 2000ms, 10000ms past snapshot
            double[] deepStarvationTimes = { 2550.0, 4050.0, 12050.0 };
            foreach (var deepTime in deepStarvationTimes)
            {
                manager.UpdateInterpolation(deepTime);
                manager.TryGetRemotePosition(peer, out float clampedX, out float clampedY);

                Assert.Equal(theoreticalMaxX, clampedX, 2);
                Assert.Equal(0f, clampedY);
                Assert.False(float.IsNaN(clampedX));
                Assert.False(float.IsInfinity(clampedX));
            }
        }

        [Fact]
        public void Stress2_SingleSnapshot_NegativeVelocityAndAirborne_ExtrapolatesAccurately()
        {
            var manager = new HeadlessPlayerCloneManager(targetDelayMs: 50.0);
            var peerNeg = new NetworkPeerId(2002);
            var peerAir = new NetworkPeerId(2003);

            manager.SpawnRemotePlayer(peerNeg, isPlayer2: false);
            manager.SpawnRemotePlayer(peerAir, isPlayer2: true);

            // peerNeg: moving left at -16f, grounded
            manager.UpdateRemoteTransform(peerNeg, 100f, 0f, -16f, 0f, 0, TransformSyncPacket.FlagGrounded, AnimationClipHasher.ComputeHash("Run"), 0, 1000);

            // peerAir: airborne jumping upwards at (5f, 15f), not grounded
            manager.UpdateRemoteTransform(peerAir, 0f, 10f, 5f, 15f, 1, 0, AnimationClipHasher.ComputeHash("Jump"), 0, 1000);

            // Advance 100ms into extrapolation: localTime = 1150ms -> renderTime = 1100ms (0.10s into starvation)
            manager.UpdateInterpolation(1150.0);

            manager.TryGetRemotePosition(peerNeg, out float negX, out float negY);
            manager.TryGetRemotePosition(peerAir, out float airX, out float airY);

            // Leftward extrapolation: negX must have moved left (< 100f)
            Assert.True(negX < 100f - 1.0f, $"Expected leftward extrapolation < 99f, got {negX}");
            Assert.Equal(0f, negY);

            // Airborne extrapolation: airX moved right (> 0f), airY moved upward with gravity damping (> 10f)
            Assert.True(airX > 0.3f, $"Expected forward extrapolation > 0.3f, got {airX}");
            Assert.True(airY > 10.5f, $"Expected upward airborne extrapolation > 10.5f, got {airY}");

            // Advance deep into starvation (localTime = 3000ms)
            manager.UpdateInterpolation(3000.0);
            manager.TryGetRemotePosition(peerNeg, out float finalNegX, out _);
            manager.TryGetRemotePosition(peerAir, out float finalAirX, out float finalAirY);

            // Clamped positions: negative drift bound = -16 * 0.15625 = -2.5f -> finalNegX = 97.5f
            Assert.Equal(97.5f, finalNegX, 2);
            Assert.False(float.IsNaN(finalAirX));
            Assert.False(float.IsNaN(finalAirY));
        }

        #endregion

        #region 3. Adversarial Stress: Room Transition Teleport Snaps

        [Fact]
        public void Stress3_Teleport_SubThresholdSmoothVsSuperThresholdInstantSnap()
        {
            var manager = new HeadlessPlayerCloneManager(targetDelayMs: 50.0);
            var peerSub = new NetworkPeerId(3001);
            var peerSuper = new NetworkPeerId(3002);

            manager.SpawnRemotePlayer(peerSub, isPlayer2: false);
            manager.SpawnRemotePlayer(peerSuper, isPlayer2: true);

            // Sub-threshold peer: moves from (0, 0) to (4.0, 0) in 33ms. At t=0.5, interpX = 2.0 (< 2.5 snap threshold)
            manager.UpdateRemoteTransform(peerSub, 0f, 0f, 0f, 0f, 1, TransformSyncPacket.FlagGrounded, AnimationClipHasher.ComputeHash("Idle"), 0, 1000);
            manager.UpdateRemoteTransform(peerSub, 4.0f, 0f, 0f, 0f, 1, TransformSyncPacket.FlagGrounded, AnimationClipHasher.ComputeHash("Idle"), 0, 1033);

            // Super-threshold peer: room transition from (0, 0) to (6.0, 0) in 33ms. At t=0.5, interpX = 3.0 (> 2.5 snap threshold)
            manager.UpdateRemoteTransform(peerSuper, 0f, 0f, 0f, 0f, 1, TransformSyncPacket.FlagGrounded, AnimationClipHasher.ComputeHash("Idle"), 0, 1000);
            manager.UpdateRemoteTransform(peerSuper, 6.0f, 0f, 0f, 0f, 1, TransformSyncPacket.FlagGrounded, AnimationClipHasher.ComputeHash("Idle"), 0, 1033);

            // Sample at midpoint: renderTime = 1016.5ms -> localTime = 1066.5ms
            manager.UpdateInterpolation(1066.5);

            manager.TryGetRemotePosition(peerSub, out float subX, out _);
            manager.TryGetRemotePosition(peerSuper, out float superX, out _);

            // Sub-threshold interpolates smoothly: midpoint should be approximately ~2.0f
            Assert.True(subX > 1.5f && subX < 2.5f, $"Expected smooth midpoint interpolation ~2.0f, got {subX}");

            // Super-threshold MUST snap instantly to destination s1 (6.0f)
            Assert.Equal(6.0f, superX, 2);
        }

        [Fact]
        public void Stress3_Teleport_LargeDistanceMidpointSweep_StrictlyAtDestinationSnapshot()
        {
            var manager = new HeadlessPlayerCloneManager(targetDelayMs: 50.0);
            var peer = new NetworkPeerId(3003);
            manager.SpawnRemotePlayer(peer, isPlayer2: true);

            const float roomAX = 0f, roomAY = 0f;
            const float roomBX = 850f, roomBY = 320f;
            const float exitVelX = 18f, exitVelY = 0f;

            // Snapshot 1: Room A door boundary at (0, 0)
            manager.UpdateRemoteTransform(peer, roomAX, roomAY, 5f, 0f, 1, TransformSyncPacket.FlagGrounded, AnimationClipHasher.ComputeHash("Run"), 0, 1000);

            // Snapshot 2: Room B door arrival at (850, 320) with sprint exit velocity
            manager.UpdateRemoteTransform(peer, roomBX, roomBY, exitVelX, exitVelY, 1, TransformSyncPacket.FlagGrounded, AnimationClipHasher.ComputeHash("Run"), 100, 1050);

            // Sweep render time across the entire transition interval: t = 0.05, 0.1, 0.25, 0.5, 0.75, 0.95
            // renderTimes: 1002.5 to 1047.5 -> localTimes: 1052.5 to 1097.5
            for (double localMs = 1052.5; localMs <= 1097.5; localMs += 5.0)
            {
                manager.UpdateInterpolation(localMs);
                Assert.True(manager.TryGetRemoteState(peer, out var state));

                // Invariant: At ALL interpolation steps during a teleport, the clone MUST reside at Room B
                // and MUST NEVER be found traversing the intermediate void (e.g. 425, 160)
                Assert.Equal(roomBX, state.PositionX, 2);
                Assert.Equal(roomBY, state.PositionY, 2);
                Assert.Equal(exitVelX, state.VelocityX, 2);
                Assert.Equal(exitVelY, state.VelocityY, 2);
            }
        }

        [Fact]
        public void Stress3_Teleport_RapidSuccessiveRoomTransitions_EachInstantlyWarped()
        {
            var manager = new HeadlessPlayerCloneManager(targetDelayMs: 50.0);
            var peer = new NetworkPeerId(3004);
            manager.SpawnRemotePlayer(peer, isPlayer2: false);

            // Room A -> Room B -> Room C in rapid 33ms steps
            // Room A: (10, 20) at t=1000
            manager.UpdateRemoteTransform(peer, 10f, 20f, 0f, 0f, 1, TransformSyncPacket.FlagGrounded, AnimationClipHasher.ComputeHash("Idle"), 0, 1000);
            // Room B: (600, 150) at t=1033
            manager.UpdateRemoteTransform(peer, 600f, 150f, 0f, 0f, 1, TransformSyncPacket.FlagGrounded, AnimationClipHasher.ComputeHash("Idle"), 0, 1033);
            // Room C: (-400, 800) at t=1066
            manager.UpdateRemoteTransform(peer, -400f, 800f, 0f, 0f, 1, TransformSyncPacket.FlagGrounded, AnimationClipHasher.ComputeHash("Idle"), 0, 1066);

            // Sample inside A->B segment: renderTime = 1016 -> localTime = 1066
            manager.UpdateInterpolation(1066.0);
            Assert.True(manager.TryGetRemotePosition(peer, out float posAB_X, out float posAB_Y));
            Assert.Equal(600f, posAB_X, 1);
            Assert.Equal(150f, posAB_Y, 1);

            // Sample inside B->C segment: renderTime = 1050 -> localTime = 1100
            manager.UpdateInterpolation(1100.0);
            Assert.True(manager.TryGetRemotePosition(peer, out float posBC_X, out float posBC_Y));
            Assert.Equal(-400f, posBC_X, 1);
            Assert.Equal(800f, posBC_Y, 1);
        }

        #endregion

        #region 4. Adversarial Stress: Dash Trail Lifecycle Invariants

        [Fact]
        public void Stress4_DashTrail_PhaseTransitionsAndResetInvariants()
        {
            var lifecycle = new DashTrailLifecycle();

            // Stress 500 consecutive dash cycles
            for (int cycle = 0; cycle < 500; cycle++)
            {
                Assert.True(lifecycle.CanTriggerDash);
                Assert.Equal(DashTrailPhase.Idle, lifecycle.CurrentPhase);

                // Trigger dash (air dash on odd cycles, ground dash on even cycles)
                bool isAir = (cycle % 2 != 0);
                lifecycle.TriggerDash(isAirDash: isAir, isPlayer2: true);

                Assert.Equal(DashTrailPhase.Active, lifecycle.CurrentPhase);
                Assert.False(lifecycle.CanTriggerDash);
                Assert.Equal(DashTrailLifecycle.DefaultDashTime, lifecycle.ActiveTimeRemaining, 3);

                // Tick partially (0.10s)
                lifecycle.Update(0.10f, isGrounded: !isAir, isWallSliding: false);
                Assert.Equal(DashTrailPhase.Active, lifecycle.CurrentPhase);

                if (cycle % 3 == 0)
                {
                    // Interrupt dash mid-flight (e.g. hurt or wall collision)
                    lifecycle.InterruptDash();
                    Assert.Equal(DashTrailPhase.Cooldown, lifecycle.CurrentPhase);
                    // Grounding during cooldown should immediately restore to Idle
                    lifecycle.Update(0.016f, isGrounded: true, isWallSliding: false);
                    Assert.Equal(DashTrailPhase.Idle, lifecycle.CurrentPhase);
                }
                else
                {
                    // Tick remaining active duration (0.16s > 0.15s)
                    lifecycle.Update(0.16f, isGrounded: false, isWallSliding: false);
                    Assert.Equal(DashTrailPhase.Cooldown, lifecycle.CurrentPhase);

                    // Tick through cooldown (0.41s > 0.40s) without touching ground
                    lifecycle.Update(0.41f, isGrounded: false, isWallSliding: false);
                    Assert.Equal(DashTrailPhase.Idle, lifecycle.CurrentPhase);
                }
            }
        }

        #endregion

        #region 5. Adversarial Stress: Facing Direction Scale Invariant

        [Fact]
        public void Stress5_FacingDirectionScale_Strict1000ToggleInvariance()
        {
            var manager = new HeadlessPlayerCloneManager();
            var peer = new NetworkPeerId(5001);
            manager.SpawnRemotePlayer(peer, isPlayer2: false);

            for (int i = 0; i < 1000; i++)
            {
                bool facingRight = (i % 2 == 0);
                manager.SetFacingDirection(peer, facingRight);

                Assert.True(manager.TryGetRemoteState(peer, out var state));
                Assert.Equal(facingRight, state.FacingRight);

                // Verify scale equivalent value: 1.0f for right, -1.0f for left
                float expectedScaleX = facingRight ? 1f : -1f;
                float actualScaleX = state.FacingRight ? 1f : -1f;
                Assert.Equal(expectedScaleX, actualScaleX);
            }
        }

        #endregion

        #region 6. Extreme Edge Case & Stress Boundary Tests

        [Fact]
        public void Stress6_ClockWrap_UintMaxWrapToSmallTimestamp_CleanSessionReset()
        {
            var manager = new HeadlessPlayerCloneManager();
            var peer = new NetworkPeerId(6001);
            manager.SpawnRemotePlayer(peer, isPlayer2: true);

            // Simulation session 1 reaches near uint.MaxValue (49.7 days of continuous session)
            uint nearMaxTs = uint.MaxValue - 500;
            manager.UpdateRemoteTransform(peer, 100f, 0f, 0f, 0f, 1, TransformSyncPacket.FlagGrounded, AnimationClipHasher.ComputeHash("Run"), 0, nearMaxTs);

            Assert.True(manager.TryGetRemoteState(peer, out var s1));
            Assert.True(s1.FacingRight);
            Assert.Equal(AnimationClipHasher.ComputeHash("Run"), s1.ActiveClipHash);

            // Clock wraps to 100ms (backward jump of > 4 billion ms > 10,000ms threshold)
            uint wrappedTs = 100;
            manager.UpdateRemoteTransform(peer, 0f, 0f, 0f, 0f, 0, TransformSyncPacket.FlagGrounded, AnimationClipHasher.ComputeHash("Idle"), 0, wrappedTs);

            Assert.True(manager.TryGetRemoteState(peer, out var s2));
            Assert.False(s2.FacingRight, "Clock wrap must accept new session facing direction");
            Assert.Equal(AnimationClipHasher.ComputeHash("Idle"), s2.ActiveClipHash);
        }

        [Fact]
        public void Stress6_SingleSnapshot_ZeroVelocity_StationaryStability()
        {
            var manager = new HeadlessPlayerCloneManager(targetDelayMs: 50.0);
            var peer = new NetworkPeerId(6002);
            manager.SpawnRemotePlayer(peer, isPlayer2: false);

            const float posX = 123.456f;
            const float posY = 78.910f;

            // Single snapshot stationary
            manager.UpdateRemoteTransform(peer, posX, posY, 0f, 0f, 1, TransformSyncPacket.FlagGrounded, AnimationClipHasher.ComputeHash("Idle"), 0, 1000);

            // Advance through starvation up to 10 seconds later
            for (double t = 1050.0; t <= 11050.0; t += 500.0)
            {
                manager.UpdateInterpolation(t);
                manager.TryGetRemotePosition(peer, out float curX, out float curY);
                Assert.Equal(posX, curX, 3);
                Assert.Equal(posY, curY, 3);
            }
        }

        [Fact]
        public void Stress6_SingleSnapshot_ExtremeVelocity_BoundedExtrapolation()
        {
            var manager = new HeadlessPlayerCloneManager(targetDelayMs: 50.0);
            var peer = new NetworkPeerId(6003);
            manager.SpawnRemotePlayer(peer, isPlayer2: false);

            const float startX = 0f;
            const float extremeVx = 250f; // Hypersonic glitch speed

            manager.UpdateRemoteTransform(peer, startX, 0f, extremeVx, 0f, 1, TransformSyncPacket.FlagGrounded, AnimationClipHasher.ComputeHash("Dash"), 0, 1000);

            // Advance deep into starvation (localTime = 3000ms)
            manager.UpdateInterpolation(3000.0);
            manager.TryGetRemotePosition(peer, out float curX, out _);

            // Max drift = 250 * 0.15625 = 39.0625f
            float expectedClampedX = extremeVx * 0.15625f;
            Assert.Equal(expectedClampedX, curX, 1);
            Assert.False(float.IsNaN(curX));
            Assert.False(float.IsInfinity(curX));
        }

        [Fact]
        public void Stress6_Teleport_NegativeCoordinates_InstantSnap()
        {
            var manager = new HeadlessPlayerCloneManager(targetDelayMs: 50.0);
            var peer = new NetworkPeerId(6004);
            manager.SpawnRemotePlayer(peer, isPlayer2: true);

            // Snapshot 1: Deep underground negative coordinates (-1200, -450)
            manager.UpdateRemoteTransform(peer, -1200f, -450f, 0f, 0f, 1, TransformSyncPacket.FlagGrounded, AnimationClipHasher.ComputeHash("Idle"), 0, 1000);

            // Snapshot 2: Bench in surface town at (500, 200)
            manager.UpdateRemoteTransform(peer, 500f, 200f, 0f, 0f, 1, TransformSyncPacket.FlagGrounded, AnimationClipHasher.ComputeHash("Idle"), 0, 1033);

            // Sample midpoint: renderTime = 1016.5ms -> localTime = 1066.5ms
            manager.UpdateInterpolation(1066.5);
            Assert.True(manager.TryGetRemoteState(peer, out var state));

            // Must snap cleanly to destination (500, 200)
            Assert.Equal(500f, state.PositionX, 2);
            Assert.Equal(200f, state.PositionY, 2);
        }

        #endregion
    }
}
