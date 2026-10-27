using System;
using System.Collections;
using HarmonyLib;
using SilksongCoop.Core.State;
using SilksongCoop.Mod.State;
using UnityEngine;

namespace SilksongCoop.Mod.Hooks
{
    /// <summary>
    /// Harmony hooks for handling death, spectator mode, and bench respawn synchronization.
    /// </summary>
    [HarmonyPatch]
    public static class DeathHooks
    {
        /// <summary>
        /// Hook that intercepts player death to implement co-op spectator mode.
        /// </summary>
        /// <param name="__instance">GameManager instance.</param>
        /// <param name="waitTime">Time to wait before respawn.</param>
        /// <param name="__result">Result coroutine reference.</param>
        /// <returns>True to run original method, false to suppress.</returns>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameManager), nameof(GameManager.PlayerDead))]
        public static bool PrefixPlayerDead(GameManager __instance, float waitTime, ref IEnumerator __result)
        {
            try
            {
                if (SilksongCoopPlugin.Instance?.SessionState?.IsConnected != true)
                {
                    return true; // Not connected, normal bench respawn
                }

                // Guard against scene transitions
                if (GameManager.instance == null) return true;

                var coordinator = SilksongCoopPlugin.Instance.DeathCoordinator;
                if (coordinator == null) return true;

                // Guard against TeamWipe re-entrancy: allow standard bench reload coroutine to execute
                if (coordinator.CurrentState == DeathState.TeamWipe)
                {
                    return true;
                }

                var hero = HeroController.instance;
                Vector3 pos = (hero != null && hero.gameObject != null) ? hero.transform.position : Vector3.zero;

                bool suppressBench = coordinator.OnLocalPlayerDied(pos.x, pos.y);
                if (suppressBench)
                {
                    // Solo death while partner alive: suppress original bench reload coroutine and enter spectator
                    __result = EmptyRoutine();
                    SilksongCoopPlugin.Instance.EnterSpectatorMode();
                    return false;
                }

                // Team wipe: allow standard bench reload coroutine to execute
                return true;
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[DeathHooks] PrefixPlayerDead error: {ex.Message}");
                return true; // Allow normal flow on error
            }
        }

        private static IEnumerator EmptyRoutine()
        {
            yield break;
        }

        /// <summary>
        /// Hook that fires after the player is ready for respawn at a bench.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameManager), nameof(GameManager.ReadyForRespawn))]
        public static void PostfixReadyForRespawn()
        {
            if (SilksongCoopPlugin.Instance?.SessionState?.IsConnected != true)
            {
                return;
            }

            SilksongCoopPlugin.Instance.OnBenchRespawnCompleted();
        }

        /// <summary>
        /// Hook that fires after setting a bench respawn point with full parameters.
        /// </summary>
        /// <param name="spawnMarker">The spawn marker name.</param>
        /// <param name="sceneName">The scene name.</param>
        /// <param name="spawnType">The spawn type identifier.</param>
        /// <param name="facingRight">Whether the player is facing right.</param>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerData), nameof(PlayerData.SetBenchRespawn), new Type[] { typeof(string), typeof(string), typeof(int), typeof(bool) })]
        public static void PostfixSetBenchRespawnFull(string spawnMarker, string sceneName, int spawnType, bool facingRight)
        {
            if (RemoteApplyScope.IsActive) return;
            if (SilksongCoopPlugin.Instance?.SessionState?.IsConnected != true) return;
            try
            {
                SilksongCoopPlugin.Instance?.BroadcastBenchCheckpoint(sceneName, spawnMarker, spawnType, facingRight);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[DeathHooks] PostfixSetBenchRespawnFull error: {ex.Message}");
            }
        }

        /// <summary>
        /// Hook that fires after setting a bench respawn point with simplified parameters.
        /// </summary>
        /// <param name="spawnMarker">The spawn marker name.</param>
        /// <param name="sceneName">The scene name.</param>
        /// <param name="facingRight">Whether the player is facing right.</param>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerData), nameof(PlayerData.SetBenchRespawn), new Type[] { typeof(string), typeof(string), typeof(bool) })]
        public static void PostfixSetBenchRespawnSimple(string spawnMarker, string sceneName, bool facingRight)
        {
            if (RemoteApplyScope.IsActive) return;
            if (SilksongCoopPlugin.Instance?.SessionState?.IsConnected != true) return;
            try
            {
                SilksongCoopPlugin.Instance?.BroadcastBenchCheckpoint(sceneName, spawnMarker, 1, facingRight);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[DeathHooks] PostfixSetBenchRespawnSimple error: {ex.Message}");
            }
        }
    }
}
