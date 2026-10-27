using System;
using HarmonyLib;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Mod.State;

namespace SilksongCoop.Mod.Hooks
{
    /// <summary>
    /// Harmony hooks for synchronizing currency changes (geo and shell shards) between co-op players.
    /// </summary>
    [HarmonyPatch]
    public static class CurrencyHooks
    {
        // BUG-08: the old `IsApplyingRemoteCurrency` static field was dead code — the
        // re-entrancy guard was replaced by RemoteApplyScope.IsActive, but the field was
        // never removed and always read as false. Removed to avoid misleading consumers.

        /// <summary>
        /// Hook that fires after currency changes to broadcast the update to the remote player.
        /// </summary>
        /// <param name="amount">The amount of currency changed (positive or negative).</param>
        /// <param name="type">The type of currency (Money or ShellShards).</param>
        /// <param name="showCounter">Whether to show the UI counter animation.</param>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CurrencyManager), nameof(CurrencyManager.ChangeCurrency))]
        public static void PostfixChangeCurrency(int amount, CurrencyType type, bool showCounter)
        {
            try
            {
                if (amount == 0) return;
                if (RemoteApplyScope.IsActive) return;
                if (SilksongCoopPlugin.Instance?.SessionState?.IsConnected != true) return;
                if (SilksongCoopPlugin.Instance?.VitalsSync == null) return;

                // Guard against scene transitions
                if (PlayerData.instance == null) return;

                var pd = PlayerData.instance;
                var coreType = (type == CurrencyType.Money) ? Core.Protocol.Packets.CurrencyType.Rosaries : Core.Protocol.Packets.CurrencyType.ShellShards;
                int currentWallet = (type == CurrencyType.Money) ? pd.geo : pd.ShellShards;
                // Postfix runs AFTER ChangeCurrency has already updated pd.geo / pd.ShellShards.
                // currentWallet already holds the updated balance.
                int projectedTotal = Math.Max(0, currentWallet);

                SilksongCoopPlugin.Instance.VitalsSync.OnLocalCurrencyChanged(coreType, amount, projectedTotal, Guid.NewGuid());
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[CurrencyHooks] PostfixChangeCurrency error: {ex.Message}");
            }
        }
    }
}
