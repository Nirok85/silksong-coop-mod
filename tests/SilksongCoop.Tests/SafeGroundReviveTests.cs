using System;
using System.Collections.Generic;
using SilksongCoop.Core.Revive;
using Xunit;

namespace SilksongCoop.Tests
{
    public class SafeGroundReviveTests
    {
        /// <summary>
        /// Simulation prober conforming to IGroundProber using the same logic as UnityGroundProber
        /// to verify spatial algorithms, hazard avoidance, slope screening, and fallback cascades.
        /// </summary>
        public class SimulatedGroundProber : IGroundProber
        {
            public class Platform
            {
                public float MinX { get; set; }
                public float MaxX { get; set; }
                public float SurfaceY { get; set; }
                public float NormalY { get; set; } = 1.0f;
                public bool IsHazard { get; set; } = false;
            }

            public List<Platform> Platforms { get; } = new List<Platform>();
            public (float X, float Y)? FallbackHazardLocation { get; set; }

            public bool TryFindSafeGround(float partnerX, float partnerY, out float safeX, out float safeY)
            {
                const float extentsY = 0.8f;
                const float offsetY = 0.0f;

                foreach (float lateralOffset in SafeGroundSolver.CandidateOffsets)
                {
                    float probeX = partnerX + lateralOffset;
                    float rayOriginY = partnerY + SafeGroundSolver.ProbeRayOriginYOffset;

                    Platform? closestPlatform = null;
                    float closestDist = float.MaxValue;

                    foreach (var platform in Platforms)
                    {
                        if (probeX >= platform.MinX && probeX <= platform.MaxX)
                        {
                            float dist = rayOriginY - platform.SurfaceY;
                            if (dist >= 0f && dist <= SafeGroundSolver.ProbeDistance)
                            {
                                if (dist < closestDist)
                                {
                                    closestDist = dist;
                                    closestPlatform = platform;
                                }
                            }
                        }
                    }

                    if (closestPlatform != null)
                    {
                        // Slope validation (reject steep surfaces / walls)
                        if (closestPlatform.NormalY < SafeGroundSolver.MinGroundNormalY)
                        {
                            continue;
                        }

                        // Hazard screening (reject spikes, acid, lava)
                        if (closestPlatform.IsHazard)
                        {
                            continue;
                        }

                        safeX = probeX;
                        safeY = closestPlatform.SurfaceY + extentsY - offsetY + 0.01f;
                        return true;
                    }
                }

                // Fallback to hazard checkpoint if available
                if (FallbackHazardLocation.HasValue)
                {
                    safeX = FallbackHazardLocation.Value.X;
                    safeY = FallbackHazardLocation.Value.Y;
                    return true;
                }

                safeX = partnerX;
                safeY = partnerY + 0.5f;
                return false;
            }

            public int CalculateReviveHealth(int maxHealth)
            {
                return SafeGroundSolver.CalculateReviveHealth(maxHealth);
            }
        }

        [Fact]
        public void DirectGround_OffsetZero_SelectedImmediately()
        {
            var prober = new SimulatedGroundProber();
            prober.Platforms.Add(new SimulatedGroundProber.Platform
            {
                MinX = -10f,
                MaxX = 10f,
                SurfaceY = 5.0f,
                NormalY = 1.0f,
                IsHazard = false
            });

            bool success = prober.TryFindSafeGround(0.0f, 5.0f, out float safeX, out float safeY);

            Assert.True(success);
            Assert.Equal(0.0f, safeX);
            // 5.0f + 0.8f extents - 0 offset + 0.01f = 5.81f
            Assert.Equal(5.81f, safeY, 2);
        }

        [Fact]
        public void DirectHazard_OffsetZeroSpikes_SelectsAdjacentSafeOffset()
        {
            var prober = new SimulatedGroundProber();
            // Center is spikes
            prober.Platforms.Add(new SimulatedGroundProber.Platform
            {
                MinX = -0.5f,
                MaxX = 0.5f,
                SurfaceY = 5.0f,
                NormalY = 1.0f,
                IsHazard = true
            });
            // Safe ground at +1.2
            prober.Platforms.Add(new SimulatedGroundProber.Platform
            {
                MinX = 0.8f,
                MaxX = 3.0f,
                SurfaceY = 5.0f,
                NormalY = 1.0f,
                IsHazard = false
            });

            bool success = prober.TryFindSafeGround(0.0f, 5.0f, out float safeX, out float safeY);

            Assert.True(success);
            Assert.Equal(1.2f, safeX, 1);
            Assert.Equal(5.81f, safeY, 2);
        }

        [Fact]
        public void ChasmVoid_BelowPartner_SelectsLedgeAtMediumOffset()
        {
            var prober = new SimulatedGroundProber();
            // Void between -2.0 and +2.0 (no platform). Safe platform starts at -2.4
            prober.Platforms.Add(new SimulatedGroundProber.Platform
            {
                MinX = -5.0f,
                MaxX = -2.2f,
                SurfaceY = 3.0f,
                NormalY = 1.0f,
                IsHazard = false
            });

            bool success = prober.TryFindSafeGround(0.0f, 5.0f, out float safeX, out float safeY);

            Assert.True(success);
            Assert.Equal(-2.4f, safeX, 1);
            Assert.Equal(3.81f, safeY, 2);
        }

        [Fact]
        public void SteepSlope_Exceeding45Degrees_Rejected()
        {
            var prober = new SimulatedGroundProber();
            // Steep cliff directly below (normal.y = 0.5, > 60 deg slope)
            prober.Platforms.Add(new SimulatedGroundProber.Platform
            {
                MinX = -1.0f,
                MaxX = 1.0f,
                SurfaceY = 5.0f,
                NormalY = 0.5f, // Below 0.707 threshold
                IsHazard = false
            });
            // Flat safe platform at -1.2
            prober.Platforms.Add(new SimulatedGroundProber.Platform
            {
                MinX = -2.0f,
                MaxX = -1.1f,
                SurfaceY = 5.0f,
                NormalY = 0.95f,
                IsHazard = false
            });

            bool success = prober.TryFindSafeGround(0.0f, 5.0f, out float safeX, out float safeY);

            Assert.True(success);
            Assert.Equal(-1.2f, safeX, 1);
        }

        [Fact]
        public void AllOffsetsHazard_CascadesToHazardCheckpointFallback()
        {
            var prober = new SimulatedGroundProber();
            // All platforms around partner are acid/hazard
            prober.Platforms.Add(new SimulatedGroundProber.Platform
            {
                MinX = -10f,
                MaxX = 10f,
                SurfaceY = 2.0f,
                NormalY = 1.0f,
                IsHazard = true
            });
            prober.FallbackHazardLocation = (100.5f, 25.0f);

            bool success = prober.TryFindSafeGround(0.0f, 5.0f, out float safeX, out float safeY);

            Assert.True(success);
            Assert.Equal(100.5f, safeX);
            Assert.Equal(25.0f, safeY);
        }

        [Fact]
        public void NoGroundAndNoFallback_ReturnsPartnerPositionAndFalse()
        {
            var prober = new SimulatedGroundProber();
            // Empty space, no platforms, no fallback
            prober.FallbackHazardLocation = null;

            bool success = prober.TryFindSafeGround(50.0f, 30.0f, out float safeX, out float safeY);

            Assert.False(success);
            Assert.Equal(50.0f, safeX);
            Assert.Equal(30.5f, safeY);
        }

        [Theory]
        [InlineData(0, 1)]   // Edge case: 0 max HP clamps to minimum 1
        [InlineData(1, 1)]   // 1 / 2 = 0 -> clamped to 1
        [InlineData(2, 1)]   // 2 / 2 = 1
        [InlineData(5, 2)]   // 5 / 2 = 2
        [InlineData(6, 3)]   // 6 / 2 = 3
        [InlineData(7, 3)]   // 7 / 2 = 3
        [InlineData(8, 4)]   // 8 / 2 = 4
        [InlineData(10, 5)]  // 10 / 2 = 5
        [InlineData(11, 5)]  // 11 / 2 = 5
        public void CalculateReviveHealth_EvaluatesFormulaCorrectly(int maxHealth, int expectedReviveHealth)
        {
            int actual = SafeGroundSolver.CalculateReviveHealth(maxHealth);
            Assert.Equal(expectedReviveHealth, actual);
        }

        [Fact]
        public void SafeGroundSolver_ConstantsAndOffsets_MeetSpecification()
        {
            Assert.Equal(7, SafeGroundSolver.CandidateOffsets.Length);
            Assert.Equal(0.0f, SafeGroundSolver.CandidateOffsets[0]);
            Assert.Equal(1.2f, SafeGroundSolver.CandidateOffsets[1]);
            Assert.Equal(-1.2f, SafeGroundSolver.CandidateOffsets[2]);
            Assert.Equal(2.4f, SafeGroundSolver.CandidateOffsets[3]);
            Assert.Equal(-2.4f, SafeGroundSolver.CandidateOffsets[4]);
            Assert.Equal(3.6f, SafeGroundSolver.CandidateOffsets[5]);
            Assert.Equal(-3.6f, SafeGroundSolver.CandidateOffsets[6]);

            Assert.Equal(12.0f, SafeGroundSolver.ProbeDistance);
            Assert.Equal(0.707f, SafeGroundSolver.MinGroundNormalY, 3);
            Assert.Equal(2.5f, SafeGroundSolver.InvulnerabilityDuration);
        }
    }
}
