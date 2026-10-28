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

                var coreType = (type == CurrencyType.Money) ? Core.Protocol.Packets.CurrencyType.Rosaries : Core.Protocol.Packets.CurrencyType.ShellShards;
                // CurrencyManager.ChangeCurrency only QUEUES the amount; PlayerData.geo is
                // updated later by ProcessAddCurrency. Passing the stale wallet as
                // "newTotal" used to overwrite the shared wallet and lose the pickup.
                // Pass 0 so VitalsSyncService derives the total from the delta.
                const int projectedTotal = 0;

                ulong pickupKey = PickupContext.CurrentKey;
                SilksongCoopPlugin.Instance.VitalsSync.OnLocalCurrencyChanged(
                    coreType, amount, projectedTotal, Guid.NewGuid(),
                    pickupKey, PickupContext.CurrentX, PickupContext.CurrentY);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[CurrencyHooks] PostfixChangeCurrency error: {ex.Message}");
            }
        }
    }
}
