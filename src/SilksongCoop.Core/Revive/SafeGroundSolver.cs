using System;

namespace SilksongCoop.Core.Revive
{
    /// <summary>
    /// Engine-agnostic solver providing candidate lateral probe offsets, geometric constants,
    /// and health calculation formulas for safe ground revival.
    /// </summary>
    public static class SafeGroundSolver
    {
        /// <summary>
        /// Ordered candidate lateral offsets (in Unity world units) searched around the partner's position.
        /// Priority: directly below (0), small right (+1.2), small left (-1.2), medium right (+2.4),
        /// medium left (-2.4), wide right (+3.6), wide left (-3.6).
        /// </summary>
        public static readonly float[] CandidateOffsets = { 0f, 1.2f, -1.2f, 2.4f, -2.4f, 3.6f, -3.6f };

        /// <summary>
        /// Maximum downward raycast probing distance in world units.
        /// </summary>
        public const float ProbeDistance = 12.0f;

        /// <summary>
        /// Downward probe start height above the partner in world units to clear slopes/steps.
        /// </summary>
        public const float ProbeRayOriginYOffset = 1.0f;

        /// <summary>
        /// Minimum normal Y component for a valid ground surface (0.707f corresponds to <= 45 degree slopes).
        /// Rejects sheer walls and ceilings.
        /// </summary>
        public const float MinGroundNormalY = 0.707f;

        /// <summary>
        /// Duration of damage immunity upon partner revival in seconds.
        /// </summary>
        public const float InvulnerabilityDuration = 2.5f;

        /// <summary>
        /// Calculates revive health according to the specification formula: Math.Max(1, maxHealth / 2).
        /// </summary>
        /// <param name="maxHealth">The player's current maximum health value.</param>
        /// <returns>At least 1 health, or half of maxHealth.</returns>
        public static int CalculateReviveHealth(int maxHealth)
        {
            return Math.Max(1, maxHealth / 2);
        }
    }
}
