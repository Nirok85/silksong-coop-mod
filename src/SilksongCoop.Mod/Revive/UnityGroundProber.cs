using System;
using GlobalEnums;
using SilksongCoop.Core.Revive;
using UnityEngine;

namespace SilksongCoop.Mod.Revive
{
    /// <summary>
    /// Unity Physics2D-based ground prober for Hollow Knight: Silksong.
    /// Executes downward raycasts across candidate lateral offsets, screens hits against
    /// environmental hazard colliders (spikes, acid, lava), and applies native collider contact offsets.
    /// </summary>
    public class UnityGroundProber : IGroundProber
    {
        // Fix: Use LayerMask.NameToLayer instead of hardcoded layer numbers
        // Layers: 8 (TERRAIN), 13 (HERO_DETECTOR), 25 (SOFT_TERRAIN)
        private static readonly int TerrainLayer = LayerMask.NameToLayer("Terrain");
        private static readonly int HeroDetectorLayer = LayerMask.NameToLayer("Hero Detector");
        private static readonly int SoftTerrainLayer = LayerMask.NameToLayer("Soft Terrain");
        private static readonly int GroundLayerMask = BuildGroundLayerMask();

        private static int BuildGroundLayerMask()
        {
            int mask = 0;
            if (TerrainLayer >= 0) mask |= (1 << TerrainLayer);
            if (HeroDetectorLayer >= 0) mask |= (1 << HeroDetectorLayer);
            if (SoftTerrainLayer >= 0) mask |= (1 << SoftTerrainLayer);
            
            // Fallback to hardcoded values if layer names not found
            if (mask == 0)
            {
                mask = (1 << 8) | (1 << 13) | (1 << 25);
            }
            return mask;
        }

        // CompareTag throws UnityException for tags that are not defined in this game's
        // TagManager. Silksong does not define "Hazard"/"Spikes", so probing a normal
        // terrain collider used to throw and abort the whole safe-ground search.
        private static readonly bool HazardTagDefined = IsTagDefined("Hazard");
        private static readonly bool SpikesTagDefined = IsTagDefined("Spikes");

        private static bool IsTagDefined(string tag)
        {
            try
            {
                GameObject.FindWithTag(tag);
                return true;
            }
            catch (UnityException)
            {
                return false;
            }
        }

        private readonly RaycastHit2D[] _hitBuffer = new RaycastHit2D[16];
        private readonly Collider2D[] _overlapBuffer = new Collider2D[16];
        private readonly ContactFilter2D _contactFilter = new ContactFilter2D { useTriggers = true };

        /// <summary>
        /// Attempts to find safe ground near the partner's position using raycasting.
        /// </summary>
        /// <param name="partnerX">Partner's X position.</param>
        /// <param name="partnerY">Partner's Y position.</param>
        /// <param name="safeX">Output X coordinate of safe ground.</param>
        /// <param name="safeY">Output Y coordinate of safe ground.</param>
        /// <returns>True if safe ground was found, false otherwise.</returns>
        public bool TryFindSafeGround(float partnerX, float partnerY, out float safeX, out float safeY)
        {
            try
            {
                // Determine hero collider extents
                float extentsY = 0.8f;
                float offsetY = 0f;
                var hero = HeroController.instance;
                if (hero != null)
                {
                    var col = hero.GetComponent<Collider2D>();
                    if (col != null)
                    {
                        extentsY = col.bounds.extents.y;
                        offsetY = col.offset.y;
                    }
                }

                // Probe candidate offsets in priority sequence
                foreach (float lateralOffset in SafeGroundSolver.CandidateOffsets)
                {
                    Vector2 rayOrigin = new Vector2(partnerX + lateralOffset, partnerY + SafeGroundSolver.ProbeRayOriginYOffset);
                    int hitCount = Physics2D.RaycastNonAlloc(rayOrigin, Vector2.down, _hitBuffer, SafeGroundSolver.ProbeDistance, GroundLayerMask);

                    RaycastHit2D closestHit = default;
                    float closestDist = float.MaxValue;
                    bool foundHit = false;

                    for (int i = 0; i < hitCount; i++)
                    {
                        var hit = _hitBuffer[i];
                        if (hit.collider == null) continue;

                        // Ensure surface slope is climbable (<= 45 degrees)
                        if (hit.normal.y < SafeGroundSolver.MinGroundNormalY) continue;

                        // Screen against environmental hazards
                        if (IsHazardCollider(hit.collider)) continue;

                        if (hit.distance < closestDist)
                        {
                            closestDist = hit.distance;
                            closestHit = hit;
                            foundHit = true;
                        }
                    }

                    if (foundHit)
                    {
                        float candidateX = closestHit.point.x;
                        float candidateY = closestHit.point.y + extentsY - offsetY + 0.01f;

                        // Clearance validation: verify candidate point doesn't intersect lethal hazard triggers
                        if (IsAreaFreeOfHazards(candidateX, candidateY, extentsY))
                        {
                            safeX = candidateX;
                            safeY = candidateY;
                            return true;
                        }
                    }
                }

                // BUG-B3: the old hazardRespawnLocation fallback returned TRUE with a stale
                // hazard marker (possibly from another room entirely). Report failure and
                // let the caller use the partner's own position — a live partner is standing
                // there, so it is ground-truth safe.
                safeX = partnerX;
                safeY = partnerY + 0.5f;
                return false;
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[UnityGroundProber] TryFindSafeGround error: {ex.Message}");
                safeX = partnerX;
                safeY = partnerY + 0.5f;
                return false;
            }
        }

        /// <summary>
        /// Calculates the health value to restore on revive.
        /// </summary>
        /// <param name="maxHealth">Maximum health value.</param>
        /// <returns>Health value to restore (typically 50% of max).</returns>
        public int CalculateReviveHealth(int maxHealth)
        {
            return SafeGroundSolver.CalculateReviveHealth(maxHealth);
        }

        private bool IsHazardCollider(Collider2D collider)
        {
            if (collider == null) return false;

            // Check DamageHero component
            var damageHero = collider.GetComponentInParent<DamageHero>();
            if (damageHero != null && damageHero.hazardType != HazardType.NON_HAZARD)
            {
                return true;
            }

            // Tag check (only for tags that actually exist in this game build)
            if (HazardTagDefined && collider.CompareTag("Hazard"))
            {
                return true;
            }
            if (SpikesTagDefined && collider.CompareTag("Spikes"))
            {
                return true;
            }

            return false;
        }

        private bool IsAreaFreeOfHazards(float x, float y, float extentsY)
        {
            Vector2 center = new Vector2(x, y);
            Vector2 size = new Vector2(0.8f, extentsY * 2f);

            int overlapCount = Physics2D.OverlapBox(center, size, 0f, _contactFilter, _overlapBuffer);
            for (int i = 0; i < overlapCount; i++)
            {
                var col = _overlapBuffer[i];
                if (col != null && IsHazardCollider(col))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
