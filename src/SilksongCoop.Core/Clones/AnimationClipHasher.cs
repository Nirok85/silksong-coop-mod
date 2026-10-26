using System;

namespace SilksongCoop.Core.Clones
{
    /// <summary>
    /// Deterministic 32-bit FNV-1a hash provider for Hornet's tk2d animation clip names,
    /// and normalized playback time encoder/decoder for network synchronization.
    /// </summary>
    public static class AnimationClipHasher
    {
        public const uint FnvOffsetBasis = 2166136261;
        public const uint FnvPrime = 16777619;

        /// <summary>
        /// Computes a deterministic 32-bit FNV-1a hash for the given animation clip name.
        /// </summary>
        public static int ComputeHash(string clipName)
        {
            if (string.IsNullOrEmpty(clipName)) return 0;

            uint hash = FnvOffsetBasis;
            for (int i = 0; i < clipName.Length; i++)
            {
                hash ^= clipName[i];
                hash *= FnvPrime;
            }
            return (int)hash;
        }

        /// <summary>
        /// Decodes a 16-bit unsigned network integer [0, 65535] into a floating-point normalized time [0.0, 1.0].
        /// </summary>
        public static float DecodeNormalizedTime(ushort rawTime)
        {
            return rawTime / 65535.0f;
        }

        /// <summary>
        /// Encodes a floating-point normalized time [0.0, 1.0] into a 16-bit unsigned integer [0, 65535].
        /// </summary>
        public static ushort EncodeNormalizedTime(float normalizedTime)
        {
            float clamped = Math.Clamp(normalizedTime, 0f, 1f);
            return (ushort)Math.Round(clamped * 65535.0f);
        }
    }
}
