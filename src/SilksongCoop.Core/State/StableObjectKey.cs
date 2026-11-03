using System.Globalization;

namespace SilksongCoop.Core.State
{
    /// <summary>
    /// Deterministic 64-bit identifiers for objects that exist in both co-op simulations.
    /// Kept in Core (no Unity references) so the exact hash and salt rules are unit-tested.
    /// The Unity layer supplies scene names, persistent ids and hierarchy paths.
    /// </summary>
    public static class StableObjectKey
    {
        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        /// <summary>
        /// Salt that keeps physical-pickup keys disjoint from persistent bool/int keys.
        /// ASCII "PICK".
        /// </summary>
        public const int PickupPersistentSalt = 0x5049434B;

        /// <summary>
        /// FNV-1a 64-bit hash. Deterministic across runtimes (unlike string.GetHashCode).
        /// A zero result is mapped to 1 because 0 means "no key" on the wire.
        /// </summary>
        public static ulong HashString(string value)
        {
            ulong hash = FnvOffset;
            for (int i = 0; i < value.Length; i++)
            {
                hash ^= value[i];
                hash *= FnvPrime;
            }

            return hash == 0 ? 1UL : hash;
        }

        /// <summary>
        /// Hash of a persistent world item (scene + persistent id + value type).
        /// </summary>
        public static ulong Persistent(string? sceneName, string? itemId, int valueType)
        {
            if (string.IsNullOrEmpty(itemId)) return 0;

            string raw = string.Concat(
                sceneName ?? string.Empty, "|", itemId, "|",
                valueType.ToString(CultureInfo.InvariantCulture));

            return HashString(raw);
        }

        /// <summary>
        /// Stable key for a physical drop that carries a persistent id. Position is not
        /// part of the key, so copies that physics moved apart still match.
        /// </summary>
        public static ulong PickupFromPersistent(string? sceneName, string? itemId)
            => Persistent(sceneName, itemId, PickupPersistentSalt);
    }
}
