using System;
using System.Collections;
using HarmonyLib;
using SilksongCoop.Core.State;
using UnityEngine;

namespace SilksongCoop.Mod.Hooks
{
    [HarmonyPatch]
    public static class DeathHooks
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameManager), nameof(GameManager.PlayerDead))]
        public static bool PrefixPlayerDead(GameManager __instance, float waitTime, ref IEnumerator __result)
        {
            if (SilksongCoopPlugin.Instance?.SessionState?.IsConnected != true)
            {
                return true; // Not connected, normal bench respawn
            }

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

        private static IEnumerator EmptyRoutine()
        {
            yield break;
        }

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
    }
}
