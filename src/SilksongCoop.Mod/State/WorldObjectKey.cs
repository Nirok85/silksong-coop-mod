using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using SilksongCoop.Core.State;
using UnityEngine;

namespace SilksongCoop.Mod.State
{
    /// <summary>
    /// Builds stable 64-bit identifiers for Unity objects that exist in both players'
    /// simulations. The key is derived from the scene name and the object's hierarchy path,
    /// so it is identical on both machines without exchanging Unity instance IDs. The pure
    /// hash/salt rules live in <see cref="StableObjectKey"/>.
    /// </summary>
    public static class WorldObjectKey
    {
        private static System.Reflection.FieldInfo? _pickupPersistentField;
        private static bool _pickupPersistentFieldResolved;

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
        /// Computes the stable key of a physical currency drop. Prefers the serialized
        /// <c>PersistentBoolItem</c> id (identical on both peers even when physics moved the
        /// copies apart) and falls back to scene + type + rounded position for dynamic drops
        /// without a persistent id.
        /// </summary>
        public static ulong ComputePickup(CurrencyObjectBase pickup)
        {
            if (pickup == null) return 0;

            var persistent = GetPersistentItem(pickup);
            if (persistent != null)
            {
                ulong stable = StableObjectKey.PickupFromPersistent(
                    persistent.GetSceneName(), persistent.GetId());
                if (stable != 0) return stable;
            }

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
        /// Reads the serialized <c>CurrencyObjectBase.persistent</c> reference. The field is
        /// private, so the accessor is resolved once and cached.
        /// </summary>
        private static PersistentBoolItem? GetPersistentItem(CurrencyObjectBase pickup)
        {
            try
            {
                if (!_pickupPersistentFieldResolved)
                {
                    _pickupPersistentFieldResolved = true;
                    _pickupPersistentField = AccessTools.Field(typeof(CurrencyObjectBase), "persistent");
                }

                return _pickupPersistentField?.GetValue(pickup) as PersistentBoolItem;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Hash of a persistent world item (scene + persistent id + value type).
        /// </summary>
        public static ulong ComputePersistent(string sceneName, string itemId, int valueType)
            => StableObjectKey.Persistent(sceneName, itemId, valueType);

        /// <summary>
        /// FNV-1a 64-bit hash. Deterministic across runtimes (unlike string.GetHashCode).
        /// </summary>
        public static ulong HashString(string value) => StableObjectKey.HashString(value);
    }
}
