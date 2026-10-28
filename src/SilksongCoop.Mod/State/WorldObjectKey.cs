using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SilksongCoop.Mod.State
{
    /// <summary>
    /// Builds stable 64-bit identifiers for Unity objects that exist in both players'
    /// simulations. The key is derived from the scene name and the object's hierarchy path,
    /// so it is identical on both machines without exchanging Unity instance IDs.
    /// </summary>
    public static class WorldObjectKey
    {
        /// <summary>
        /// Computes the scene + hierarchy-path hash for an object.
        /// </summary>
        public static ulong Compute(Transform transform)
        {
            if (transform == null) return 0;

            var path = new StringBuilder(192);
            path.Append(transform.gameObject.scene.name);

            var chain = new Stack<Transform>(8);
            var current = transform;
            while (current != null)
            {
                chain.Push(current);
                current = current.parent;
            }

            while (chain.Count > 0)
            {
                var node = chain.Pop();
                path.Append('|').Append(node.name).Append('#').Append(node.GetSiblingIndex());
            }

            return HashString(path.ToString());
        }

        /// <summary>
        /// Computes the stable key of a physical currency drop. Position is rounded to 0.1
        /// units so small physics differences between the two simulations still match.
        /// </summary>
        public static ulong ComputePickup(CurrencyObjectBase pickup)
        {
            if (pickup == null) return 0;

            // CurrencyObjectBase.CurrencyType is protected, so distinguish the two concrete
            // drop components instead. Everything that is not a shell shard is rosaries.
            int type = pickup is ShellShard ? 1 : 0;

            Vector3 pos = pickup.transform.position;
            float x = Mathf.Round(pos.x * 10f) / 10f;
            float y = Mathf.Round(pos.y * 10f) / 10f;

            string raw = string.Concat(
                pickup.gameObject.scene.name, "|", type.ToString(), "|",
                x.ToString("F1", System.Globalization.CultureInfo.InvariantCulture), "|",
                y.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));

            return HashString(raw);
        }

        /// <summary>
        /// Hash of a persistent world item (scene + persistent id + value type).
        /// </summary>
        public static ulong ComputePersistent(string sceneName, string itemId, int valueType)
        {
            if (string.IsNullOrEmpty(itemId)) return 0;

            string raw = string.Concat(
                sceneName ?? string.Empty, "|", itemId, "|",
                valueType.ToString(System.Globalization.CultureInfo.InvariantCulture));

            return HashString(raw);
        }

        /// <summary>
        /// FNV-1a 64-bit hash. Deterministic across runtimes (unlike string.GetHashCode).
        /// </summary>
        public static ulong HashString(string value)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;

            ulong hash = offset;
            for (int i = 0; i < value.Length; i++)
            {
                hash ^= value[i];
                hash *= prime;
            }

            return hash == 0 ? 1UL : hash;
        }
    }
}
