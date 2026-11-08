using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
        /// <summary>
        /// Cached key plus the allocation-free hierarchy fingerprint it was computed from.
        /// Names are only read while recomputing; a hit compares transform references,
        /// sibling indices and the scene handle.
        /// </summary>
        private sealed class CachedKey
        {
            public ulong Key;
            public Transform[] Chain = Array.Empty<Transform>();
            public int[] SiblingIndices = Array.Empty<int>();
            public int SceneHandle = -1;
        }

        private static ConditionalWeakTable<Transform, CachedKey> _cache =
            new ConditionalWeakTable<Transform, CachedKey>();
        private static float _cacheRefreshedAt;
        private const float CacheRefreshSeconds = 30f;

        private static System.Reflection.FieldInfo? _pickupPersistentField;
        private static bool _pickupPersistentFieldResolved;

        /// <summary>
        /// Computes the scene + hierarchy-path hash for an object, reusing a cached key while
        /// the transform chain, sibling indices and scene handle are unchanged.
        /// </summary>
        public static ulong Compute(Transform transform)
        {
            if (transform == null) return 0;
            RefreshCacheIfStale();

            if (_cache.TryGetValue(transform, out var cached))
            {
                if (MatchesFingerprint(cached, transform))
                {
                    return cached.Key;
                }

                ulong refreshed = ComputeUncached(transform);
                if (refreshed == 0)
                {
                    _cache.Remove(transform);
                    return 0;
                }

                FillFingerprint(cached, transform, refreshed);
                return refreshed;
            }

            ulong key = ComputeUncached(transform);
            if (key == 0) return 0;

            var entry = new CachedKey();
            FillFingerprint(entry, transform, key);
            _cache.Remove(transform);
            _cache.Add(transform, entry);
            return key;
        }

        /// <summary>
        /// Drops every cached hierarchy key. Call on scene changes so destroyed/reloaded
        /// transforms cannot keep stale keys alive through the weak table.
        /// </summary>
        public static void ClearCache()
        {
            _cache = new ConditionalWeakTable<Transform, CachedKey>();
            _cacheRefreshedAt = Time.unscaledTime;
        }

        /// <summary>
        /// The fingerprint omits names, so periodically drop the whole cache to catch
        /// ancestor renames even on paths that never trigger an enemy-index rebuild.
        /// </summary>
        private static void RefreshCacheIfStale()
        {
            float now = Time.unscaledTime;
            if (now - _cacheRefreshedAt < CacheRefreshSeconds) return;

            _cache = new ConditionalWeakTable<Transform, CachedKey>();
            _cacheRefreshedAt = now;
        }

        /// <summary>
        /// Computes a key without consulting the cache and refreshes the cached fingerprint.
        /// Used for persistent death records, where a stale pre-rename key would never be
        /// resolvable by a peer with a fresh cache.
        /// </summary>
        public static ulong ComputeFresh(Transform transform)
        {
            if (transform == null) return 0;
            RefreshCacheIfStale();

            ulong key = ComputeUncached(transform);
            if (key == 0)
            {
                _cache.Remove(transform);
                return 0;
            }

            var entry = new CachedKey();
            FillFingerprint(entry, transform, key);
            _cache.Remove(transform);
            _cache.Add(transform, entry);
            return key;
        }

        private static ulong ComputeUncached(Transform transform)
        {
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

        private static bool MatchesFingerprint(CachedKey cached, Transform transform)
        {
            if (cached.Chain.Length == 0 ||
                cached.SceneHandle != transform.gameObject.scene.handle)
            {
                return false;
            }

            int index = 0;
            for (var current = transform; current != null; current = current.parent)
            {
                if (index >= cached.Chain.Length ||
                    cached.Chain[index] != current ||
                    cached.SiblingIndices[index] != current.GetSiblingIndex())
                {
                    return false;
                }
                index++;
            }

            return index == cached.Chain.Length;
        }

        private static void FillFingerprint(CachedKey cached, Transform transform, ulong key)
        {
            int depth = 0;
            for (var current = transform; current != null; current = current.parent)
            {
                depth++;
            }

            var chain = new Transform[depth];
            var siblings = new int[depth];
            int index = 0;
            for (var current = transform; current != null; current = current.parent)
            {
                chain[index] = current;
                siblings[index] = current.GetSiblingIndex();
                index++;
            }

            cached.Key = key;
            cached.Chain = chain;
            cached.SiblingIndices = siblings;
            cached.SceneHandle = transform.gameObject.scene.handle;
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
