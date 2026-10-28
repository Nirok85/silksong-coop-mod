using System;
using HarmonyLib;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Mod.State;

namespace SilksongCoop.Mod.Hooks
{
    /// <summary>
    /// Hooks that feed local world changes into <see cref="SilksongCoop.Mod.State.EnemySyncController"/>:
    /// enemy damage, physical currency pickups, breakable walls and gates.
    /// </summary>
    [HarmonyPatch]
    public static class WorldSyncHooks
    {
        /// <summary>State captured by the hit prefix and consumed by the postfix.</summary>
        public struct HitHookState
        {
            public HealthManager? Target;
            public int HpBefore;
            public bool Valid;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(HealthManager), nameof(HealthManager.Hit))]
        public static void PrefixHit(HealthManager __instance, out HitHookState __state)
        {
            __state = default;
            if (RemoteApplyScope.IsActive) return;

            var target = __instance?.SendDamageTo ?? __instance;
            if (target == null) return;

            __state = new HitHookState
            {
                Target = target,
                HpBefore = target.hp,
                Valid = true
            };
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HealthManager), nameof(HealthManager.Hit))]
        public static void PostfixHit(HealthManager __instance, HitInstance hitInstance, HitHookState __state)
        {
            if (!__state.Valid || __state.Target == null) return;
            if (RemoteApplyScope.IsActive) return;

            // Only hero-originated damage is replicated. Environmental/scripted damage is
            // simulated independently on both machines; broadcasting it would apply it twice.
            if (!hitInstance.IsHeroDamage) return;

            var target = __state.Target;
            if (target == null) return;

            int hpAfter = target.hp;
            bool dead = target.GetIsDead() || hpAfter <= 0;

            // A hit that was fully blocked (invincibility/tink) leaves HP unchanged.
            if (!dead && hpAfter >= __state.HpBefore) return;

            SilksongCoopPlugin.Instance?.EnemySync?.OnLocalHit(target, hitInstance, hpAfter, dead);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CurrencyObjectBase), nameof(CurrencyObjectBase.DoCollect))]
        public static void PrefixDoCollect(CurrencyObjectBase __instance)
        {
            PickupContext.End();
            if (RemoteApplyScope.IsActive) return;
            if (SilksongCoopPlugin.Instance?.SessionState?.IsConnected != true) return;

            ulong key = WorldObjectKey.ComputePickup(__instance);
            if (key == 0) return;

            var position = __instance.transform.position;
            PickupContext.Begin(key, position.x, position.y);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CurrencyObjectBase), nameof(CurrencyObjectBase.DoCollect))]
        public static void PostfixDoCollect()
        {
            PickupContext.End();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Breakable), nameof(Breakable.Break))]
        public static void PostfixBreakableBreak(Breakable __instance)
        {
            if (RemoteApplyScope.IsActive) return;

            SilksongCoopPlugin.Instance?.EnemySync?.OnLocalWorldEvent(WorldEventType.BreakableBroken, __instance?.transform);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Gate), nameof(Gate.Open))]
        public static void PostfixGateOpen(Gate __instance)
        {
            if (RemoteApplyScope.IsActive) return;

            SilksongCoopPlugin.Instance?.EnemySync?.OnLocalWorldEvent(WorldEventType.GateOpened, __instance?.transform);
        }

        /// <summary>
        /// Catches deaths that did not come from a hero hit (acid, scripted kills, enemy
        /// infighting). All shorter Die overloads chain to this one.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(HealthManager), nameof(HealthManager.Die), new Type[]
        {
            typeof(float?), typeof(AttackTypes), typeof(NailElements), typeof(UnityEngine.GameObject),
            typeof(bool), typeof(float), typeof(bool), typeof(bool)
        })]
        public static void PostfixDie(HealthManager __instance)
        {
            if (RemoteApplyScope.IsActive) return;

            SilksongCoopPlugin.Instance?.EnemySync?.OnLocalEnemyDeath(__instance);
        }
    }
}
