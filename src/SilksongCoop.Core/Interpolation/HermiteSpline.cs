using System;

namespace SilksongCoop.Core.Interpolation
{
    /// <summary>
    /// Cubic Hermite Spline calculation ensuring C1 continuity for position and velocity smoothing.
    /// Eliminates jitter and sharp direction change stutter under network latency.
    /// </summary>
    public static class HermiteSpline
    {
        /// <summary>
        /// Interpolates position along a cubic Hermite spline at parameter t.
        /// </summary>
        /// <param name="p0">Start position.</param>
        /// <param name="v0">Start velocity (tangent).</param>
        /// <param name="p1">End position.</param>
        /// <param name="v1">End velocity (tangent).</param>
        /// <param name="dt">Time interval between p0 and p1.</param>
        /// <param name="t">Interpolation parameter [0, 1].</param>
        /// <returns>Interpolated position value.</returns>
        public static float InterpolatePosition(float p0, float v0, float p1, float v1, float dt, float t)
        {
            if (dt <= 0.0001f) return p1;

            t = Math.Clamp(t, 0f, 1f);
            float t2 = t * t;
            float t3 = t2 * t;

            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;

            return h00 * p0 + h10 * dt * v0 + h01 * p1 + h11 * dt * v1;
        }

        /// <summary>
        /// Interpolates velocity (derivative) along a cubic Hermite spline at parameter t.
        /// </summary>
        /// <param name="p0">Start position.</param>
        /// <param name="v0">Start velocity (tangent).</param>
        /// <param name="p1">End position.</param>
        /// <param name="v1">End velocity (tangent).</param>
        /// <param name="dt">Time interval between p0 and p1.</param>
        /// <param name="t">Interpolation parameter [0, 1].</param>
        /// <returns>Interpolated velocity value.</returns>
        public static float InterpolateVelocity(float p0, float v0, float p1, float v1, float dt, float t)
        {
            if (dt <= 0.0001f) return v1;

            t = Math.Clamp(t, 0f, 1f);
            float t2 = t * t;

            // Derivatives of basis functions
            float dh00 = 6f * t2 - 6f * t;
            float dh10 = 3f * t2 - 4f * t + 1f;
            float dh01 = -6f * t2 + 6f * t;
            float dh11 = 3f * t2 - 2f * t;

            float dp_dt = dh00 * p0 + dh10 * dt * v0 + dh01 * p1 + dh11 * dt * v1;
            return dp_dt / dt;
        }

        /// <summary>
        /// Interpolates 2D position along a cubic Hermite spline at parameter t.
        /// </summary>
        /// <param name="p0x">Start X position.</param>
        /// <param name="p0y">Start Y position.</param>
        /// <param name="v0x">Start X velocity.</param>
        /// <param name="v0y">Start Y velocity.</param>
        /// <param name="p1x">End X position.</param>
        /// <param name="p1y">End Y position.</param>
        /// <param name="v1x">End X velocity.</param>
        /// <param name="v1y">End Y velocity.</param>
        /// <param name="dt">Time interval between start and end.</param>
        /// <param name="t">Interpolation parameter [0, 1].</param>
        /// <returns>Interpolated (X, Y) position tuple.</returns>
        public static (float X, float Y) InterpolatePosition2D(
            float p0x, float p0y,
            float v0x, float v0y,
            float p1x, float p1y,
            float v1x, float v1y,
            float dt, float t)
        {
            float x = InterpolatePosition(p0x, v0x, p1x, v1x, dt, t);
            float y = InterpolatePosition(p0y, v0y, p1y, v1y, dt, t);
            return (x, y);
        }

        /// <summary>
        /// Interpolates 2D velocity (derivative) along a cubic Hermite spline at parameter t.
        /// </summary>
        /// <param name="p0x">Start X position.</param>
        /// <param name="p0y">Start Y position.</param>
        /// <param name="v0x">Start X velocity.</param>
        /// <param name="v0y">Start Y velocity.</param>
        /// <param name="p1x">End X position.</param>
        /// <param name="p1y">End Y position.</param>
        /// <param name="v1x">End X velocity.</param>
        /// <param name="v1y">End Y velocity.</param>
        /// <param name="dt">Time interval between start and end.</param>
        /// <param name="t">Interpolation parameter [0, 1].</param>
        /// <returns>Interpolated (VX, VY) velocity tuple.</returns>
        public static (float VX, float VY) InterpolateVelocity2D(
            float p0x, float p0y,
            float v0x, float v0y,
            float p1x, float p1y,
            float v1x, float v1y,
            float dt, float t)
        {
            float vx = InterpolateVelocity(p0x, v0x, p1x, v1x, dt, t);
            float vy = InterpolateVelocity(p0y, v0y, p1y, v1y, dt, t);
            return (vx, vy);
        }
    }
}
