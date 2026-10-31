using System;
using System.Collections.Generic;
using System.IO;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.Revive;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Empirical Adversarial Stress Suite for Milestone 4 (Cooperative Death & Respawn Lifecycle):
    /// 1. SafeGroundSolver.CalculateReviveHealth domain boundaries and invariant verification.
    /// 2. SafeGroundSolver probe offset permutations (center hazard, steep wall normal screening, safe offset selection).
    /// 3. Complete hazard & abyss scenarios: verified fallback to hazardRespawnLocation.
    /// 4. Spectator countdown timer clamping [15.0, 30.0] invariant under extreme configurations.
    /// 5. Control restoration invariant: cState.dead is false strictly before RegainControl() execution.
    /// </summary>
    public class Challenger2Milestone4InvariantsStressTests
    {
        #region 1. SafeGroundSolver.CalculateReviveHealth Boundary Values & Mathematical Invariants

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1, 1)]
        [InlineData(2, 1)]
        [InlineData(5, 2)]
        [InlineData(6, 3)]
        [InlineData(9, 4)]
        [InlineData(10, 5)]
        [InlineData(100, 50)]
        [InlineData(-5, 1)]
        [InlineData(int.MaxValue, 1073741823)]
        public void CalculateReviveHealth_MandatedDomainBoundaries_MatchInvariant(int maxHealth, int expectedHealth)
        {
            // Act
            int actual = SafeGroundSolver.CalculateReviveHealth(maxHealth);

            // Assert exact expected value
            Assert.Equal(expectedHealth, actual);

            // Invariant 1: Revive health must be strictly >= 1
            Assert.True(actual >= 1, $"Invariant violation: revive HP for maxHealth={maxHealth} was {actual}, expected >= 1.");

            // Invariant 2: Revive health must equal Math.Max(1, maxHealth / 2)
            int expectedInvariant = Math.Max(1, maxHealth / 2);
            Assert.Equal(expectedInvariant, actual);
        }

        [Theory]
        [InlineData(-1, 1)]
        [InlineData(-100, 1)]
        [InlineData(int.MinValue, 1)]
        [InlineData(3, 1)]
        [InlineData(4, 2)]
        [InlineData(7, 3)]
        [InlineData(8, 4)]
        [InlineData(11, 5)]
        [InlineData(12, 6)]
        [InlineData(int.MaxValue - 1, 1073741823)]
        public void CalculateReviveHealth_ExtendedBoundaries_MaintainStrictFloorAndMinimum(int maxHealth, int expectedHealth)
        {
            int actual = SafeGroundSolver.CalculateReviveHealth(maxHealth);

            Assert.Equal(expectedHealth, actual);
            Assert.True(actual >= 1);
            Assert.Equal(Math.Max(1, maxHealth / 2), actual);
        }

        [Fact]
        public void CalculateReviveHealth_Fuzz10000Values_StrictlyPreservesInvariant()
        {
            var rng = new Random(424242);

            for (int i = 0; i < 10000; i++)
            {
                // Generate a mix of negative, zero, small, medium, and extreme positive numbers
                int maxHp = rng.Next(int.MinValue / 4, int.MaxValue / 2);
                int result = SafeGroundSolver.CalculateReviveHealth(maxHp);

                // Invariant checks
                Assert.True(result >= 1, $"Fuzz failure at maxHp={maxHp}: result={result} < 1");
                Assert.Equal(Math.Max(1, maxHp / 2), result);
            }
        }

        #endregion

        #region 2. SafeGroundSolver Probe Offset Permutations & Geometric Invariants

        [Fact]
        public void ProbeOffsets_CenterHazard_SteepWallOffsets_SelectsPlus2Point4SafeGround()
        {
            // Mandated Dispatch Scenario:
            // Center probe hits acid/spikes.
            // Offsets +/- 1.2 hit steep walls (normal.y < 0.707).
            // Offset +2.4 hits solid safe ground.
            // Verify solver accurately returns +2.4.

            var prober = new SafeGroundReviveTests.SimulatedGroundProber();

            // Offset 0: Spikes / Acid (hazard)
            prober.Platforms.Add(new SafeGroundReviveTests.SimulatedGroundProber.Platform
            {
                MinX = -0.5f,
                MaxX = 0.5f,
                SurfaceY = 10.0f,
                NormalY = 1.0f,
                IsHazard = true
            });

            // Offset +1.2: Steep cliff (normal.y = 0.65 < 0.707)
            prober.Platforms.Add(new SafeGroundReviveTests.SimulatedGroundProber.Platform
            {
                MinX = 0.8f,
                MaxX = 1.5f,
                SurfaceY = 10.0f,
                NormalY = 0.65f,
                IsHazard = false
            });

            // Offset -1.2: Sheer vertical wall (normal.y = 0.20 < 0.707)
            prober.Platforms.Add(new SafeGroundReviveTests.SimulatedGroundProber.Platform
            {
                MinX = -1.5f,
                MaxX = -0.8f,
                SurfaceY = 10.0f,
                NormalY = 0.20f,
                IsHazard = false
            });

            // Offset +2.4: Solid, flat safe ground platform (normal.y = 1.0)
            prober.Platforms.Add(new SafeGroundReviveTests.SimulatedGroundProber.Platform
            {
                MinX = 2.0f,
                MaxX = 3.0f,
                SurfaceY = 10.0f,
                NormalY = 1.0f,
                IsHazard = false
            });

            // Act: Probe safe ground from partner at (0, 10)
            bool found = prober.TryFindSafeGround(0.0f, 10.0f, out float safeX, out float safeY);

            // Assert
            Assert.True(found, "Safe ground should be found at offset +2.4.");
            Assert.Equal(2.4f, safeX, 1);
            Assert.Equal(10.81f, safeY, 2); // 10.0f + 0.8f extents + 0.01f
        }

        [Fact]
        public void ProbeOffsets_CenterHazard_Offsets1Point2Steep_Plus2Point4Steep_SelectsMinus2Point4SafeGround()
        {
            // Center is hazard; +/- 1.2 are steep walls; +2.4 is steep wall; -2.4 is solid safe ground.
            var prober = new SafeGroundReviveTests.SimulatedGroundProber();

            prober.Platforms.Add(new SafeGroundReviveTests.SimulatedGroundProber.Platform
            {
                MinX = -0.6f,
                MaxX = 0.6f,
                SurfaceY = 8.0f,
                NormalY = 1.0f,
                IsHazard = true
            });

            prober.Platforms.Add(new SafeGroundReviveTests.SimulatedGroundProber.Platform
            {
                MinX = 0.7f,
                MaxX = 1.7f,
                SurfaceY = 8.0f,
                NormalY = 0.5f,
                IsHazard = false
            });

            prober.Platforms.Add(new SafeGroundReviveTests.SimulatedGroundProber.Platform
            {
                MinX = -1.7f,
                MaxX = -0.7f,
                SurfaceY = 8.0f,
                NormalY = 0.4f,
                IsHazard = false
            });

            prober.Platforms.Add(new SafeGroundReviveTests.SimulatedGroundProber.Platform
            {
                MinX = 2.0f,
                MaxX = 2.8f,
                SurfaceY = 8.0f,
                NormalY = 0.6f,
                IsHazard = false
            });

            prober.Platforms.Add(new SafeGroundReviveTests.SimulatedGroundProber.Platform
            {
                MinX = -2.8f,
                MaxX = -2.0f,
                SurfaceY = 8.0f,
                NormalY = 0.98f,
                IsHazard = false
            });

            bool found = prober.TryFindSafeGround(0.0f, 8.0f, out float safeX, out float safeY);

            Assert.True(found);
            Assert.Equal(-2.4f, safeX, 1);
            Assert.Equal(8.81f, safeY, 2);
        }

        [Fact]
        public void ProbeOffsets_CascadesThroughAllCandidateOffsets_InExactPriorityOrder()
        {
            // Verify candidate offset search order strictly follows:
            // [0f, 1.2f, -1.2f, 2.4f, -2.4f, 3.6f, -3.6f]
            float[] expectedOffsets = { 0f, 1.2f, -1.2f, 2.4f, -2.4f, 3.6f, -3.6f };
            Assert.Equal(expectedOffsets, SafeGroundSolver.CandidateOffsets);

            // Test each offset individually by invalidating all prior offsets
            for (int targetIndex = 0; targetIndex < expectedOffsets.Length; targetIndex++)
            {
                var prober = new SafeGroundReviveTests.SimulatedGroundProber();

                // Make all prior offsets hazards
                for (int prior = 0; prior < targetIndex; prior++)
                {
                    float priorOffset = expectedOffsets[prior];
                    prober.Platforms.Add(new SafeGroundReviveTests.SimulatedGroundProber.Platform
                    {
                        MinX = 100.0f + priorOffset - 0.4f,
                        MaxX = 100.0f + priorOffset + 0.4f,
                        SurfaceY = 15.0f,
                        NormalY = 1.0f,
                        IsHazard = true
                    });
                }

                // Make target offset safe ground
                float targetOffset = expectedOffsets[targetIndex];
                prober.Platforms.Add(new SafeGroundReviveTests.SimulatedGroundProber.Platform
                {
                    MinX = 100.0f + targetOffset - 0.4f,
                    MaxX = 100.0f + targetOffset + 0.4f,
                    SurfaceY = 15.0f,
                    NormalY = 1.0f,
                    IsHazard = false
                });

                bool found = prober.TryFindSafeGround(100.0f, 15.0f, out float safeX, out float safeY);

                Assert.True(found, $"Failed finding safe ground at offset index {targetIndex} ({targetOffset})");
                Assert.Equal(100.0f + targetOffset, safeX, 1);
                Assert.Equal(15.81f, safeY, 2);
            }
        }

        [Theory]
        [InlineData(0.706f, false)] // Strictly below MinGroundNormalY (0.707) -> rejected
        [InlineData(0.707f, true)]  // Exactly equal -> accepted
        [InlineData(0.708f, true)]  // Strictly above -> accepted
        [InlineData(1.0f, true)]    // Flat surface -> accepted
        [InlineData(0.0f, false)]   // Vertical wall -> rejected
        [InlineData(-0.5f, false)]  // Ceiling / overhang -> rejected
        public void ProbeOffsets_SlopeNormalYThreshold_ScreensAccurately(float normalY, bool expectedAccept)
        {
            var prober = new SafeGroundReviveTests.SimulatedGroundProber();
            prober.Platforms.Add(new SafeGroundReviveTests.SimulatedGroundProber.Platform
            {
                MinX = -1.0f,
                MaxX = 1.0f,
                SurfaceY = 20.0f,
                NormalY = normalY,
                IsHazard = false
            });

            bool found = prober.TryFindSafeGround(0.0f, 20.0f, out float safeX, out float safeY);

            if (expectedAccept)
            {
                Assert.True(found);
                Assert.Equal(0.0f, safeX);
                Assert.Equal(20.81f, safeY, 2);
            }
            else
            {
                Assert.False(found);
            }
        }

        [Fact]
        public void ProbeOffsets_DistanceExceedingProbeDistance_Ignored()
        {
            var prober = new SafeGroundReviveTests.SimulatedGroundProber();

            // ProbeRayOriginYOffset = 1.0f; partner at Y=50.0f -> ray starts at 51.0f
            // ProbeDistance = 12.0f -> reaches down to Y = 51.0f - 12.0f = 39.0f.
            // Platform at Y = 38.9f is 12.1f away (out of probe range)
            prober.Platforms.Add(new SafeGroundReviveTests.SimulatedGroundProber.Platform
            {
                MinX = -2.0f,
                MaxX = 2.0f,
                SurfaceY = 38.9f,
                NormalY = 1.0f,
                IsHazard = false
            });

            bool found = prober.TryFindSafeGround(0.0f, 50.0f, out _, out _);
            Assert.False(found, "Platform beyond ProbeDistance (12.0f) must not be selected.");

            // Platform at Y = 39.1f is 11.9f away (within probe range)
            prober.Platforms.Clear();
            prober.Platforms.Add(new SafeGroundReviveTests.SimulatedGroundProber.Platform
            {
                MinX = -2.0f,
                MaxX = 2.0f,
                SurfaceY = 39.1f,
                NormalY = 1.0f,
                IsHazard = false
            });

            found = prober.TryFindSafeGround(0.0f, 50.0f, out float safeX, out float safeY);
            Assert.True(found, "Platform within ProbeDistance (12.0f) must be selected.");
            Assert.Equal(0.0f, safeX);
            Assert.Equal(39.91f, safeY, 2);
        }

        #endregion

        #region 3. All-Hazard & Abyss Scenario: Fallback to hazardRespawnLocation

        [Fact]
        public void AllHazardScenario_All7OffsetsHitSpikesOrAcid_FallsBackToHazardRespawnLocation()
        {
            var prober = new SafeGroundReviveTests.SimulatedGroundProber();

            // Wide acid vat spanning all possible probe offsets [-4.0 to +4.0]
            prober.Platforms.Add(new SafeGroundReviveTests.SimulatedGroundProber.Platform
            {
                MinX = -10.0f,
                MaxX = 10.0f,
                SurfaceY = 5.0f,
                NormalY = 1.0f,
                IsHazard = true // All hits are hazards
            });

            prober.FallbackHazardLocation = (88.5f, 14.25f);

            bool success = prober.TryFindSafeGround(0.0f, 6.0f, out float safeX, out float safeY);

            Assert.True(success, "Solver must succeed by cascading to hazardRespawnLocation.");
            Assert.Equal(88.5f, safeX);
            Assert.Equal(14.25f, safeY);
        }

        [Fact]
        public void AbyssScenario_BottomlessPitAll7OffsetsEmpty_FallsBackToHazardRespawnLocation()
        {
            var prober = new SafeGroundReviveTests.SimulatedGroundProber();
            // No platforms in scene (bottomless chasm)
            prober.Platforms.Clear();
            prober.FallbackHazardLocation = (150.0f, 32.0f);

            bool success = prober.TryFindSafeGround(12.0f, 40.0f, out float safeX, out float safeY);

            Assert.True(success);
            Assert.Equal(150.0f, safeX);
            Assert.Equal(32.0f, safeY);
        }

        [Fact]
        public void MixedHazardAndSteepAndAbyss_AllInvalid_FallsBackToHazardRespawnLocation()
        {
            var prober = new SafeGroundReviveTests.SimulatedGroundProber();

            // Offsets 0, 1.2: Hazard
            prober.Platforms.Add(new SafeGroundReviveTests.SimulatedGroundProber.Platform
            {
                MinX = -0.5f,
                MaxX = 1.5f,
                SurfaceY = 10.0f,
                NormalY = 1.0f,
                IsHazard = true
            });

            // Offsets -1.2, 2.4: Steep walls
            prober.Platforms.Add(new SafeGroundReviveTests.SimulatedGroundProber.Platform
            {
                MinX = -1.5f,
                MaxX = -1.0f,
                SurfaceY = 10.0f,
                NormalY = 0.3f,
                IsHazard = false
            });
            prober.Platforms.Add(new SafeGroundReviveTests.SimulatedGroundProber.Platform
            {
                MinX = 2.2f,
                MaxX = 2.6f,
                SurfaceY = 10.0f,
                NormalY = 0.5f,
                IsHazard = false
            });

            // Offsets -2.4, 3.6, -3.6: Abyss (no platform)

            prober.FallbackHazardLocation = (42.0f, 19.5f);

            bool success = prober.TryFindSafeGround(0.0f, 10.0f, out float safeX, out float safeY);

            Assert.True(success);
            Assert.Equal(42.0f, safeX);
            Assert.Equal(19.5f, safeY);
        }

        [Fact]
        public void AllHazardScenario_NoFallbackAvailable_ReturnsFalseAndPartnerDefault()
        {
            var prober = new SafeGroundReviveTests.SimulatedGroundProber();
            prober.Platforms.Add(new SafeGroundReviveTests.SimulatedGroundProber.Platform
            {
                MinX = -20f,
                MaxX = 20f,
                SurfaceY = 5.0f,
                NormalY = 1.0f,
                IsHazard = true
            });
            prober.FallbackHazardLocation = null;

            bool success = prober.TryFindSafeGround(75.0f, 20.0f, out float safeX, out float safeY);

            Assert.False(success);
            Assert.Equal(75.0f, safeX);
            Assert.Equal(20.5f, safeY); // partnerY + 0.5f
        }

        #endregion

        #region 4. Countdown Timer Clamp Invariant [15.0, 30.0]

        [Theory]
        [InlineData(-1000f, 15.0f)]
        [InlineData(-50f, 15.0f)]
        [InlineData(0f, 15.0f)]
        [InlineData(5.0f, 15.0f)]
        [InlineData(14.999f, 15.0f)]
        [InlineData(15.0f, 15.0f)]
        [InlineData(15.001f, 15.001f)]
        [InlineData(20.0f, 20.0f)]
        [InlineData(25.5f, 25.5f)]
        [InlineData(29.999f, 29.999f)]
        [InlineData(30.0f, 30.0f)]
        [InlineData(30.001f, 30.0f)]
        [InlineData(50f, 30.0f)]
        [InlineData(1000f, 30.0f)]
        [InlineData(float.MinValue, 15.0f)]
        [InlineData(float.MaxValue, 30.0f)]
        public void CountdownTimer_RemainingSpectatorTime_StrictlyClampedTo15And30(float configDuration, float expectedTime)
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.IsPartnerAlive = true;
            coordinator.ReviveCooldownDuration = configDuration;

            coordinator.OnLocalPlayerDied(10f, 20f);

            Assert.Equal(DeathState.Spectating, coordinator.CurrentState);
            Assert.Equal(expectedTime, coordinator.RemainingSpectatorTime, 3);
            Assert.True(coordinator.RemainingSpectatorTime >= 15.0f, $"Timer below 15.0s: {coordinator.RemainingSpectatorTime}");
            Assert.True(coordinator.RemainingSpectatorTime <= 30.0f, $"Timer above 30.0s: {coordinator.RemainingSpectatorTime}");
        }

        [Fact]
        public void CountdownTimer_MonotonicallyDecreases_AndRevivesExactlyAtZero()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.IsPartnerAlive = true;
            coordinator.ReviveCooldownDuration = 20.0f;
            coordinator.LastPartnerPosition = (50.0f, 15.0f);

            coordinator.OnLocalPlayerDied(0f, 0f);
            Assert.Equal(20.0f, coordinator.RemainingSpectatorTime);

            float lastTime = coordinator.RemainingSpectatorTime;
            bool reviveFired = false;
            coordinator.OnReviveAtPartnerRequested += (_, _) => reviveFired = true;

            // Step in increments of 0.25s
            for (int i = 0; i < 79; i++)
            {
                coordinator.UpdateTick(0.25f);
                Assert.True(coordinator.RemainingSpectatorTime < lastTime, "Countdown must monotonically decrease.");
                Assert.Equal(DeathState.Spectating, coordinator.CurrentState);
                Assert.False(reviveFired);
                lastTime = coordinator.RemainingSpectatorTime;
            }

            // At 19.75s elapsed, remaining is 0.25s
            Assert.Equal(0.25f, coordinator.RemainingSpectatorTime, 3);

            // Final tick of 0.3s crosses 0
            coordinator.UpdateTick(0.3f);
            Assert.Equal(DeathState.RespawningAtPartner, coordinator.CurrentState);
            Assert.True(reviveFired, "Revive at partner must fire when timer reaches 0.");
        }

        #endregion

        #region 5. Control Restoration Invariant: cState.dead is false strictly before RegainControl()

        /// <summary>
        /// Simulation model of Hollow Knight: Silksong's decompiled HeroController.RegainControl contract.
        /// Demonstrates that if cState.dead is true, RegainControl() early-exits without restoring control.
        /// </summary>
        public class MockHeroControllerContract
        {
            public bool IsDead { get; set; } = true;
            public bool ControlRelinquished { get; set; } = true;
            public bool InputAllowed { get; private set; } = false;
            public List<string> CallTrace { get; } = new List<string>();

            public void ClearDeadState()
            {
                CallTrace.Add("ClearDeadState");
                IsDead = false;
            }

            public void RegainControl(bool allowInput)
            {
                CallTrace.Add($"RegainControl(allowInput={allowInput})");
                // Exact decompiled check from Assembly-CSharp HeroController.RegainControl(bool):
                // if (controlRelinquished && !cState.dead) { controlRelinquished = false; ... }
                if (ControlRelinquished && !IsDead)
                {
                    ControlRelinquished = false;
                    InputAllowed = allowInput;
                }
            }
        }

        [Fact]
        public void ControlRestoration_IfDeadTrue_RegainControlFails_ProvesInvariantNecessity()
        {
            // Adversarial test: Call RegainControl while dead is still true
            var hero = new MockHeroControllerContract();
            Assert.True(hero.IsDead);
            Assert.True(hero.ControlRelinquished);

            // Calling RegainControl while dead
            hero.RegainControl(allowInput: true);

            // CONTROL FAILS TO RESTORE!
            Assert.True(hero.ControlRelinquished, "Control remains relinquished if dead is true.");
            Assert.False(hero.InputAllowed, "Input is not allowed if dead is true.");
        }

        [Fact]
        public void ControlRestoration_WhenDeadClearedPriorToRegainControl_ControlRestoresSuccessfully()
        {
            var hero = new MockHeroControllerContract();
            Assert.True(hero.IsDead);
            Assert.True(hero.ControlRelinquished);

            // Proper engine revival sequence:
            hero.ClearDeadState();
            hero.RegainControl(allowInput: true);

            // CONTROL SUCCESSFULLY RESTORED!
            Assert.False(hero.ControlRelinquished, "Control must be restored.");
            Assert.True(hero.InputAllowed, "Input must be allowed.");
            Assert.Equal(new[] { "ClearDeadState", "RegainControl(allowInput=True)" }, hero.CallTrace);
        }

        [Fact]
        public void ControlRestoration_SourceCodeStaticAnalysis_GuaranteesStrictOrdering()
        {
            // Verify that in SilksongCoopPlugin.cs within ReviveAtPartner, "hero.cState.dead = false" occurs
            // BEFORE "hero.RegainControl" in the revival sequence!
            string pluginPath = FindRepoFile(Path.Combine("src", "SilksongCoop.Mod", "SilksongCoopPlugin.cs"));
            string revivePath = FindRepoFile(Path.Combine("src", "SilksongCoop.Mod", "SilksongCoopPlugin.ReviveFlow.cs"));

            Assert.True(File.Exists(pluginPath), $"SilksongCoopPlugin.cs must exist at {pluginPath}");

            var allLines = new List<string>(File.ReadAllLines(pluginPath));
            if (File.Exists(revivePath))
                allLines.AddRange(File.ReadAllLines(revivePath));
            string[] lines = allLines.ToArray();
            int clearDeadLine = -1;
            int regainControlLine = -1;
            bool insideReviveAtPartner = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Contains("ReviveAtPartner("))
                {
                    insideReviveAtPartner = true;
                }
                else if (insideReviveAtPartner && line.Contains("ReviveInvulnerabilityRoutine("))
                {
                    insideReviveAtPartner = false;
                }

                if (insideReviveAtPartner)
                {
                    if (line.Contains("hero.cState.dead = false"))
                    {
                        clearDeadLine = i + 1;
                    }
                    else if (line.Contains("hero.RegainControl"))
                    {
                        regainControlLine = i + 1;
                    }
                }
            }

            Assert.True(clearDeadLine > 0, "hero.cState.dead = false must be present inside ReviveAtPartner");
            Assert.True(regainControlLine > 0, "hero.RegainControl must be present inside ReviveAtPartner");
            Assert.True(clearDeadLine < regainControlLine,
                $"Invariant violation: hero.cState.dead = false (line {clearDeadLine}) must occur strictly BEFORE hero.RegainControl (line {regainControlLine}).");
        }

        /// <summary>
        /// Walks up from the test binaries until the repository root (SilksongCoop.sln) is
        /// found, so the static-analysis check does not depend on the output directory depth.
        /// </summary>
        private static string FindRepoFile(string relativePath)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SilksongCoop.sln")))
                    return Path.Combine(directory.FullName, relativePath);

                directory = directory.Parent;
            }

            return Path.GetFullPath(relativePath);
        }

        #endregion
    }
}
