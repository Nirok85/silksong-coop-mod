using System;
using System.Reflection;
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
        // Hero Detector triggers cannot provide physical support for a revived player.
        private static readonly int SoftTerrainLayer = LayerMask.NameToLayer("Soft Terrain");
        private static readonly int GroundLayerMask = BuildGroundLayerMask();

        private static int BuildGroundLayerMask()
        {
            int mask = 0;
            if (TerrainLayer >= 0) mask |= (1 << TerrainLayer);

            if (SoftTerrainLayer >= 0) mask |= (1 << SoftTerrainLayer);
            
            // Fallback to hardcoded values if layer names not found
            if (mask == 0)
            {
                mask = (1 << 8) | (1 << 25);
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

        public const int InitialOverlapBufferSize = 16;
        public const int MaxOverlapBufferSize = 128;
        public const float FloorInsetMargin = 0.1f;

        public const int InitialRayBufferSize = 16;
        public const int MaxRayBufferSize = 128;

        private RaycastHit2D[] _hitBuffer = new RaycastHit2D[InitialRayBufferSize];
        private Collider2D[] _overlapBuffer = new Collider2D[InitialOverlapBufferSize];
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
                var hero = HeroController.instance;
                if (hero == null || !TryGetHeroBounds(hero, out Bounds bodyBounds))
                {
                    safeX = partnerX;
                    safeY = partnerY;
                    return false;
                }
                Vector2 bodyOffset = (Vector2)(bodyBounds.center - hero.transform.position);
                Vector2 bodySize = bodyBounds.size;
                float extentsY = bodySize.y * 0.5f;

                // Probe candidate offsets in priority sequence
                foreach (float lateralOffset in SafeGroundSolver.CandidateOffsets)
                {
                    Vector2 rayOrigin = new Vector2(partnerX + lateralOffset, partnerY + SafeGroundSolver.ProbeRayOriginYOffset);
                    int hitCount = QueryGroundHits(rayOrigin);
                    if (hitCount < 0) continue;

                    RaycastHit2D closestHit = default;
                    float closestDist = float.MaxValue;
                    bool foundHit = false;

                    for (int i = 0; i < hitCount; i++)
                    {
                        var hit = _hitBuffer[i];
                        if (hit.collider == null || hit.collider.isTrigger || IsHeroCollider(hit.collider, hero.transform)) continue;

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
                        float bodyCenterX = closestHit.point.x;
                        float bodyCenterY = closestHit.point.y + extentsY + 0.01f;
                        float candidateX = bodyCenterX - bodyOffset.x;
                        float candidateY = bodyCenterY - bodyOffset.y;

                        // Clearance validation: verify candidate point doesn't intersect lethal hazard triggers
                        if (IsAreaFreeOfHazards(bodyCenterX, bodyCenterY, bodySize))
                        {
                            safeX = candidateX;
                            safeY = candidateY;
                            return true;
                        }
                    }
                }

                // Safe ground search failed across all candidate offsets.
                // Check if HeroController / PlayerData maintains a valid, hazard-free hazardRespawnLocation fallback.
                if (TryGetHazardRespawnLocation(hero, out Vector3 hazardLoc) &&
                    float.IsFinite(hazardLoc.x) && float.IsFinite(hazardLoc.y) &&
                    (hazardLoc.x != 0f || hazardLoc.y != 0f))
                {
                    float hzBodyCenterX = hazardLoc.x + bodyOffset.x;
                    float hzBodyCenterY = hazardLoc.y + bodyOffset.y;
                    if (IsAreaFreeOfHazards(hzBodyCenterX, hzBodyCenterY, bodySize))
                    {
                        safeX = hazardLoc.x;
                        safeY = hazardLoc.y;
                        return true;
                    }
                }

                // Safe ground search failed across all candidate offsets and fallback.
                // Caller cascades to bench respawn fallback rather than using unverified coordinates.
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

        private static bool TryGetHazardRespawnLocation(HeroController? hero, out Vector3 location)
        {
            location = Vector3.zero;
            if (hero == null) return false;

            try
            {
                var heroType = hero.GetType();
                var field = heroType.GetField("hazardRespawnLocation", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null && field.GetValue(hero) is Vector3 fieldLoc)
                {
                    location = fieldLoc;
                    return true;
                }

                var prop = heroType.GetProperty("hazardRespawnLocation", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null && prop.GetValue(hero) is Vector3 propLoc)
                {
                    location = propLoc;
                    return true;
                }

                // Check PlayerData.instance.hazardRespawnLocation via reflection (avoid compile dependency in RuntimeChecks)
                var pdType = heroType.Assembly.GetType("PlayerData");
                if (pdType != null)
                {
                    var pdInstProp = pdType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static);
                    object? pd = pdInstProp != null ? pdInstProp.GetValue(null) : null;
                    if (pd == null)
                    {
                        var pdInstField = pdType.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                        pd = pdInstField?.GetValue(null);
                    }

                    if (pd != null)
                    {
                        var pdField = pdType.GetField("hazardRespawnLocation", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (pdField != null && pdField.GetValue(pd) is Vector3 v)
                        {
                            location = v;
                            return true;
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        private static bool IsHeroCollider(Collider2D collider, Transform heroTransform) =>
            collider.transform == heroTransform || collider.transform.IsChildOf(heroTransform);

        private static bool TryGetHeroBounds(HeroController hero, out Bounds bounds)
        {
            var collider = hero.GetComponent<Collider2D>();
            bounds = default;
            if (collider == null || collider.isTrigger) return false;

            bounds = collider.bounds;
            if (collider.enabled && bounds.size.x > 0f && bounds.size.y > 0f)
                return IsFinite(bounds);

            // Unity reports empty bounds for a disabled collider during death. Reconstruct
            // known body shapes without enabling collision or changing the hero state.
            Vector2 localSize;
            if (collider is BoxCollider2D box) localSize = box.size;
            else if (collider is CapsuleCollider2D capsule) localSize = capsule.size;
            else if (collider is CircleCollider2D circle)
                localSize = new Vector2(circle.radius * 2f, circle.radius * 2f);
            else return false;

            if (localSize.x <= 0f || localSize.y <= 0f) return false;
            Vector2 half = localSize * 0.5f;
            Vector2 offset = collider.offset;
            var bodyTransform = collider.transform;
            bounds = new Bounds(bodyTransform.TransformPoint(new Vector3(offset.x - half.x, offset.y - half.y, 0f)), Vector3.zero);
            bounds.Encapsulate(bodyTransform.TransformPoint(new Vector3(offset.x + half.x, offset.y - half.y, 0f)));
            bounds.Encapsulate(bodyTransform.TransformPoint(new Vector3(offset.x - half.x, offset.y + half.y, 0f)));
            bounds.Encapsulate(bodyTransform.TransformPoint(new Vector3(offset.x + half.x, offset.y + half.y, 0f)));
            return bounds.size.x > 0f && bounds.size.y > 0f && IsFinite(bounds);
        }

        private static bool IsFinite(Bounds bounds) =>
            IsFinite(bounds.center.x) && IsFinite(bounds.center.y) &&
            IsFinite(bounds.size.x) && IsFinite(bounds.size.y);

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private int QueryGroundHits(Vector2 origin)
        {
            while (true)
            {
                int count = Physics2D.RaycastNonAlloc(origin, Vector2.down, _hitBuffer,
                    SafeGroundSolver.ProbeDistance, GroundLayerMask);
                if (count < _hitBuffer.Length) return count;
                if (_hitBuffer.Length >= MaxRayBufferSize)
                {
                    Array.Clear(_hitBuffer, 0, _hitBuffer.Length);
                    return -1; // An incomplete hit set cannot establish a safe support surface.
                }
                _hitBuffer = new RaycastHit2D[Math.Min(_hitBuffer.Length * 2, MaxRayBufferSize)];
            }
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

        internal bool IsAreaFreeOfHazards(float x, float y, Vector2 bodySize)
        {
            // Defect 10: Candidate spawn clearance box inset from the floor by FloorInsetMargin
            // so walkable floor directly beneath feet does not count as an obstacle, while
            // solid walls, ceilings, and obstacles intersecting the hero volume are detected.
            float floorInset = Math.Min(FloorInsetMargin, bodySize.y * 0.1f);
            float boxHeight = bodySize.y - floorInset;
            float centerY = y + (floorInset * 0.5f);
            Vector2 center = new Vector2(x, centerY);
            Vector2 size = new Vector2(bodySize.x, boxHeight);

            int overlapCount;
            while (true)
            {
                overlapCount = Physics2D.OverlapBox(center, size, 0f, _contactFilter, _overlapBuffer);

                // Defect 11: If overlap buffer is saturated, dynamically double buffer up to MaxOverlapBufferSize (128).
                if (overlapCount >= _overlapBuffer.Length)
                {
                    if (_overlapBuffer.Length < MaxOverlapBufferSize)
                    {
                        int newSize = Math.Min(_overlapBuffer.Length * 2, MaxOverlapBufferSize);
                        _overlapBuffer = new Collider2D[newSize];
                        continue;
                    }
                    else
                    {
                        // Count exceeds the maximum cap of 128 colliders. Safely fail the area check
                        // (treat as unsafe/blocked) to avoid truncation of hazards or solid colliders.
                        Array.Clear(_overlapBuffer, 0, _overlapBuffer.Length);
                        SilksongCoopPlugin.LogWarn($"[UnityGroundProber] Overlap buffer cap ({MaxOverlapBufferSize}) exceeded in IsAreaFreeOfHazards at ({x:F1}, {y:F1}). Rejecting area as unsafe.");
                        return false;
                    }
                }
                break;
            }

            var hero = HeroController.instance;
            var heroTransform = hero != null ? hero.transform : null;

            try
            {
                for (int i = 0; i < overlapCount; i++)
                {
                    var col = _overlapBuffer[i];
                    if (col == null) continue;

                    // Exclude hero's own colliders
                    if (heroTransform != null && IsHeroCollider(col, heroTransform))
                    {
                        continue;
                    }

                    // Screen against environmental hazards (triggers, spikes, acid, lava)
                    if (IsHazardCollider(col))
                    {
                        return false;
                    }

                    // Screen against solid non-trigger geometry (walls, ceilings, solid obstacles)
                    if (!col.isTrigger)
                    {
                        return false;
                    }
                }

                return true;
            }
            finally
            {
                Array.Clear(_overlapBuffer, 0, overlapCount);
            }
        }
    }
}
