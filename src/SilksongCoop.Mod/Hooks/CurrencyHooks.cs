using System;
using HarmonyLib;
using SilksongCoop.Core.Protocol.Packets;

namespace SilksongCoop.Mod.Hooks
{
    [HarmonyPatch]
    public static class CurrencyHooks
    {
        public static bool IsApplyingRemoteCurrency = false;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CurrencyManager), nameof(CurrencyManager.ChangeCurrency))]
        public static void PostfixChangeCurrency(int amount, CurrencyType type, bool showCounter)
        {
            try
            {
                if (amount == 0) return;
                if (IsApplyingRemoteCurrency) return;
                if (SilksongCoopPlugin.Instance?.SessionState?.IsConnected != true) return;
                if (SilksongCoopPlugin.Instance?.VitalsSync == null) return;

                // Guard against scene transitions
                if (PlayerData.instance == null) return;

                var pd = PlayerData.instance;
                var coreType = (type == CurrencyType.Money) ? Core.Protocol.Packets.CurrencyType.Rosaries : Core.Protocol.Packets.CurrencyType.ShellShards;
                int currentWallet = (type == CurrencyType.Money) ? pd.geo : pd.ShellShards;
                int projectedTotal = Math.Max(0, currentWallet + amount);

                SilksongCoopPlugin.Instance.VitalsSync.OnLocalCurrencyChanged(coreType, amount, projectedTotal, Guid.NewGuid());
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[CurrencyHooks] PostfixChangeCurrency error: {ex.Message}");
            }
        }
    }
}
