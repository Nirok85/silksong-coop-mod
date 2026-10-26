using System;
using SilksongCoop.Core.Visuals;
using Xunit;

namespace SilksongCoop.Tests
{
    public class PaletteSwapTests
    {
        [Fact]
        public void PaletteSwapMath_RedCloak_RotatesToAzureBlue()
        {
            float redR = 0.850f, redG = 0.150f, redB = 0.150f;
            PaletteSwapMath.ApplyHueShift(redR, redG, redB, PaletteSwapMath.Player2HueShift, out float outR, out float outG, out float outB);

            // Azure blue must dominate: Blue > Red and Blue > Green
            Assert.True(outB > outR, $"Blue ({outB}) must exceed Red ({outR}) after hue shift.");
            Assert.True(outB > outG, $"Blue ({outB}) must exceed Green ({outG}) after hue shift.");
            Assert.InRange(outB, 0.80f, 0.90f);
            Assert.InRange(outR, 0.10f, 0.20f);
            Assert.InRange(outG, 0.45f, 0.55f);
        }

        [Fact]
        public void PaletteSwapMath_WhiteMask_StrictlyPreservedWithZeroDrift()
        {
            float whiteR = 1.000f, whiteG = 1.000f, whiteB = 1.000f;
            PaletteSwapMath.ApplyHueShift(whiteR, whiteG, whiteB, PaletteSwapMath.Player2HueShift, out float outR, out float outG, out float outB);

            Assert.Equal(1.000f, outR, 5);
            Assert.Equal(1.000f, outG, 5);
            Assert.Equal(1.000f, outB, 5);
        }

        [Fact]
        public void PaletteSwapMath_BlackHorns_StrictlyPreservedWithZeroDrift()
        {
            float blackR = 0.000f, blackG = 0.000f, blackB = 0.000f;
            PaletteSwapMath.ApplyHueShift(blackR, blackG, blackB, PaletteSwapMath.Player2HueShift, out float outR, out float outG, out float outB);

            Assert.Equal(0.000f, outR, 5);
            Assert.Equal(0.000f, outG, 5);
            Assert.Equal(0.000f, outB, 5);
        }

        [Theory]
        [InlineData(0.10f)]
        [InlineData(0.25f)]
        [InlineData(0.50f)]
        [InlineData(0.75f)]
        [InlineData(0.90f)]
        public void PaletteSwapMath_NeutralGrays_RemainAchromatic(float grayLevel)
        {
            PaletteSwapMath.ApplyHueShift(grayLevel, grayLevel, grayLevel, PaletteSwapMath.Player2HueShift, out float outR, out float outG, out float outB);

            Assert.Equal(grayLevel, outR, 5);
            Assert.Equal(grayLevel, outG, 5);
            Assert.Equal(grayLevel, outB, 5);
            Assert.True(PaletteSwapMath.IsAchromatic(outR, outG, outB));
        }

        [Fact]
        public void PaletteSwapMath_FullCircleRoundtrip_ReturnsInitialColor()
        {
            float origR = 0.92f, origG = 0.24f, origB = 0.18f;

            // Shift +0.58f followed by +0.42f (sum = 1.00f full rotation)
            PaletteSwapMath.ApplyHueShift(origR, origG, origB, 0.58f, out float midR, out float midG, out float midB);
            PaletteSwapMath.ApplyHueShift(midR, midG, midB, 0.42f, out float finalR, out float finalG, out float finalB);

            Assert.Equal(origR, finalR, 3);
            Assert.Equal(origG, finalG, 3);
            Assert.Equal(origB, finalB, 3);
        }

        [Fact]
        public void PaletteSwapMath_ZeroShift_LeavesColorUntouched()
        {
            float r = 0.73f, g = 0.41f, b = 0.88f;
            PaletteSwapMath.ApplyHueShift(r, g, b, PaletteSwapMath.Player1HueShift, out float outR, out float outG, out float outB);

            Assert.Equal(r, outR, 4);
            Assert.Equal(g, outG, 4);
            Assert.Equal(b, outB, 4);
        }

        [Fact]
        public void RodriguesRotation_AchromaticColors_StrictlyPreserved()
        {
            // Pure white, charcoal, and black under Rodrigues rotation
            float[] levels = { 0.0f, 0.15f, 0.5f, 0.85f, 1.0f };
            foreach (var level in levels)
            {
                PaletteSwapMath.ApplyRodriguesHueRotation(level, level, level, PaletteSwapMath.Player2HueShift, out float r, out float g, out float b);
                Assert.Equal(level, r, 4);
                Assert.Equal(level, g, 4);
                Assert.Equal(level, b, 4);
            }
        }

        [Fact]
        public void RodriguesRotation_RedCloak_ShiftsHueTowardsBlue()
        {
            float redR = 0.850f, redG = 0.150f, redB = 0.150f;
            PaletteSwapMath.ApplyRodriguesHueRotation(redR, redG, redB, PaletteSwapMath.Player2HueShift, out float outR, out float outG, out float outB);

            Assert.True(outB > outR, $"Rodrigues Blue ({outB}) must exceed Red ({outR}).");
            Assert.True(outB > outG, $"Rodrigues Blue ({outB}) must exceed Green ({outG}).");
        }

        [Fact]
        public void ProveSaturationInvariance_HornetNeutralFeatures_EvaluatesTrue()
        {
            // Mask (1.0, 1.0, 1.0)
            Assert.True(PaletteSwapMath.ProveSaturationInvariance(1.0f, 1.0f, 1.0f, PaletteSwapMath.Player2HueShift, out float devMask));
            Assert.True(devMask < 1e-6f);

            // Black Horns (0.0, 0.0, 0.0)
            Assert.True(PaletteSwapMath.ProveSaturationInvariance(0.0f, 0.0f, 0.0f, PaletteSwapMath.Player2HueShift, out float devHorns));
            Assert.True(devHorns < 1e-6f);

            // Needle neutral steel (0.80, 0.80, 0.80)
            Assert.True(PaletteSwapMath.ProveSaturationInvariance(0.80f, 0.80f, 0.80f, PaletteSwapMath.Player2HueShift, out float devNeedle));
            Assert.True(devNeedle < 1e-6f);
        }

        [Fact]
        public void CalculateSaturationAndChroma_CorrectlyIdentifiesAchromaticColors()
        {
            Assert.Equal(0f, PaletteSwapMath.CalculateChroma(1f, 1f, 1f));
            Assert.Equal(0f, PaletteSwapMath.CalculateSaturation(1f, 1f, 1f));
            Assert.True(PaletteSwapMath.IsSaturationInvariant(1f, 1f, 1f));

            Assert.Equal(0f, PaletteSwapMath.CalculateChroma(0f, 0f, 0f));
            Assert.Equal(0f, PaletteSwapMath.CalculateSaturation(0f, 0f, 0f));
            Assert.True(PaletteSwapMath.IsSaturationInvariant(0f, 0f, 0f));

            float satRed = PaletteSwapMath.CalculateSaturation(0.850f, 0.150f, 0.150f);
            Assert.True(satRed > 0.8f);
            Assert.False(PaletteSwapMath.IsSaturationInvariant(0.850f, 0.150f, 0.150f));
        }
    }
}
