using System;
using HarmonyLib;

namespace SilksongCoop.Mod.Hooks
{
    /// <summary>
    /// BUG-20: broadcasts an explicit <see cref="SilksongCoop.Core.Protocol.Packets.SceneTransitionPacket"/>
    /// when the local player starts a scene transition, so the remote peer hides our clone
    /// immediately instead of waiting for the next transform tick (up to ~50 ms) — or for the
    /// whole scene load, during which transform streaming stops and the clone used to linger
    /// at its last position. The clone reappears automatically when the arriving player's
    /// transform packets carry a matching scene hash again.
    /// </summary>
    [HarmonyPatch]
    public static class SceneTransitionHooks
    {
        /// <summary>
        /// Hook that fires when the game begins a scene transition.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameManager), nameof(GameManager.BeginSceneTransition))]
        public static void PostfixBeginSceneTransition()
        {
            try
            {
                // The packet only signals "this player is leaving the scene"; the receiver
                // hides the clone and drops buffered snapshots. The scene hash lets the
                // receiver ignore signals that carry no hash.
                SilksongCoopPlugin.Instance?.BroadcastSceneTransitionDeparture();
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[SceneTransitionHooks] PostfixBeginSceneTransition error: {ex.Message}");
            }
        }
    }
}
