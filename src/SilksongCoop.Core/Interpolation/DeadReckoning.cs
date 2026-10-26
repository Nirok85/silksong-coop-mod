using System;

namespace SilksongCoop.Core.Interpolation
{
    /// <summary>
    /// Dead reckoning extrapolation with velocity damping, in-air gravity, and snap cutoff.
    /// Handles packet starvation up to 250ms and prevents glitching during room teleports.
    /// </summary>
    public static class DeadReckoning
    {
        public const float DefaultGravity = -38.0f; // Silksong Hornet air gravity units/s^2
        public const float DefaultSnapDistance = 2.5f; // Distance threshold triggering instant snap
        public const float MaxExtrapolationSeconds = 0.250f; // 250ms cutoff threshold
        public const float DefaultVelocityDampingRate = 1.5f; // Damping rate per second

        public static (float X, float Y) ExtrapolatePosition(
            float startX, float startY,
            float velocityX, float velocityY,
            bool isGrounded,
            float deltaSeconds,
            float gravity = DefaultGravity,
            float dampingRate = DefaultVelocityDampingRate)
        {
            if (deltaSeconds <= 0f)
                return (startX, startY);

            float clampedDt = Math.Min(deltaSeconds, MaxExtrapolationSeconds);

            // Velocity damping over time
            float dampingFactor = Math.Max(0f, 1f - dampingRate * clampedDt);
            float dampedVx = velocityX * dampingFactor;

            float posX = startX + dampedVx * clampedDt;
            float posY;

            if (isGrounded)
            {
                // Horizontal ground movement, vertical fixed
                posY = startY;
            }
            else
            {
                // In air: apply gravity to vertical velocity
                float dampedVy = velocityY * dampingFactor;
                posY = startY + (dampedVy * clampedDt) + (0.5f * gravity * clampedDt * clampedDt);
            }

            return (posX, posY);
        }

        public static bool ShouldSnap(float currentX, float currentY, float targetX, float targetY, float threshold = DefaultSnapDistance)
        {
            float dx = targetX - currentX;
            float dy = targetY - currentY;
            float distSq = dx * dx + dy * dy;
            return distSq > (threshold * threshold);
        }

        public static float Distance(float x1, float y1, float x2, float y2)
        {
            float dx = x2 - x1;
            float dy = y2 - y1;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
