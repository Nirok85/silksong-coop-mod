using System;

namespace SilksongCoop.Core.Visuals
{
    /// <summary>
    /// Pure engine-agnostic colorimetry and color space transformation math.
    /// Implements RGB &lt;-&gt; HSV conversions, Rodrigues color space rotation around the achromatic diagonal,
    /// and saturation invariance proofs for Hornet's cloak palette swap.
    /// </summary>
    public static class PaletteSwapMath
    {
        public const string KeywordHueShift = "CAN_HUESHIFT";
        public const string KeywordRecolour = "RECOLOUR";
        public const string ShaderPropertyHueShift = "_HueShift";

        public const float Player1HueShift = 0.00f; // Default Hornet red
        /// <summary>
        /// Hue shift value for Player 2's cloak (azure blue, approximately 208.8 degree rotation).
        /// </summary>
        /// <summary>
        /// Hue shift value for Player 2 (azure blue, approximately 208.8 degrees).
        /// </summary>
        public const float Player2HueShift = 0.58f; // Azure blue (~208.8° rotation)
        public const float AchromaticTolerance = 1e-4f;

        private const float OneOverSqrt3 = 0.5773502691896257645f;
        private const float OneThird = 1.0f / 3.0f;

        /// <summary>
        /// Converts an RGB triplet in [0, 1] to HSV in [0, 1].
        /// </summary>
        public static void RgbToHsv(float r, float g, float b, out float h, out float s, out float v)
        {
            r = Math.Clamp(r, 0f, 1f);
            g = Math.Clamp(g, 0f, 1f);
            b = Math.Clamp(b, 0f, 1f);

            float max = Math.Max(r, Math.Max(g, b));
            float min = Math.Min(r, Math.Min(g, b));
            float delta = max - min;

            v = max;
            s = (max > 1e-5f) ? (delta / max) : 0f;

            if (delta < 1e-5f)
            {
                h = 0f;
                return;
            }

            if (Math.Abs(max - r) < 1e-5f)
            {
                h = (g - b) / delta;
                if (h < 0f) h += 6f;
            }
            else if (Math.Abs(max - g) < 1e-5f)
            {
                h = (b - r) / delta + 2f;
            }
            else
            {
                h = (r - g) / delta + 4f;
            }

            h /= 6f;
            if (h < 0f) h += 1f;
            if (h >= 1f) h -= 1f;
        }

        /// <summary>
        /// Converts an HSV triplet in [0, 1] to RGB in [0, 1].
        /// </summary>
        public static void HsvToRgb(float h, float s, float v, out float r, out float g, out float b)
        {
            v = Math.Clamp(v, 0f, 1f);
            s = Math.Clamp(s, 0f, 1f);

            if (s < 1e-5f)
            {
                r = g = b = v;
                return;
            }

            h = (h % 1f + 1f) % 1f;
            float h6 = h * 6f;
            int sector = (int)Math.Floor(h6);
            float fract = h6 - sector;

            float p = v * (1f - s);
            float q = v * (1f - s * fract);
            float t = v * (1f - s * (1f - fract));

            switch (sector % 6)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }

            r = Math.Clamp(r, 0f, 1f);
            g = Math.Clamp(g, 0f, 1f);
            b = Math.Clamp(b, 0f, 1f);
        }

        /// <summary>
        /// Applies normalized hue shift in [0, 1] using standard cylindrical HSV colorimetry.
        /// When S == 0 (achromatic colors: white mask, black horns, gray needle),
        /// output RGB is mathematically identical to input RGB.
        /// </summary>
        public static void ApplyHueShift(float inR, float inG, float inB, float hueShift, out float outR, out float outG, out float outB)
        {
            RgbToHsv(inR, inG, inB, out float h, out float s, out float v);

            // Neutral/achromatic invariance: S == 0 is strictly invariant under any hue rotation
            if (s < 1e-5f)
            {
                outR = inR;
                outG = inG;
                outB = inB;
                return;
            }

            float newH = (h + hueShift) % 1f;
            if (newH < 0f) newH += 1f;
            HsvToRgb(newH, s, v, out outR, out outG, out outB);
        }

        /// <summary>
        /// Applies hue shift using Rodrigues' rotation formula in RGB color space around the unit diagonal k = (1, 1, 1) / sqrt(3).
        /// v_rot = v * cos(theta) + (k x v) * sin(theta) + k * (k . v) * (1 - cos(theta)).
        /// For any achromatic color v = c * (1, 1, 1), k x v = 0 and k * (k . v) = v, proving v_rot == v for all theta.
        /// </summary>
        public static void ApplyRodriguesHueRotation(float inR, float inG, float inB, float hueShift, out float outR, out float outG, out float outB)
        {
            float theta = hueShift * 2.0f * (float)Math.PI;
            RodriguesRotateRgb(inR, inG, inB, theta, out outR, out outG, out outB);
        }

        /// <summary>
        /// Rotates RGB color around the achromatic gray axis k = (1, 1, 1) / sqrt(3) by the specified angle in radians.
        /// </summary>
        public static void RodriguesRotateRgb(float r, float g, float b, float theta, out float outR, out float outG, out float outB)
        {
            float cosTheta = (float)Math.Cos(theta);
            float sinTheta = (float)Math.Sin(theta);

            // Cross product: k x v where k = (1, 1, 1) / sqrt(3)
            float crossX = (b - g) * OneOverSqrt3;
            float crossY = (r - b) * OneOverSqrt3;
            float crossZ = (g - r) * OneOverSqrt3;

            // Dot product projection: k * (k . v) = (r + g + b) / 3 * (1, 1, 1)
            float kDotV = (r + g + b) * OneThird;

            outR = r * cosTheta + crossX * sinTheta + kDotV * (1f - cosTheta);
            outG = g * cosTheta + crossY * sinTheta + kDotV * (1f - cosTheta);
            outB = b * cosTheta + crossZ * sinTheta + kDotV * (1f - cosTheta);
        }

        /// <summary>
        /// Proves saturation invariance by evaluating whether an achromatic color is invariant under hue rotation.
        /// Returns true if the maximum channel deviation between shifted and original color is below tolerance.
        /// </summary>
        public static bool ProveSaturationInvariance(float r, float g, float b, float hueShift, out float maxDeviation)
        {
            ApplyHueShift(r, g, b, hueShift, out float outR, out float outG, out float outB);
            float dr = Math.Abs(outR - r);
            float dg = Math.Abs(outG - g);
            float db = Math.Abs(outB - b);
            maxDeviation = Math.Max(dr, Math.Max(dg, db));
            return maxDeviation <= AchromaticTolerance;
        }

        /// <summary>
        /// Returns whether the specified RGB color is achromatic (neutral grayscale) within tolerance.
        /// </summary>
        public static bool IsAchromatic(float r, float g, float b, float tolerance = AchromaticTolerance)
        {
            return Math.Abs(r - g) <= tolerance && Math.Abs(g - b) <= tolerance;
        }

        /// <summary>
        /// Calculates saturation S = (max - min) / max for the given RGB color.
        /// </summary>
        public static float CalculateSaturation(float r, float g, float b)
        {
            float max = Math.Max(r, Math.Max(g, b));
            float min = Math.Min(r, Math.Min(g, b));
            return (max > 1e-5f) ? ((max - min) / max) : 0f;
        }

        /// <summary>
        /// Calculates chroma C = max - min for the given RGB color.
        /// </summary>
        public static float CalculateChroma(float r, float g, float b)
        {
            float max = Math.Max(r, Math.Max(g, b));
            float min = Math.Min(r, Math.Min(g, b));
            return max - min;
        }

        /// <summary>
        /// Returns true if the color is saturation-invariant (S == 0 within tolerance).
        /// </summary>
        public static bool IsSaturationInvariant(float r, float g, float b, float tolerance = AchromaticTolerance)
        {
            return CalculateSaturation(r, g, b) <= tolerance;
        }
    }
}
