using System;
using System.Collections.Generic;
using HarmonyLib;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Mod.State;

namespace SilksongCoop.Mod.Hooks
{
    [HarmonyPatch]
    public static class InventoryHooks
    {
        // Guard flag replaced by RemoteApplyScope.IsActive

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
                if (!string.IsNullOrEmpty(boolName) && SharedAbilityBools.Contains(boolName))
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
