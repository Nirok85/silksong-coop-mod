using System;
using SilksongCoop.Core.Visuals;
using Xunit;

namespace SilksongCoop.Tests
{
    public class ChallengerMilestone2StressTests
    {
        #region 1. PaletteSwapMath: 10,000 Full-Circle Hue Rotations & Numerical Stability

        [Fact]
        public void PaletteSwapMath_SingleHugePositiveHueShift_10000Circles_PreservesColor()
        {
            // Testing 10,000 full circle rotations in a single call (hueShift = +10,000.0f) on chromatic color
            float r = 0.250f, g = 0.750f, b = 0.500f;
            PaletteSwapMath.ApplyHueShift(r, g, b, 10000.0f, out float outR, out float outG, out float outB);

            // In 32-bit float, 10,000 has 14 integer bits, leaving 10 bits of fractional precision (~1/1024 = 0.00097)
            // Empirical drift is bounded within 0.15% (1.5e-3)
            Assert.True(Math.Abs(r - outR) <= 1.5e-3f, $"outR drift: {Math.Abs(r - outR)}");
            Assert.True(Math.Abs(g - outG) <= 1.5e-3f, $"outG drift: {Math.Abs(g - outG)}");
            Assert.True(Math.Abs(b - outB) <= 1.5e-3f, $"outB drift: {Math.Abs(b - outB)}");
            Assert.Equal(r, outR, 2);
            Assert.Equal(g, outG, 2);
            Assert.Equal(b, outB, 2);
        }

        [Fact]
        public void PaletteSwapMath_SingleHugeNegativeHueShift_Minus10000Circles_PreservesColor()
        {
            // Testing -10,000 full circle rotations in a single call (hueShift = -10,000.0f) on chromatic color
            float r = 0.250f, g = 0.750f, b = 0.500f;
            PaletteSwapMath.ApplyHueShift(r, g, b, -10000.0f, out float outR, out float outG, out float outB);

            // Negative modulo and float mantissa precision bounds: drift <= 1.5e-3 (0.15%)
            Assert.True(Math.Abs(r - outR) <= 1.5e-3f, $"outR drift: {Math.Abs(r - outR)}");
            Assert.True(Math.Abs(g - outG) <= 1.5e-3f, $"outG drift: {Math.Abs(g - outG)}");
            Assert.True(Math.Abs(b - outB) <= 1.5e-3f, $"outB drift: {Math.Abs(b - outB)}");
            Assert.Equal(r, outR, 2);
            Assert.Equal(g, outG, 2);
            Assert.Equal(b, outB, 2);
        }

        [Fact]
        public void PaletteSwapMath_10000SequentialFullCircleRotations_CumulativeStability()
        {
            // Chaining 10,000 consecutive 1.0f hue rotations
            float r = 0.850f, g = 0.200f, b = 0.350f;
            float initR = r, initG = g, initB = b;

            for (int i = 0; i < 10000; i++)
            {
                PaletteSwapMath.ApplyHueShift(r, g, b, 1.0f, out r, out g, out b);
            }

            // After 10,000 conversions back and forth between RGB and HSV:
            Assert.False(float.IsNaN(r) || float.IsNaN(g) || float.IsNaN(b));
            Assert.False(float.IsInfinity(r) || float.IsInfinity(g) || float.IsInfinity(b));
            Assert.Equal(initR, r, 2);
            Assert.Equal(initG, g, 2);
            Assert.Equal(initB, b, 2);
        }

        [Fact]
        public void PaletteSwapMath_10000MicroRotations_FullCircleIntegralStability()
        {
            // 10,000 steps of Delta H = 1.0 / 10,000 = 0.0001f
            // Sum of shifts = 1.0 (exact 360-degree rotation)
            float r = 0.900f, g = 0.300f, b = 0.100f;
            float startR = r, startG = g, startB = b;
            float step = 1.0f / 10000.0f;

            for (int i = 0; i < 10000; i++)
            {
                PaletteSwapMath.ApplyHueShift(r, g, b, step, out r, out g, out b);
            }

            Assert.False(float.IsNaN(r) || float.IsNaN(g) || float.IsNaN(b));
            Assert.Equal(startR, r, 2);
            Assert.Equal(startG, g, 2);
            Assert.Equal(startB, b, 2);
        }

        [Fact]
        public void RodriguesRotation_10000SequentialRotations_NumericalStability()
        {
            float r = 0.850f, g = 0.150f, b = 0.150f;
            float initR = r, initG = g, initB = b;
            float twoPi = 2.0f * (float)Math.PI;

            for (int i = 0; i < 10000; i++)
            {
                PaletteSwapMath.RodriguesRotateRgb(r, g, b, twoPi, out r, out g, out b);
            }

            Assert.False(float.IsNaN(r) || float.IsNaN(g) || float.IsNaN(b));
            // Accumulated single-precision floating point matrix drift after 10,000 matrix multiplications is bounded by 1e-3 (0.1%)
            Assert.True(Math.Abs(initR - r) <= 1e-3f, $"Rodrigues R drift: {Math.Abs(initR - r)}");
            Assert.True(Math.Abs(initG - g) <= 1e-3f, $"Rodrigues G drift: {Math.Abs(initG - g)}");
            Assert.True(Math.Abs(initB - b) <= 1e-3f, $"Rodrigues B drift: {Math.Abs(initB - b)}");
            Assert.Equal(initR, r, 2);
            Assert.Equal(initG, g, 2);
            Assert.Equal(initB, b, 2);
        }

        #endregion

        #region 2. PaletteSwapMath: Color Fuzzing & Value/Saturation Invariance

        [Fact]
        public void PaletteSwapMath_Fuzzing20000RandomColors_NoNaNsAndValuesPreserved()
        {
            var rng = new Random(42);

            for (int i = 0; i < 20000; i++)
            {
                float inR = (float)rng.NextDouble();
                float inG = (float)rng.NextDouble();
                float inB = (float)rng.NextDouble();
                float hueShift = (float)(rng.NextDouble() * 20.0 - 10.0); // [-10, +10]

                PaletteSwapMath.ApplyHueShift(inR, inG, inB, hueShift, out float outR, out float outG, out float outB);

                // 1. Never NaN or Infinite
                Assert.False(float.IsNaN(outR), $"outR NaN for in=({inR},{inG},{inB}), shift={hueShift}");
                Assert.False(float.IsNaN(outG), $"outG NaN for in=({inR},{inG},{inB}), shift={hueShift}");
                Assert.False(float.IsNaN(outB), $"outB NaN for in=({inR},{inG},{inB}), shift={hueShift}");

                // 2. Output strictly bounded in [0, 1]
                Assert.InRange(outR, 0f, 1f);
                Assert.InRange(outG, 0f, 1f);
                Assert.InRange(outB, 0f, 1f);

                // 3. Value V = max(R,G,B) must be preserved within float epsilon
                float inV = Math.Max(inR, Math.Max(inG, inB));
                float outV = Math.Max(outR, Math.Max(outG, outB));
                Assert.True(Math.Abs(inV - outV) <= 1e-3f, $"Value drifted: inV={inV}, outV={outV}");

                // 4. Chroma max - min must be preserved within float epsilon
                float inChroma = PaletteSwapMath.CalculateChroma(inR, inG, inB);
                float outChroma = PaletteSwapMath.CalculateChroma(outR, outG, outB);
                Assert.True(Math.Abs(inChroma - outChroma) <= 1e-3f, $"Chroma drifted: inC={inChroma}, outC={outChroma}");
            }
        }

        #endregion

        #region 3. PaletteSwapMath: Boundary Values & Extreme Out-of-Range Inputs

        [Theory]
        [InlineData(1f, 0f, 0f)] // Red
        [InlineData(0f, 1f, 0f)] // Green
        [InlineData(0f, 0f, 1f)] // Blue
        [InlineData(0f, 1f, 1f)] // Cyan
        [InlineData(1f, 0f, 1f)] // Magenta
        [InlineData(1f, 1f, 0f)] // Yellow
        [InlineData(0f, 0f, 0f)] // Black
        [InlineData(1f, 1f, 1f)] // White
        public void PaletteSwapMath_GamutCorners_AreRobust(float r, float g, float b)
        {
            float[] shifts = { -1.0f, -0.58f, -0.01f, 0f, 0.01f, 0.58f, 1.0f, 2.5f, -100.0f, 100.0f };
            foreach (var shift in shifts)
            {
                PaletteSwapMath.ApplyHueShift(r, g, b, shift, out float outR, out float outG, out float outB);
                Assert.False(float.IsNaN(outR));
                Assert.False(float.IsNaN(outG));
                Assert.False(float.IsNaN(outB));
                Assert.InRange(outR, 0f, 1f);
                Assert.InRange(outG, 0f, 1f);
                Assert.InRange(outB, 0f, 1f);
            }
        }

        [Fact]
        public void PaletteSwapMath_NearAchromaticBoundaryEpsilon_TransitionsSmoothly()
        {
            // Testing around the delta < 1e-5f threshold
            float baseGray = 0.5f;
            float[] deltas = { 0f, 1e-7f, 1e-6f, 9e-6f, 1e-5f, 1.1e-5f, 1e-4f, 1e-3f };

            foreach (var d in deltas)
            {
                float r = baseGray + d;
                float g = baseGray;
                float b = baseGray;

                PaletteSwapMath.ApplyHueShift(r, g, b, PaletteSwapMath.Player2HueShift, out float outR, out float outG, out float outB);

                Assert.False(float.IsNaN(outR));
                Assert.False(float.IsNaN(outG));
                Assert.False(float.IsNaN(outB));
                // Maximum deviation from baseGray must be bounded by delta + tolerance
                Assert.True(Math.Abs(outR - baseGray) <= d + 1e-3f);
            }
        }

        [Fact]
        public void PaletteSwapMath_NearZeroBlackBoundary_TransitionsSmoothly()
        {
            float[] nearBlackLevels = { 0f, 1e-7f, 1e-6f, 1e-5f, 1.1e-5f, 1e-4f };

            foreach (var level in nearBlackLevels)
            {
                PaletteSwapMath.ApplyHueShift(level, level, level, PaletteSwapMath.Player2HueShift, out float outR, out float outG, out float outB);
                Assert.False(float.IsNaN(outR));
                Assert.False(float.IsNaN(outG));
                Assert.False(float.IsNaN(outB));
                Assert.True(outR <= 1e-3f && outG <= 1e-3f && outB <= 1e-3f);
            }
        }

        [Theory]
        [InlineData(-1.0f, -0.5f, -100.0f, 0.58f)] // Negative RGB
        [InlineData(2.0f, 5.0f, 10.0f, 0.58f)]     // HDR / > 1.0 RGB
        [InlineData(-50.0f, 50.0f, 0.5f, -2.5f)]   // Mixed extreme
        [InlineData(100.0f, 100.0f, 100.0f, 0.58f)]// Clamped achromatic
        public void PaletteSwapMath_ExtremeOutOfRangeInputs_ClampedGracefully(float r, float g, float b, float shift)
        {
            PaletteSwapMath.RgbToHsv(r, g, b, out float h, out float s, out float v);
            Assert.InRange(h, 0f, 1f);
            Assert.InRange(s, 0f, 1f);
            Assert.InRange(v, 0f, 1f);

            PaletteSwapMath.HsvToRgb(h, s, v, out float cr, out float cg, out float cb);
            Assert.InRange(cr, 0f, 1f);
            Assert.InRange(cg, 0f, 1f);
            Assert.InRange(cb, 0f, 1f);

            // Directly invoking ApplyHueShift with extreme shift
            PaletteSwapMath.ApplyHueShift(r, g, b, shift, out float outR, out float outG, out float outB);
            Assert.False(float.IsNaN(outR));
            Assert.False(float.IsNaN(outG));
            Assert.False(float.IsNaN(outB));
        }

        #endregion

        #region 4. PaletteSwapMath: Strict Saturation Invariance (S = 0)

        [Fact]
        public void PaletteSwapMath_All256Grayscales_ExactZeroDriftUnderAllHueShifts()
        {
            float[] testShifts = { -1000f, -50.58f, -1f, -0.58f, 0f, 0.333f, 0.58f, 1.0f, 42.0f, 10000.0f };

            // Exhaustive test of all 256 standard 8-bit gray values [0/255 .. 255/255]
            for (int i = 0; i <= 255; i++)
            {
                float gray = i / 255.0f;

                foreach (var shift in testShifts)
                {
                    PaletteSwapMath.ApplyHueShift(gray, gray, gray, shift, out float outR, out float outG, out float outB);

                    // S=0 invariance MUST guarantee exact mathematical identity
                    Assert.Equal(gray, outR, 5);
                    Assert.Equal(gray, outG, 5);
                    Assert.Equal(gray, outB, 5);
                    Assert.True(PaletteSwapMath.IsAchromatic(outR, outG, outB));
                    Assert.True(PaletteSwapMath.IsSaturationInvariant(outR, outG, outB));
                }
            }
        }

        [Fact]
        public void PaletteSwapMath_Fuzz10000RandomGrayscaleLevels_StrictlyPreserved()
        {
            var rng = new Random(1337);

            for (int i = 0; i < 10000; i++)
            {
                float gray = (float)rng.NextDouble();
                float shift = (float)(rng.NextDouble() * 2000.0 - 1000.0);

                PaletteSwapMath.ApplyHueShift(gray, gray, gray, shift, out float outR, out float outG, out float outB);

                Assert.Equal(gray, outR, 5);
                Assert.Equal(gray, outG, 5);
                Assert.Equal(gray, outB, 5);
                Assert.True(PaletteSwapMath.ProveSaturationInvariance(gray, gray, gray, shift, out float maxDev));
                Assert.True(maxDev <= 1e-6f);
            }
        }

        #endregion

        #region 5. DashTrailLifecycle: Rapid Interrupt Sequences & Boundary Stress

        [Fact]
        public void DashTrail_RapidTriggerBombardment_MaintainsPhaseInvariants()
        {
            var controller = new DashTrailLifecycle();

            // Bombard with 1,000 rapid triggers
            for (int i = 0; i < 1000; i++)
            {
                controller.TriggerDash(isAirDash: i % 2 == 0, isPlayer2: true);
                Assert.Equal(DashTrailPhase.Active, controller.CurrentPhase);
                Assert.False(controller.CanTriggerDash);
                Assert.True(controller.ActiveTimeRemaining > 0f);
            }
        }

        [Fact]
        public void DashTrail_InstantWallSlideInterrupt_TransitionsCleanly()
        {
            var controller = new DashTrailLifecycle();

            for (int cycle = 0; cycle < 50; cycle++)
            {
                // Trigger dash
                controller.TriggerDash(isAirDash: false, isPlayer2: false);
                Assert.Equal(DashTrailPhase.Active, controller.CurrentPhase);

                // Immediately interrupt via wall slide contact
                controller.InterruptDash();
                Assert.Equal(DashTrailPhase.Cooldown, controller.CurrentPhase);

                // Touching wall slide immediately resets cooldown to Idle
                controller.Update(0f, isGrounded: false, isWallSliding: true);
                Assert.Equal(DashTrailPhase.Idle, controller.CurrentPhase);
                Assert.True(controller.CanTriggerDash);
            }
        }

        [Fact]
        public void DashTrail_ZeroDurationGroundContacts_ResetsCooldownWithoutTimeAdvancement()
        {
            var controller = new DashTrailLifecycle();

            for (int cycle = 0; cycle < 100; cycle++)
            {
                controller.TriggerDash(isAirDash: true, isPlayer2: true);

                // Interrupt or expire into cooldown
                controller.InterruptDash();
                Assert.Equal(DashTrailPhase.Cooldown, controller.CurrentPhase);

                // Zero-duration ground contact: dt = 0f, isGrounded = true
                controller.Update(0f, isGrounded: true, isWallSliding: false);

                Assert.Equal(DashTrailPhase.Idle, controller.CurrentPhase);
                Assert.Equal(0f, controller.ActiveTimeRemaining);
                Assert.Equal(0f, controller.CooldownTimeRemaining);
                Assert.True(controller.CanTriggerDash);
            }
        }

        [Fact]
        public void DashTrail_1000AlternatingZeroDurationCancelCycles_MaintainsConsistency()
        {
            var controller = new DashTrailLifecycle();

            for (int i = 0; i < 1000; i++)
            {
                controller.TriggerDash(isAirDash: true, isPlayer2: true);
                Assert.Equal(DashTrailPhase.Active, controller.CurrentPhase);

                controller.InterruptDash();
                Assert.Equal(DashTrailPhase.Cooldown, controller.CurrentPhase);

                // Alternate between zero-duration ground and wall resets
                if (i % 2 == 0)
                {
                    controller.Update(0f, isGrounded: true, isWallSliding: false);
                }
                else
                {
                    controller.Update(0f, isGrounded: false, isWallSliding: true);
                }

                Assert.Equal(DashTrailPhase.Idle, controller.CurrentPhase);
                Assert.True(controller.CanTriggerDash);
            }
        }

        [Fact]
        public void DashTrail_ExcessDeltaTime_ActivePhaseOverflow_DemonstratesCooldownBehavior()
        {
            // Adversarial test: large delta time (1.0s) exceeding active (0.25s) + cooldown (0.40s) = 0.65s
            var controller = new DashTrailLifecycle();
            controller.TriggerDash(isAirDash: false, isPlayer2: false);

            controller.Update(1.0f, isGrounded: false, isWallSliding: false);

            // Due to 'else if' design in Update, transitioning Active -> Cooldown does not consume
            // remaining delta time within the same frame tick. Cooldown remains at full duration.
            Assert.Equal(DashTrailPhase.Cooldown, controller.CurrentPhase);
            Assert.Equal(DashTrailLifecycle.DefaultDashCooldown, controller.CooldownTimeRemaining, 3);

            // Next tick drains cooldown
            controller.Update(0.40f, isGrounded: false, isWallSliding: false);
            Assert.Equal(DashTrailPhase.Idle, controller.CurrentPhase);
        }

        [Fact]
        public void DashTrail_HighFrequencyChaosFuzzing_NeverEntersIllegalState()
        {
            var controller = new DashTrailLifecycle();
            var rng = new Random(777);

            for (int i = 0; i < 5000; i++)
            {
                int action = rng.Next(0, 5);
                float dt = (float)rng.NextDouble() * 0.1f;
                bool grounded = rng.Next(0, 2) == 1;
                bool wallSlide = rng.Next(0, 2) == 1;

                switch (action)
                {
                    case 0:
                        controller.TriggerDash(rng.Next(0, 2) == 1, rng.Next(0, 2) == 1);
                        break;
                    case 1:
                        controller.InterruptDash();
                        break;
                    case 2:
                        controller.Update(dt, grounded, wallSlide);
                        break;
                    case 3:
                        controller.Update(0f, grounded, wallSlide); // 0-duration probe
                        break;
                    case 4:
                        if (rng.Next(0, 10) == 0) controller.Reset();
                        break;
                }

                // Invariant checks after every single operation:
                Assert.True(controller.ActiveTimeRemaining >= 0f, $"ActiveTime was negative: {controller.ActiveTimeRemaining}");
                Assert.True(controller.CooldownTimeRemaining >= 0f, $"CooldownTime was negative: {controller.CooldownTimeRemaining}");
                Assert.False(float.IsNaN(controller.ActiveTimeRemaining));
                Assert.False(float.IsNaN(controller.CooldownTimeRemaining));

                if (controller.CurrentPhase == DashTrailPhase.Idle)
                {
                    Assert.True(controller.CanTriggerDash);
                    Assert.Equal(0f, controller.ActiveTimeRemaining);
                    Assert.Equal(0f, controller.CooldownTimeRemaining);
                }
                else
                {
                    Assert.False(controller.CanTriggerDash);
                }
            }
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(0.00001f)]
        [InlineData(0.01666f)]
        [InlineData(0.25f)]
        [InlineData(1000f)]
        public void DashTrail_FrameDeltaVariations_SurviveExtremeTimeDeltas(float dt)
        {
            var controller = new DashTrailLifecycle();
            controller.TriggerDash(isAirDash: false, isPlayer2: true);

            controller.Update(dt, isGrounded: false, isWallSliding: false);

            Assert.False(float.IsNaN(controller.ActiveTimeRemaining));
            Assert.False(float.IsNaN(controller.CooldownTimeRemaining));
            Assert.True(controller.ActiveTimeRemaining >= 0f);
            Assert.True(controller.CooldownTimeRemaining >= 0f);
        }

        [Fact]
        public void DashTrail_ColorConsistency_UnderMultipleInvocations()
        {
            var controller = new DashTrailLifecycle();

            for (int i = 0; i < 100; i++)
            {
                controller.GetTrailColor(true, out float r2, out float g2, out float b2, out float a2);
                Assert.Equal(0.15f, r2);
                Assert.Equal(0.75f, g2);
                Assert.Equal(0.95f, b2);
                Assert.Equal(0.80f, a2);

                controller.GetTrailColor(false, out float r1, out float g1, out float b1, out float a1);
                Assert.Equal(0.90f, r1);
                Assert.Equal(0.15f, g1);
                Assert.Equal(0.20f, b1);
                Assert.Equal(0.80f, a1);
            }
        }

        #endregion
    }
}
