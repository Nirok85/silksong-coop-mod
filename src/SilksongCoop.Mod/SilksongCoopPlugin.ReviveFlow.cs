using System;
using SilksongCoop.Core.Networking;
using UnityEngine;

namespace SilksongCoop.Mod
{
    public partial class SilksongCoopPlugin
    {
        #region Spectator and Death Handling Facades

        private bool _reviveInvulnerabilityActive
        {
            get => ReviveCoordinator?.IsReviveInvulnerabilityActive ?? false;
            set { if (ReviveCoordinator != null) ReviveCoordinator.IsReviveInvulnerabilityActive = value; }
        }

        public void EnterSpectatorMode() => ReviveCoordinator.EnterSpectatorMode();
        internal bool TryEnterSpectatorMode() => ReviveCoordinator.TryEnterSpectatorMode();
        public void ExitSpectatorMode() => ReviveCoordinator.ExitSpectatorMode();
        internal void RestoreSpectatorComponents() => ReviveCoordinator.RestoreSpectatorComponents();

        public void ReviveAtPartner(float x, float y)
        {
            // GroundProber == null || !GroundProber.TryFindSafeGround -> FallbackFromPartnerRevive
            // Invariant ordering:
            // hero.cState.dead = false;
            // hero.RegainControl(allowInput: true);
            // ReviveInvulnerabilityRoutine(hero, duration);
            ReviveCoordinator.ReviveAtPartner(x, y);
        }
        public bool TryReviveAtPartner(float x, float y, NetworkPeerId? targetPeer) =>
            ReviveCoordinator.TryReviveAtPartner(x, y, targetPeer);

        internal void BeginPlayerDeadHook() => ReviveCoordinator.BeginPlayerDeadHook();
        internal bool IsTeamWipeInProgress => ReviveCoordinator.IsTeamWipeInProgress;
        internal void EndPlayerDeadHook() => ReviveCoordinator.EndPlayerDeadHook();
        public void TriggerTeamWipe() => ReviveCoordinator.TriggerTeamWipe();
        public void OnBenchRespawnCompleted() => ReviveCoordinator.OnBenchRespawnCompleted();

        #endregion
    }
}
