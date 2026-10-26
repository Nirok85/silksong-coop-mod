using System;
using HarmonyLib;
using SilksongCoop.Core.Protocol.Packets;

namespace SilksongCoop.Mod.Hooks
{
    [HarmonyPatch]
    public static class HeroVitalsHooks
    {
        public static bool IsApplyingRemoteUpgrade = false;
        private static int _lastNailUpgradeLevel = -1;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HeroController), nameof(HeroController.AddToMaxHealth))]
        public static void PostfixAddToMaxHealth(int amount)
        {
            if (IsApplyingRemoteUpgrade) return;
            if (SilksongCoopPlugin.Instance?.VitalsSync != null && SilksongCoopPlugin.Instance.SessionState.IsConnected)
            {
                if (HeroController.instance == null || PlayerData.instance == null) return;
                var pd = PlayerData.instance;
                if (pd != null)
                {
                    SilksongCoopPlugin.Instance.VitalsSync.OnLocalMaxHealthUpgrade(pd.maxHealthBase);
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HeroController), nameof(HeroController.AddToMaxSilk))]
        public static void PostfixAddToMaxSilk(int amount)
        {
            if (IsApplyingRemoteUpgrade) return;
            if (SilksongCoopPlugin.Instance?.VitalsSync != null && SilksongCoopPlugin.Instance.SessionState.IsConnected)
            {
                if (HeroController.instance == null || PlayerData.instance == null) return;
                var pd = PlayerData.instance;
                if (pd != null)
                {
                    SilksongCoopPlugin.Instance.VitalsSync.OnLocalMaxSilkUpgrade(pd.silkMax / 9);
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HeroController), nameof(HeroController.TakeDamage))]
        public static void PostfixTakeDamage(int damageAmount)
        {
            // Current health is local only!
            if (SilksongCoopPlugin.Instance?.VitalsSync != null && SilksongCoopPlugin.Instance.SessionState.IsConnected)
            {
                if (HeroController.instance == null || PlayerData.instance == null) return;
                var pd = PlayerData.instance;
                if (pd != null)
                {
                    SilksongCoopPlugin.Instance.VitalsSync.OnLocalDamageTaken(damageAmount, pd.health);
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HeroController), nameof(HeroController.TakeSilk), new Type[] { typeof(int), typeof(SilkSpool.SilkTakeSource) })]
        public static void PostfixTakeSilk(int amount, SilkSpool.SilkTakeSource source)
        {
            // Current silk is local only!
            if (SilksongCoopPlugin.Instance?.VitalsSync != null && SilksongCoopPlugin.Instance.SessionState.IsConnected)
            {
                if (HeroController.instance == null || PlayerData.instance == null) return;
                var pd = PlayerData.instance;
                if (pd != null)
                {
                    SilksongCoopPlugin.Instance.VitalsSync.OnLocalSilkUsed(amount, pd.silk);
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HeroController), nameof(HeroController.AddHealth))]
        public static void PostfixAddHealth(int amount)
        {
            if (SilksongCoopPlugin.Instance?.VitalsSync != null && SilksongCoopPlugin.Instance.SessionState.IsConnected)
            {
                if (HeroController.instance == null || PlayerData.instance == null) return;
                var pd = PlayerData.instance;
                if (pd != null)
                {
                    SilksongCoopPlugin.Instance.VitalsSync.OnLocalHealthGained(amount, pd.health);
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerData), nameof(PlayerData.SetInt))]
        public static void PostfixSetInt(string intName, int value)
        {
            if (IsApplyingRemoteUpgrade) return;
            if (intName == "nailUpgrades" && SilksongCoopPlugin.Instance?.VitalsSync != null && SilksongCoopPlugin.Instance.SessionState.IsConnected)
            {
                // Fix: Only broadcast if value actually changed to prevent duplicate packets
                if (value != _lastNailUpgradeLevel)
                {
                    _lastNailUpgradeLevel = value;
                    SilksongCoopPlugin.Instance.VitalsSync.OnLocalNeedleUpgrade(value);
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerData), nameof(PlayerData.IntAdd))]
        public static void PostfixIntAdd(string intName, int amount)
        {
            if (IsApplyingRemoteUpgrade) return;
            if (intName == "nailUpgrades" && SilksongCoopPlugin.Instance?.VitalsSync != null && SilksongCoopPlugin.Instance.SessionState.IsConnected)
            {
                var pd = PlayerData.instance;
                if (pd != null)
                {
                    // Fix: Only broadcast if value actually changed to prevent duplicate packets
                    if (pd.nailUpgrades != _lastNailUpgradeLevel)
                    {
                        _lastNailUpgradeLevel = pd.nailUpgrades;
                        SilksongCoopPlugin.Instance.VitalsSync.OnLocalNeedleUpgrade(pd.nailUpgrades);
                    }
                }
            }
        }
    }
}
