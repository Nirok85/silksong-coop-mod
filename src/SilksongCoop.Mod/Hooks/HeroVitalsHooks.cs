using System;
using HarmonyLib;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Mod.State;

namespace SilksongCoop.Mod.Hooks
{
    [HarmonyPatch]
    public static class HeroVitalsHooks
    {
        private static int _lastNailUpgradeLevel = -1;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HeroController), nameof(HeroController.AddToMaxHealth))]
        public static void PostfixAddToMaxHealth(int amount)
        {
            if (RemoteApplyScope.IsActive) return;
            
            try
            {
                if (SilksongCoopPlugin.Instance?.VitalsSync == null || !SilksongCoopPlugin.Instance.SessionState.IsConnected) return;
                if (HeroController.instance == null || PlayerData.instance == null) return;
                
                SilksongCoopPlugin.Instance.VitalsSync.OnLocalMaxHealthUpgrade(PlayerData.instance.maxHealthBase);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[HeroVitalsHooks] PostfixAddToMaxHealth error: {ex.Message}");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HeroController), nameof(HeroController.AddToMaxSilk))]
        public static void PostfixAddToMaxSilk(int amount)
        {
            if (RemoteApplyScope.IsActive) return;
            
            try
            {
                if (SilksongCoopPlugin.Instance?.VitalsSync == null || !SilksongCoopPlugin.Instance.SessionState.IsConnected) return;
                if (HeroController.instance == null || PlayerData.instance == null) return;
                
                SilksongCoopPlugin.Instance.VitalsSync.OnLocalMaxSilkUpgrade(PlayerData.instance.silkMax / 9);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[HeroVitalsHooks] PostfixAddToMaxSilk error: {ex.Message}");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HeroController), nameof(HeroController.TakeDamage))]
        public static void PostfixTakeDamage(int damageAmount)
        {
            try
            {
                if (SilksongCoopPlugin.Instance?.VitalsSync == null || !SilksongCoopPlugin.Instance.SessionState.IsConnected) return;
                if (PlayerData.instance == null) return;
                
                SilksongCoopPlugin.Instance.VitalsSync.OnLocalDamageTaken(damageAmount, PlayerData.instance.health);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[HeroVitalsHooks] PostfixTakeDamage error: {ex.Message}");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HeroController), nameof(HeroController.TakeSilk), new Type[] { typeof(int), typeof(SilkSpool.SilkTakeSource) })]
        public static void PostfixTakeSilk(int amount, SilkSpool.SilkTakeSource source)
        {
            try
            {
                if (SilksongCoopPlugin.Instance?.VitalsSync == null || !SilksongCoopPlugin.Instance.SessionState.IsConnected) return;
                if (PlayerData.instance == null) return;
                
                SilksongCoopPlugin.Instance.VitalsSync.OnLocalSilkUsed(amount, PlayerData.instance.silk);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[HeroVitalsHooks] PostfixTakeSilk error: {ex.Message}");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HeroController), nameof(HeroController.AddHealth))]
        public static void PostfixAddHealth(int amount)
        {
            try
            {
                if (SilksongCoopPlugin.Instance?.VitalsSync == null || !SilksongCoopPlugin.Instance.SessionState.IsConnected) return;
                if (PlayerData.instance == null) return;
                
                SilksongCoopPlugin.Instance.VitalsSync.OnLocalHealthGained(amount, PlayerData.instance.health);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[HeroVitalsHooks] PostfixAddHealth error: {ex.Message}");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HeroController), "DoAttack")]
        public static void PostfixDoAttack(HeroController __instance)
        {
            if (RemoteApplyScope.IsActive) return;
            if (SilksongCoopPlugin.Instance?.SessionState?.IsConnected != true) return;
            try
            {
                bool up = __instance != null && __instance.vertical_input > 0.5f;
                SilksongCoopPlugin.Instance?.BroadcastActionTrigger(up ? ActionType.SlashUp : ActionType.SlashNormal);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[HeroVitalsHooks] PostfixDoAttack error: {ex.Message}");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HeroController), "DownAttack")]
        public static void PostfixDownAttack()
        {
            if (RemoteApplyScope.IsActive) return;
            if (SilksongCoopPlugin.Instance?.SessionState?.IsConnected != true) return;
            try
            {
                SilksongCoopPlugin.Instance?.BroadcastActionTrigger(ActionType.SlashDown);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[HeroVitalsHooks] PostfixDownAttack error: {ex.Message}");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HeroController), "HeroDash")]
        public static void PostfixHeroDash(HeroController __instance)
        {
            if (RemoteApplyScope.IsActive) return;
            if (SilksongCoopPlugin.Instance?.SessionState?.IsConnected != true) return;
            try
            {
                bool isAir = __instance != null && !__instance.cState.onGround;
                SilksongCoopPlugin.Instance?.BroadcastActionTrigger(isAir ? ActionType.AirDash : ActionType.HarpoonDash);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[HeroVitalsHooks] PostfixHeroDash error: {ex.Message}");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerData), nameof(PlayerData.SetInt))]
        public static void PostfixSetInt(string intName, int value)
        {
            if (RemoteApplyScope.IsActive) return;
            
            try
            {
                if (SilksongCoopPlugin.Instance?.SessionState?.IsConnected != true) return;

                if (intName == "nailUpgrades" && SilksongCoopPlugin.Instance?.VitalsSync != null)
                {
                    if (value != _lastNailUpgradeLevel)
                    {
                        _lastNailUpgradeLevel = value;
                        SilksongCoopPlugin.Instance.VitalsSync.OnLocalNeedleUpgrade(value);
                    }
                }
                else if (intName == "heartPieces")
                {
                    SilksongCoopPlugin.Instance?.BroadcastHeartPieces(value);
                }
                else if (intName == "silkParts")
                {
                    SilksongCoopPlugin.Instance?.BroadcastSilkParts(value);
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[HeroVitalsHooks] PostfixSetInt error: {ex.Message}");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerData), nameof(PlayerData.IntAdd))]
        public static void PostfixIntAdd(string intName, int amount)
        {
            if (RemoteApplyScope.IsActive) return;
            
            try
            {
                if (SilksongCoopPlugin.Instance?.SessionState?.IsConnected != true) return;
                var pd = PlayerData.instance;
                if (pd == null) return;

                if (intName == "nailUpgrades" && SilksongCoopPlugin.Instance?.VitalsSync != null)
                {
                    if (pd.nailUpgrades != _lastNailUpgradeLevel)
                    {
                        _lastNailUpgradeLevel = pd.nailUpgrades;
                        SilksongCoopPlugin.Instance.VitalsSync.OnLocalNeedleUpgrade(pd.nailUpgrades);
                    }
                }
                else if (intName == "heartPieces")
                {
                    SilksongCoopPlugin.Instance?.BroadcastHeartPieces(pd.heartPieces);
                }
                else if (intName == "silkParts")
                {
                    SilksongCoopPlugin.Instance?.BroadcastSilkParts(pd.silkParts);
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[HeroVitalsHooks] PostfixIntAdd error: {ex.Message}");
            }
        }
    }
}
