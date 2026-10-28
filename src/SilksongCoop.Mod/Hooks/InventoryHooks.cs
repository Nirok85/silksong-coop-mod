using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Mod.State;

namespace SilksongCoop.Mod.Hooks
{
    /// <summary>
    /// Harmony hooks for synchronizing inventory unlocks (tools, crests, abilities) between co-op players.
    /// </summary>
    [HarmonyPatch]
    public static class InventoryHooks
    {
        /// <summary>
        /// Set of ability boolean flags that should be synchronized between players.
        /// </summary>
        public static readonly HashSet<string> SharedAbilityBools = new HashSet<string>(StringComparer.Ordinal)
        {
            "hasDash",
            "hasWalljump",
            "hasDoubleJump",
            "hasHarpoonDash",
            "hasSilkSpecial",
            "hasNeedleThrow",
            "hasThreadSphere",
            "hasParry",
            "hasSilkCharge",
            "hasSilkBomb",
            "hasSilkBossNeedle",
            "hasNeedolin",
            "hasNeedolinMemoryPowerup",
            "hasBrolly",
            "hasChargeSlash",
            "hasSuperJump",
            "hasQuill"
        };

        private static HashSet<string>? _validatedAbilityBools;
        private static readonly object _abilityValidationLock = new object();

        /// <summary>
        /// Returns the ability names that actually exist on <see cref="PlayerData"/>.
        /// A name that is not a real field/property would be silently ignored by
        /// <c>SetBool</c> and never synchronize anything; validate once at startup and log
        /// the mismatch instead of pretending to sync it.
        /// </summary>
        public static HashSet<string> GetValidatedAbilityBools()
        {
            if (_validatedAbilityBools != null) return _validatedAbilityBools;

            lock (_abilityValidationLock)
            {
                if (_validatedAbilityBools != null) return _validatedAbilityBools;

                var validated = new HashSet<string>(StringComparer.Ordinal);
                var type = typeof(PlayerData);
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                foreach (var name in SharedAbilityBools)
                {
                    bool exists = type.GetField(name, flags) != null || type.GetProperty(name, flags) != null;
                    if (exists)
                    {
                        validated.Add(name);
                    }
                    else
                    {
                        SilksongCoopPlugin.LogWarn(
                            $"[Coop] PlayerData has no member '{name}' — ability sync for it is disabled (game signature changed?)");
                    }
                }

                _validatedAbilityBools = validated;
                return validated;
            }
        }

        /// <summary>
        /// True when the name is a shared ability AND exists on the current game's
        /// <see cref="PlayerData"/> type.
        /// </summary>
        public static bool IsSharedAbilityBool(string boolName)
        {
            if (string.IsNullOrEmpty(boolName) || !SharedAbilityBools.Contains(boolName))
                return false;

            return GetValidatedAbilityBools().Contains(boolName);
        }

        /// <summary>
        /// Hook that fires after a crest is unlocked.
        /// </summary>
        /// <param name="__instance">The ToolCrest instance that was unlocked.</param>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ToolCrest), nameof(ToolCrest.Unlock))]
        public static void PostfixToolCrestUnlock(ToolCrest __instance)
        {
            if (RemoteApplyScope.IsActive) return;
            if (SilksongCoopPlugin.Instance?.SessionState?.IsConnected != true) return;
            if (SilksongCoopPlugin.Instance?.InventorySync == null) return;

            try
            {
                if (__instance != null && !string.IsNullOrEmpty(__instance.name))
                {
                    SilksongCoopPlugin.Instance.InventorySync.OnLocalItemDiscovered(__instance.name, ItemCategory.Crest);
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[Coop] Exception in PostfixToolCrestUnlock: {ex.Message}");
            }
        }

        /// <summary>
        /// Hook that fires after a tool or skill is unlocked.
        /// </summary>
        /// <param name="__instance">The ToolItem instance that was unlocked.</param>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ToolItem), nameof(ToolItem.Unlock))]
        public static void PostfixToolItemUnlock(ToolItem __instance)
        {
            if (RemoteApplyScope.IsActive) return;
            if (SilksongCoopPlugin.Instance?.SessionState?.IsConnected != true) return;
            if (SilksongCoopPlugin.Instance?.InventorySync == null) return;

            try
            {
                if (__instance != null && !string.IsNullOrEmpty(__instance.name))
                {
                    var category = (__instance.Type == ToolItemType.Skill) ? ItemCategory.Ability : ItemCategory.Tool;
                    SilksongCoopPlugin.Instance.InventorySync.OnLocalItemDiscovered(__instance.name, category);
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[Coop] Exception in PostfixToolItemUnlock: {ex.Message}");
            }
        }

        /// <summary>
        /// Hook that fires after a boolean value is set in PlayerData.
        /// </summary>
        /// <param name="boolName">The name of the boolean field.</param>
        /// <param name="value">The new value.</param>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerData), nameof(PlayerData.SetBool))]
        public static void PostfixSetBool(string boolName, bool value)
        {
            if (RemoteApplyScope.IsActive) return;
            if (!value) return; // Only synchronize discovery/unlocks (value == true)
            if (SilksongCoopPlugin.Instance?.SessionState?.IsConnected != true) return;
            if (SilksongCoopPlugin.Instance?.InventorySync == null) return;

            try
            {
                if (!string.IsNullOrEmpty(boolName) && IsSharedAbilityBool(boolName))
                {
                    SilksongCoopPlugin.Instance.InventorySync.OnLocalItemDiscovered(boolName, ItemCategory.Ability);
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[Coop] Exception in PostfixSetBool: {ex.Message}");
            }
        }
    }
}
