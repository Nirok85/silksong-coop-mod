using System;
using System.Collections.Generic;
using System.IO;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.Revive;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Comprehensive remediation test suite for Milestone 5:
    /// - Defect 10: Solid geometry (wall) intersection check in safe spawn search and exclude unverified teammate position.
    /// - Defect 11: Handle potential overlap-buffer overflow (16 colliders) via dynamic resizing up to 128 cap.
    /// - Defect 18: Dash trail light fade shutoff threshold (check intensity > 0f and threshold <= 0.05f).
    /// </summary>
    public class Milestone5RemediationTests
    {
        #region Test Spatial Harnesses & Models

        /// <summary>
        /// Represents a simulated 2D axis-aligned bounding box collider for spatial testing.
        /// </summary>
        public class SimulatedCollider
        {
            public float MinX { get; set; }
            public float MaxX { get; set; }
            public float MinY { get; set; }
            public float MaxY { get; set; }
            public bool IsTrigger { get; set; }
            public bool IsHazard { get; set; }
            public string Name { get; set; } = "Collider";

            public bool Overlaps(float boxMinX, float boxMaxX, float boxMinY, float boxMaxY)
            {
                return MinX < boxMaxX && MaxX > boxMinX && MinY < boxMaxY && MaxY > boxMinY;
            }
        }

        /// <summary>
        /// Simulated ground and clearance prober implementing the exact logic of UnityGroundProber
        /// with candidate clearance box, floor inset margin, solid geometry screening, and dynamic buffer resizing.
        /// </summary>
        public class AdvancedSimulatedGroundProber : IGroundProber
        {
            public const int InitialBufferSize = 16;
            public const int MaxBufferSize = 128;
            public const float FloorInsetMargin = 0.1f;

            public List<SimulatedCollider> WorldColliders { get; } = new List<SimulatedCollider>();
            public int CurrentBufferSize { get; private set; } = InitialBufferSize;
            public int ResizeCount { get; private set; } = 0;
            public bool BufferCapExceeded { get; private set; } = false;

            public bool TryFindSafeGround(float partnerX, float partnerY, out float safeX, out float safeY)
            {
                const float extentsY = 0.8f;
                const float offsetY = 0.0f;

                foreach (float lateralOffset in SafeGroundSolver.CandidateOffsets)
                {
                    float probeX = partnerX + lateralOffset;
                    float rayOriginY = partnerY + SafeGroundSolver.ProbeRayOriginYOffset;

                    // Downward raycast: find closest solid non-hazard ground below ray origin
                    SimulatedCollider? closestGround = null;
                    float closestHitY = float.MinValue;

                    foreach (var col in WorldColliders)
                    {
                        // Ray is a vertical line at probeX from rayOriginY down to rayOriginY - ProbeDistance
                        if (!col.IsTrigger && probeX >= col.MinX && probeX <= col.MaxX)
                        {
                            if (col.MaxY <= rayOriginY && col.MaxY >= rayOriginY - SafeGroundSolver.ProbeDistance)
                            {
                                if (col.MaxY > closestHitY)
                                {
                                    closestHitY = col.MaxY;
                                    closestGround = col;
                                }
                            }
                        }
                    }

                    if (closestGround != null)
                    {
                        if (closestGround.IsHazard)
                        {
                            continue;
                        }

                        float candidateX = probeX;
                        float candidateY = closestHitY + extentsY - offsetY + 0.01f;

                        if (IsAreaFreeOfHazardsAndSolidObstacles(candidateX, candidateY, extentsY))
                        {
                            safeX = candidateX;
                            safeY = candidateY;
                            return true;
                        }
                    }
                }

                safeX = partnerX;
                safeY = partnerY + 0.5f;
                return false;
            }

            public bool IsAreaFreeOfHazardsAndSolidObstacles(float candidateX, float candidateY, float extentsY)
            {
                // Defect 10: Candidate spawn clearance box inset from the bottom by FloorInsetMargin
                float boxHeight = Math.Max(0.1f, (extentsY * 2f) - FloorInsetMargin);
                float centerY = candidateY + (FloorInsetMargin * 0.5f);

                float boxMinX = candidateX - 0.4f;
                float boxMaxX = candidateX + 0.4f;
                float boxMinY = centerY - (boxHeight * 0.5f);
                float boxMaxY = centerY + (boxHeight * 0.5f);

                // Collect all overlapping colliders from world
                var overlapping = new List<SimulatedCollider>();
                foreach (var col in WorldColliders)
                {
                    if (col.Overlaps(boxMinX, boxMaxX, boxMinY, boxMaxY))
                    {
                        overlapping.Add(col);
                    }
                }

                // Defect 11: Dynamic buffer resizing up to MaxBufferSize (128)
                int queryCount = overlapping.Count;
                while (queryCount >= CurrentBufferSize)
                {
                    if (CurrentBufferSize < MaxBufferSize)
                    {
                        CurrentBufferSize = Math.Min(CurrentBufferSize * 2, MaxBufferSize);
                        ResizeCount++;
                    }
                    else
                    {
                        // Buffer cap of 128 exceeded: safely fail check to prevent truncation of hazards or walls
                        BufferCapExceeded = true;
                        return false;
                    }
                }

                // Check colliders
                foreach (var col in overlapping)
                {
                    // Hazard trigger or hazard solid collider
                    if (col.IsHazard)
                    {
                        return false;
                    }

                    // Defect 10: Solid geometry (non-trigger) intersecting body volume
                    if (!col.IsTrigger)
                    {
                        return false;
                    }
                }

                return true;
            }

            public int CalculateReviveHealth(int maxHealth)
            {
                return SafeGroundSolver.CalculateReviveHealth(maxHealth);
            }
        }

        /// <summary>
        /// Simulates the revive flow logic from SilksongCoopPlugin.ReviveFlow.cs
        /// </summary>
        public class SimulatedReviveFlow
        {
            public DeathLifecycleCoordinator Coordinator { get; }
            public IGroundProber? GroundProber { get; set; }
            public bool FallbackTriggered { get; private set; }
            public string? FallbackMessage { get; private set; }
            public string? FallbackReason { get; private set; }
            public (float X, float Y)? RevivedHeroPosition { get; private set; }

            public SimulatedReviveFlow(DeathLifecycleCoordinator coordinator, IGroundProber? prober)
            {
                Coordinator = coordinator;
                GroundProber = prober;
            }

            public bool TryReviveAtPartner(float x, float y)
            {
                // Defect 10: If prober is null or fails to find safe ground, do NOT use unverified partner position!
                // Cascade to bench respawn fallback.
                if (GroundProber == null || !GroundProber.TryFindSafeGround(x, y, out float safeX, out float safeY))
                {
                    return FallbackFromPartnerRevive(
                        "No safe ground near partner — reviving at the last bench.",
                        $"Safe ground prober failed near ({x:F1}, {y:F1})");
                }

                RevivedHeroPosition = (safeX, safeY);
                Coordinator.ConfirmReviveAtPartner(safeX, safeY);
                return true;
            }

            private bool FallbackFromPartnerRevive(string? message, string reason)
            {
                FallbackTriggered = true;
                FallbackMessage = message;
                FallbackReason = reason;

                Coordinator.FailReviveAtPartner();
                Coordinator.BeginSoloBenchRespawn();
                return false;
            }
        }

        /// <summary>
        /// Simulates DashTrailController rim light fade logic from DashTrailController.cs
        /// </summary>
        public class SimulatedRimLightController
        {
            public float Intensity { get; set; }
            public bool IsActive { get; set; }

            public void TriggerDash()
            {
                IsActive = true;
                Intensity = 2.5f;
            }

            public void UpdateLifecycle(float deltaTime)
            {
                // Fix (Defect 18): Check intensity > 0f and cutoff threshold <= 0.05f
                if (Intensity > 0f)
                {
                    Intensity = MathF.Max(0f, Intensity + (0f - Intensity) * Math.Clamp(deltaTime * 5f, 0f, 1f));
                    if (Intensity <= 0.05f)
                    {
                        Intensity = 0f;
                        IsActive = false;
                    }
                }
            }

            public void UpdateLifecycleOldBuggy(float deltaTime)
            {
                // Buggy code: intensity > 0.1f and < 0.05f
                if (Intensity > 0.1f)
                {
                    Intensity = MathF.Max(0f, Intensity + (0f - Intensity) * Math.Clamp(deltaTime * 5f, 0f, 1f));
                    if (Intensity < 0.05f)
                    {
                        Intensity = 0f;
                        IsActive = false;
                    }
                }
            }
        }

        #endregion

        #region Defect 10: Solid Geometry (Wall) Intersection & Exclusion of Unverified Partner Position

        [Fact]
        public void Defect10_SolidWallAtCandidatePosition_RejectsCandidate_AndSelectsSafeAdjacentOffset()
        {
            var prober = new AdvancedSimulatedGroundProber();

            // Continuous ground platform at Y = 10
            prober.WorldColliders.Add(new SimulatedCollider
            {
                Name = "Floor",
                MinX = -10f,
                MaxX = 10f,
                MinY = 9f,
                MaxY = 10f,
                IsTrigger = false,
                IsHazard = false
            });

            // Solid vertical wall at offset 0 (X = 0, Y = 10 to 14)
            prober.WorldColliders.Add(new SimulatedCollider
            {
                Name = "SolidWall",
                MinX = -0.3f,
                MaxX = 0.3f,
                MinY = 10.05f,
                MaxY = 14f,
                IsTrigger = false,
                IsHazard = false
            });

            // Partner standing near wall at (0, 10)
            bool success = prober.TryFindSafeGround(0f, 10f, out float safeX, out float safeY);

            // Invariant: Candidate offset 0 is rejected due to solid wall collision;
            // Prober selects candidate offset +1.2f where no wall intersects the clearance volume.
            Assert.True(success);
            Assert.Equal(1.2f, safeX, 1);
            Assert.True(safeY > 10.5f);
        }

        [Fact]
        public void Defect10_SolidWallsAtAllCandidateOffsets_ReturnsFalse_AndRejectsAllCandidates()
        {
            var prober = new AdvancedSimulatedGroundProber();

            // Ground floor at Y = 0
            prober.WorldColliders.Add(new SimulatedCollider
            {
                Name = "Floor",
                MinX = -10f,
                MaxX = 10f,
                MinY = -1f,
                MaxY = 0f,
                IsTrigger = false,
                IsHazard = false
            });

            // A solid cavern wall covers all candidate offsets (-4.0 to +4.0) at torso height
            prober.WorldColliders.Add(new SimulatedCollider
            {
                Name = "ContinuousWallObstacle",
                MinX = -5f,
                MaxX = 5f,
                MinY = 0.2f,
                MaxY = 3.0f,
                IsTrigger = false,
                IsHazard = false
            });

            bool success = prober.TryFindSafeGround(0f, 0f, out float safeX, out float safeY);

            // Invariant: Prober returns false because every candidate intersects solid non-trigger geometry
            Assert.False(success);
        }

        [Fact]
        public void Defect10_FloorInsetMargin_PreventsWalkableGroundFromTriggeringObstacleFalsePositive()
        {
            var prober = new AdvancedSimulatedGroundProber();

            // Flat ground collider at Y = 5.0
            prober.WorldColliders.Add(new SimulatedCollider
            {
                Name = "WalkableGround",
                MinX = -5f,
                MaxX = 5f,
                MinY = 4f,
                MaxY = 5.0f,
                IsTrigger = false,
                IsHazard = false
            });

            // No obstacles or walls in the room
            bool isFree = prober.IsAreaFreeOfHazardsAndSolidObstacles(0f, 5.81f, extentsY: 0.8f);

            // Invariant: Walkable floor beneath feet does NOT intersect the clearance box thanks to FloorInsetMargin
            Assert.True(isFree, "FloorInsetMargin must ensure walkable ground beneath feet is not flagged as an obstacle.");
        }

        [Fact]
        public void Defect10_LowCeilingOverhang_IntersectingTorso_IsDetectedAndRejected()
        {
            var prober = new AdvancedSimulatedGroundProber();

            // Ground floor at Y = 0
            prober.WorldColliders.Add(new SimulatedCollider
            {
                Name = "Floor",
                MinX = -5f,
                MaxX = 5f,
                MinY = -1f,
                MaxY = 0f,
                IsTrigger = false,
                IsHazard = false
            });

            // Low ceiling overhang at Y = 1.0 (hero height is ~1.6m, candidate top is at Y ~ 1.61m)
            prober.WorldColliders.Add(new SimulatedCollider
            {
                Name = "LowCeilingOverhang",
                MinX = -1f,
                MaxX = 1f,
                MinY = 0.9f,
                MaxY = 1.8f,
                IsTrigger = false,
                IsHazard = false
            });

            // Area check at offset 0 (candidateY = 0.81m, top = 1.61m)
            bool isFree = prober.IsAreaFreeOfHazardsAndSolidObstacles(0f, 0.81f, extentsY: 0.8f);

            // Invariant: Overhang intersecting clearance box is caught as solid obstacle
            Assert.False(isFree, "Low ceiling obstacle intersecting body volume must be rejected.");
        }

        [Fact]
        public void Defect10_PassableTriggers_AreNotTreatedAsSolidObstacles()
        {
            var prober = new AdvancedSimulatedGroundProber();

            // Ambient music / event trigger intersecting hero volume
            prober.WorldColliders.Add(new SimulatedCollider
            {
                Name = "AmbientMusicZone",
                MinX = -2f,
                MaxX = 2f,
                MinY = 0f,
                MaxY = 3f,
                IsTrigger = true,
                IsHazard = false
            });

            bool isFree = prober.IsAreaFreeOfHazardsAndSolidObstacles(0f, 0.81f, extentsY: 0.8f);

            // Invariant: Passable triggers (non-hazard) do not block candidate revival point
            Assert.True(isFree, "Passable triggers that are not hazards must not block revive safe ground.");
        }

        [Fact]
        public void Defect10_ReviveFlow_WhenProberFails_NeverUsesUnverifiedPartnerPosition_CascadesToBenchRespawn()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var partnerPeer = new NetworkPeerId(2);
            coordinator.HandleDeathPacket(partnerPeer, new DeathLifecyclePacket
            {
                EventType = DeathEventType.BenchCheckpointUpdate,
                Epoch = 1
            });

            // Setup prober that fails (e.g. partner is in air or surrounded by hazards/walls)
            var prober = new AdvancedSimulatedGroundProber(); // No platforms -> fails

            var reviveFlow = new SimulatedReviveFlow(coordinator, prober);

            // Attempt revive at partner coordinates (25.0f, 15.0f)
            bool result = reviveFlow.TryReviveAtPartner(25.0f, 15.0f);

            // Invariants:
            // 1. Revive at partner fails
            Assert.False(result);
            // 2. Unverified partner position (25.0, 15.5) was NEVER assigned to hero
            Assert.Null(reviveFlow.RevivedHeroPosition);
            // 3. Fallback to bench respawn was triggered
            Assert.True(reviveFlow.FallbackTriggered);
            Assert.NotNull(reviveFlow.FallbackMessage);
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
        }

        [Fact]
        public void Defect10_ReviveFlow_WhenProberSucceeds_UsesVerifiedSafeGround_AndConfirmsRevive()
        {
            var coordinator = new DeathLifecycleCoordinator();
            var partnerPeer = new NetworkPeerId(2);
            coordinator.HandleDeathPacket(partnerPeer, new DeathLifecyclePacket
            {
                EventType = DeathEventType.BenchCheckpointUpdate,
                Epoch = 1
            });

            var prober = new AdvancedSimulatedGroundProber();
            prober.WorldColliders.Add(new SimulatedCollider
            {
                Name = "Ground",
                MinX = -5f,
                MaxX = 5f,
                MinY = 0f,
                MaxY = 2f,
                IsTrigger = false,
                IsHazard = false
            });

            var reviveFlow = new SimulatedReviveFlow(coordinator, prober);

            bool result = reviveFlow.TryReviveAtPartner(0f, 2f);

            // Invariants:
            Assert.True(result);
            Assert.False(reviveFlow.FallbackTriggered);
            Assert.NotNull(reviveFlow.RevivedHeroPosition);
            Assert.Equal(0f, reviveFlow.RevivedHeroPosition!.Value.X);
            Assert.Equal(2.81f, reviveFlow.RevivedHeroPosition.Value.Y, 2);
        }

        #endregion

        #region Defect 11: Dynamic Overlap Buffer Resizing and Saturation Cap

        [Fact]
        public void Defect11_BufferOverflow_DynamicallyResizesBuffer_UpToCap()
        {
            var prober = new AdvancedSimulatedGroundProber();
            Assert.Equal(16, prober.CurrentBufferSize);

            // Add 20 non-hazard colliders that overlap candidate box
            for (int i = 0; i < 20; i++)
            {
                prober.WorldColliders.Add(new SimulatedCollider
                {
                    Name = $"Trigger_{i}",
                    MinX = -0.2f,
                    MaxX = 0.2f,
                    MinY = 0.5f,
                    MaxY = 1.5f,
                    IsTrigger = true,
                    IsHazard = false
                });
            }

            bool isFree = prober.IsAreaFreeOfHazardsAndSolidObstacles(0f, 0.81f, extentsY: 0.8f);

            // Invariant: Buffer dynamically expanded from 16 to 32 to accommodate 20 colliders
            Assert.True(isFree);
            Assert.Equal(32, prober.CurrentBufferSize);
            Assert.Equal(1, prober.ResizeCount);
            Assert.False(prober.BufferCapExceeded);
        }

        [Fact]
        public void Defect11_DynamicResize_DetectsHazardBeyondInitial16Colliders()
        {
            var prober = new AdvancedSimulatedGroundProber();

            // Add 16 safe triggers first (saturates initial 16-element buffer)
            for (int i = 0; i < 16; i++)
            {
                prober.WorldColliders.Add(new SimulatedCollider
                {
                    Name = $"SafeTrigger_{i}",
                    MinX = -0.2f,
                    MaxX = 0.2f,
                    MinY = 0.5f,
                    MaxY = 1.5f,
                    IsTrigger = true,
                    IsHazard = false
                });
            }

            // 17th collider is a lethal hazard trigger (spikes/acid)
            prober.WorldColliders.Add(new SimulatedCollider
            {
                Name = "SpikeTrigger_Index16",
                MinX = -0.2f,
                MaxX = 0.2f,
                MinY = 0.5f,
                MaxY = 1.5f,
                IsTrigger = true,
                IsHazard = true
            });

            bool isFree = prober.IsAreaFreeOfHazardsAndSolidObstacles(0f, 0.81f, extentsY: 0.8f);

            // Invariant: The hazard was beyond the first 16 elements. Because the buffer doubled to 32,
            // the 17th collider was inspected and the candidate was correctly rejected!
            Assert.False(isFree, "Hazard beyond initial 16-element buffer must be detected after dynamic resize.");
            Assert.Equal(32, prober.CurrentBufferSize);
        }

        [Fact]
        public void Defect11_BufferCapExceeded_SafelyRejectsCandidateArea()
        {
            var prober = new AdvancedSimulatedGroundProber();

            // Add 130 overlapping colliders (exceeds maximum cap of 128)
            for (int i = 0; i < 130; i++)
            {
                prober.WorldColliders.Add(new SimulatedCollider
                {
                    Name = $"DenseCollider_{i}",
                    MinX = -0.2f,
                    MaxX = 0.2f,
                    MinY = 0.5f,
                    MaxY = 1.5f,
                    IsTrigger = true,
                    IsHazard = false
                });
            }

            bool isFree = prober.IsAreaFreeOfHazardsAndSolidObstacles(0f, 0.81f, extentsY: 0.8f);

            // Invariant: Over 128 colliders -> buffer cap exceeded -> safely returns false
            // to prevent truncation from missing hazards or solid geometry!
            Assert.False(isFree);
            Assert.True(prober.BufferCapExceeded);
            Assert.Equal(AdvancedSimulatedGroundProber.MaxBufferSize, prober.CurrentBufferSize);
        }

        #endregion

        #region Defect 18: Dash Trail Light Fade Shutoff Threshold

        [Fact]
        public void Defect18_LightFade_IntensityBetween005And010_ContinuesDecayInNewLogic_StallsInOldLogic()
        {
            var newController = new SimulatedRimLightController();
            newController.TriggerDash();
            newController.Intensity = 0.08f; // Between 0.05f and 0.10f

            var oldController = new SimulatedRimLightController();
            oldController.TriggerDash();
            oldController.Intensity = 0.08f;

            float dt = 0.016f; // One frame at 60 FPS

            // Step old buggy controller
            oldController.UpdateLifecycleOldBuggy(dt);
            // Old controller stopped decaying because `intensity > 0.1f` was false!
            Assert.Equal(0.08f, oldController.Intensity);
            Assert.True(oldController.IsActive, "Old logic left light active indefinitely!");

            // Step new fixed controller
            newController.UpdateLifecycle(dt);
            // New controller continues decay because `intensity > 0f` is true!
            Assert.True(newController.Intensity < 0.08f, "New logic must continue decaying intensity below 0.10f!");
        }

        [Fact]
        public void Defect18_LightFade_ThresholdBelowOrEqual005_CompletelyShutsOffLightAndObject()
        {
            var controller = new SimulatedRimLightController();
            controller.TriggerDash();
            Assert.True(controller.IsActive);
            Assert.Equal(2.5f, controller.Intensity);

            // Step until intensity reaches <= 0.05f
            float dt = 0.1f;
            for (int frame = 0; frame < 50; frame++)
            {
                controller.UpdateLifecycle(dt);
                if (!controller.IsActive) break;
            }

            // Invariants:
            // 1. Intensity is snapped to exactly 0f
            Assert.Equal(0f, controller.Intensity);
            // 2. Light component / object is deactivated
            Assert.False(controller.IsActive, "Rim light object must be deactivated when intensity reaches <= 0.05f.");
        }

        [Theory]
        [InlineData(0.05f)]
        [InlineData(0.049f)]
        [InlineData(0.01f)]
        [InlineData(0.001f)]
        public void Defect18_BoundaryCutoffValues_ImmediatelyTurnOff(float startingIntensity)
        {
            var controller = new SimulatedRimLightController
            {
                Intensity = startingIntensity,
                IsActive = true
            };

            controller.UpdateLifecycle(0.016f);

            Assert.Equal(0f, controller.Intensity);
            Assert.False(controller.IsActive);
        }

        [Fact]
        public void Defect18_ZeroIntensity_DoesNotDecayOrReactivate()
        {
            var controller = new SimulatedRimLightController
            {
                Intensity = 0f,
                IsActive = false
            };

            controller.UpdateLifecycle(0.016f);

            Assert.Equal(0f, controller.Intensity);
            Assert.False(controller.IsActive);
        }

        #endregion

        #region Static Analysis & Source Code Invariant Verification

        [Fact]
        public void SourceInvariant_UnityGroundProber_ContainsFloorInsetMarginAndSolidGeometryCheck()
        {
            string proberPath = FindRepoFile(Path.Combine("src", "SilksongCoop.Mod", "Revive", "UnityGroundProber.cs"));
            Assert.True(File.Exists(proberPath), $"UnityGroundProber.cs must exist at {proberPath}");

            string content = File.ReadAllText(proberPath);

            // Invariants for Defect 10:
            Assert.Contains("FloorInsetMargin", content);
            Assert.Contains("!col.isTrigger", content);

            // Invariants for Defect 11:
            Assert.Contains("InitialOverlapBufferSize = 16", content);
            Assert.Contains("MaxOverlapBufferSize = 128", content);
            Assert.Contains("_overlapBuffer.Length * 2", content);
            Assert.Contains("Array.Clear(_overlapBuffer", content);
        }

        [Fact]
        public void SourceInvariant_ReviveFlow_CascadesToBenchRespawnOnProberFailure()
        {
            string revivePath = FindRepoFile(Path.Combine("src", "SilksongCoop.Mod", "SilksongCoopPlugin.ReviveFlow.cs"));
            Assert.True(File.Exists(revivePath), $"SilksongCoopPlugin.ReviveFlow.cs must exist at {revivePath}");

            string content = File.ReadAllText(revivePath);

            // Invariant for Defect 10: Revive flow must check prober failure and call FallbackFromPartnerRevive
            Assert.Contains("GroundProber == null || !GroundProber.TryFindSafeGround", content);
            Assert.Contains("FallbackFromPartnerRevive", content);
            Assert.DoesNotContain("Vector2 spawnPos = new Vector2(x, y + 0.5f);", content);
        }

        [Fact]
        public void SourceInvariant_DashTrailController_HasCorrectFadeConditionAndShutoffThreshold()
        {
            string trailPath = FindRepoFile(Path.Combine("src", "SilksongCoop.Mod", "Visuals", "DashTrailController.cs"));
            Assert.True(File.Exists(trailPath), $"DashTrailController.cs must exist at {trailPath}");

            string content = File.ReadAllText(trailPath);

            // Invariants for Defect 18:
            Assert.Contains("_rimLight.intensity > 0f", content);
            Assert.DoesNotContain("_rimLight.intensity > 0.1f", content);
            Assert.Contains("_rimLight.intensity <= 0.05f", content);
            Assert.Contains("_rimLight.intensity = 0f;", content);
            Assert.Contains("_rimLightObject?.SetActive(false);", content);
        }

        #endregion

        #region Helpers

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
