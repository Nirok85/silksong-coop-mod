using System;
using System.Collections;
using System.Threading;
using SilksongCoop.Core.Revive;
using SilksongCoop.Core.State;
using UnityEngine;

namespace SilksongCoop.Mod
{
    public partial class SilksongCoopPlugin
    {
        #region Spectator and Death Handling
        /// <summary>
        /// Enters spectator mode after local player death, hiding the hero and disabling physics.
        /// </summary>
        public void EnterSpectatorMode()
        {
            try
            {
                var hero = HeroController.instance;
                if (hero != null && hero.gameObject != null)
                {
                    hero.AffectedByGravity(false);
                    hero.RelinquishControl();

                    if (!Spectator.IsActive)
                    {
                        Spectator.DisabledColliders.Clear();
                        Spectator.DisabledRenderers.Clear();
                    }

                    // HIGH-01: Disable colliders so invisible dead body doesn't block path or take hits
                    foreach (var col in hero.gameObject.GetComponentsInChildren<Collider2D>())
                    {
                        if (col != null && col.enabled)
                        {
                            col.enabled = false;
                            Spectator.DisabledColliders.Add(col);
                        }
                    }

                    foreach (var ren in hero.gameObject.GetComponentsInChildren<Renderer>())
                    {
                        if (ren != null && ren.enabled)
                        {
                            ren.enabled = false;
                            Spectator.DisabledRenderers.Add(ren);
                        }
                    }

                    Spectator.IsActive = true;
                }

                // Tether CameraTarget to free mode so it can smoothly follow remote partner
                var camTarget = GameManager.instance?.cameraCtrl?.camTarget;
                camTarget?.StartFreeMode(false);

                SessionState?.SetSpectating();
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Coop] Failed entering spectator mode: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Exits spectator mode and restores control to the local player.
        /// </summary>
        public void ExitSpectatorMode()
        {
            try
            {
                var hero = HeroController.instance;
                if (hero != null && hero.gameObject != null)
                {
                    hero.AffectedByGravity(true);
                    hero.RegainControl(allowInput: true);

                    RestoreSpectatorComponents();
                }

                Spectator.IsActive = false;
                var camTarget = GameManager.instance?.cameraCtrl?.camTarget;
                camTarget?.EndFreeMode();
                GameManager.instance?.cameraCtrl?.PositionToHero(forceDirect: false);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Failed exiting spectator mode: {ex.Message}");
            }
        }

        /// <summary>
        /// Re-enables exactly the components that <see cref="EnterSpectatorMode"/> disabled.
        /// Enabling every child collider/renderer also revived components the game had
        /// disabled for its own reasons (invisible hazards, scripted sequences).
        /// </summary>
        private void RestoreSpectatorComponents()
        {
            for (int i = 0; i < Spectator.DisabledColliders.Count; i++)
            {
                var col = Spectator.DisabledColliders[i];
                if (col != null) col.enabled = true;
            }
            Spectator.DisabledColliders.Clear();

            for (int i = 0; i < Spectator.DisabledRenderers.Count; i++)
            {
                var ren = Spectator.DisabledRenderers[i];
                if (ren != null) ren.enabled = true;
            }
            Spectator.DisabledRenderers.Clear();
        }

        /// <summary>
        /// Revives the local player at the remote partner's position with partial health.
        /// </summary>
        /// <param name="x">X coordinate of the partner's position.</param>
        /// <param name="y">Y coordinate of the partner's position.</param>
        public void ReviveAtPartner(float x, float y)
        {
            try
            {
                var hero = HeroController.instance;
                if (hero == null || hero.gameObject == null)
                {
                    // BUG-N10: without a hero there is nothing to revive, but staying in
                    // RespawningAtPartner forever soft-locked the session (no countdown, no
                    // transforms, no input). Fall back to a bench respawn instead.
                    Logger.LogWarning("[Coop] Cannot revive: HeroController or gameObject is null — falling back to bench respawn");
                    ExitSpectatorMode();
                    DeathCoordinator?.ForceBenchRespawn(notifyPartner: false);
                    return;
                }

                // BUG-B4: never teleport across scenes — if the partner moved to another room
                // while we were dead/spectating, their coordinates are meaningless (and
                // likely wall-embedded) inside our current scene. Fall back to a team-wipe
                // bench respawn, which always lands on the local scene's bench.
                if (CloneSpawner != null &&
                    SessionState?.RemotePeerId != null &&
                    !CloneSpawner.IsRemoteInSameScene(SessionState.RemotePeerId.Value))
                {
                    Logger.LogInfo("[Coop] Partner is in a different scene — falling back to bench respawn");
                    ExitSpectatorMode();
                    // Solo fallback: the partner is alive in another room. Broadcasting a
                    // team wipe here killed them (issue #2.1) and reloaded their bench.
                    DeathCoordinator?.ForceBenchRespawn(notifyPartner: false);
                    return;
                }

                // 1. Resolve safe ground position via GroundProber
                // BUG-17: if prober fails, fall back to the partner's own position —
                // the partner is standing alive there, so it is ground-truth safe.
                // (hazardRespawnLocation may point to a stale hazard marker in another
                // room/session and would spawn the player out of bounds.)
                Vector2 spawnPos = new Vector2(x, y + 0.5f);
                if (GroundProber != null && GroundProber.TryFindSafeGround(x, y, out float safeX, out float safeY))
                {
                    spawnPos = new Vector2(safeX, safeY);
                }

                // 2. Position hero and reset linear velocity
                hero.transform.position = new Vector3(spawnPos.x, spawnPos.y, 0f);
                var rb = hero.GetComponent<Rigidbody2D>();
                if (rb != null)
                {
                    rb.linearVelocity = Vector2.zero;
                }

                // 3. Restore camera from spectator free mode back to hero following
                var camTarget = GameManager.instance?.cameraCtrl?.camTarget;
                camTarget?.EndFreeMode();

                // 4. Calculate and apply the configured fraction of max HP.
                // MaxHealthKeepBlue() calls PlayerData.MaxHealth(), which sets health to the
                // FULL maximum — so it must run BEFORE we assign the revive HP, otherwise
                // ReviveHealthPercent is silently overwritten with 100% (issue #1.4).
                var pd = PlayerData.instance;
                if (pd != null)
                {
                    hero.MaxHealthKeepBlue();

                    float reviveHealthPercent = Math.Clamp(ReviveHealthPercentConfig?.Value ?? 0.5f, 0.1f, 1f);
                    int reviveHp = Math.Max(1, Math.Min(pd.maxHealth, (int)Math.Round(pd.maxHealth * reviveHealthPercent)));
                    pd.health = reviveHp;
                    pd.prevHealth = reviveHp;
                    pd.healthBlue = 0;
                    EventRegister.SendEvent(EventRegisterEvents.HeroHealed);
                    EventRegister.SendEvent(EventRegisterEvents.HealthUpdate);
                    VitalsSync?.BroadcastCurrentVitals();
                }

                // 5. Re-enable the renderers and colliders disabled by EnterSpectatorMode
                RestoreSpectatorComponents();

                // 6. Restore player state in exact engine sequence: clear dead before RegainControl!
                Spectator.IsActive = false;
                hero.cState.dead = false;
                hero.cState.hazardDeath = false;
                hero.cState.recoiling = false;
                hero.hero_state = GlobalEnums.ActorStates.idle;
                hero.heroBox?.HeroBoxNormal();
                hero.AffectedByGravity(true);
                hero.RegainControl(allowInput: true);
                hero.StartAnimationControlToIdle();

                // 7. Re-center camera
                GameManager.instance?.cameraCtrl?.PositionToHero(forceDirect: false);

                // 8. Apply the configured invulnerability window (was hard-coded 2.5 s)
                float invulnerabilitySeconds = Math.Clamp(
                    ReviveInvulnerabilityConfig?.Value ?? SafeGroundSolver.InvulnerabilityDuration,
                    0f, 10f);
                StartCoroutine(ReviveInvulnerabilityRoutine(hero, invulnerabilitySeconds));

                DeathCoordinator?.CompleteRevival();
                SessionState?.ReturnToActive();
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Coop] Failed reviving at partner: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private IEnumerator ReviveInvulnerabilityRoutine(HeroController hero, float duration)
        {
            hero.SetDamageMode(GlobalEnums.DamageMode.HAZARD_ONLY);
            hero.cState.invulnerable = true;
            var pulse = hero.GetComponent<InvulnerablePulse>();
            try
            {
                pulse?.StartInvulnerablePulse();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Failed to start invulnerable pulse: {ex.Message}");
            }

            float elapsed = 0f;
            while (elapsed < duration)
            {
                if (hero == null || hero.gameObject == null)
                    yield break; // Hero destroyed (scene transition) — abort coroutine
                yield return null;
                // BUG-01: unscaled time — Time.deltaTime is 0 while the game is paused,
                // which would freeze the routine and leave the hero invulnerable forever.
                elapsed += Time.unscaledDeltaTime;
            }

            try
            {
                pulse?.StopInvulnerablePulse();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Failed to stop invulnerable pulse: {ex.Message}");
            }

            hero.cState.invulnerable = false;
            hero.cState.recoiling = false;
            hero.SetDamageMode(GlobalEnums.DamageMode.FULL_DAMAGE);
        }

        /// <summary>
        /// Marks that the current call stack is inside the Harmony prefix for
        /// <see cref="GameManager.PlayerDead"/> (see BUG-15). Internal use by DeathHooks.
        /// </summary>
        internal void BeginPlayerDeadHook() => _insidePlayerDeadHook = true;

        /// <summary>
        /// True while a team wipe / forced bench reload is pending. DeathHooks uses this to
        /// keep calling <see cref="OnBenchRespawnCompleted"/> even after the session was
        /// torn down (the disconnect handler starts a wipe and then disconnects).
        /// </summary>
        internal bool IsTeamWipeInProgress => Volatile.Read(ref _teamWipeInProgress) != 0;

        /// <summary>
        /// Clears the in-hook marker set by <see cref="BeginPlayerDeadHook"/>.
        /// </summary>
        internal void EndPlayerDeadHook() => _insidePlayerDeadHook = false;

        /// <summary>
        /// Triggers a team wipe, forcing both players to respawn at a bench.
        /// </summary>
        public void TriggerTeamWipe()
        {
            // BUG-04: atomic check-and-set — two concurrent callers cannot both pass.
            if (Interlocked.CompareExchange(ref _teamWipeInProgress, 1, 0) != 0) return;

            try
            {
                SessionState?.SetTeamWipe();

                var gm = GameManager.instance;
                if (gm != null)
                {
                    // BUG-15: when triggered from DeathHooks.PrefixPlayerDead, the game is
                    // already running the PlayerDead coroutine (the prefix allows the original
                    // through for TeamWipe). Starting it here as well would reload the bench
                    // twice; remote-triggered wipes still need the explicit start.
                    if (!_insidePlayerDeadHook)
                    {
                        gm.StartCoroutine(gm.PlayerDead(1.5f));
                    }
                }
                else
                {
                    // Never leave the wipe latch set when no coroutine could be started,
                    // otherwise every later team wipe no-ops until the game restarts.
                    Interlocked.Exchange(ref _teamWipeInProgress, 0);
                    Logger.LogWarning("[Coop] GameManager.instance is null during team wipe");
                }
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _teamWipeInProgress, 0);
                Logger.LogError($"[Coop] Failed triggering team wipe: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Called after a bench respawn completes, resetting death state and restoring visuals.
        /// </summary>
        public void OnBenchRespawnCompleted()
        {
            try
            {
                Interlocked.Exchange(ref _teamWipeInProgress, 0);

                // BUG-A1: if the mod itself disabled the hero's physics while spectating
                // (gravity off, control relinquished, colliders off), the game's own
                // bench-respawn flow will NOT undo those — the hero would fall through the
                // room with no input after a team-wipe bench reload. Restore everything.
                if (Spectator.IsActive)
                {
                    Spectator.IsActive = false;
                    var heroRestore = HeroController.instance;
                    if (heroRestore != null && heroRestore.gameObject != null)
                    {
                        heroRestore.AffectedByGravity(true);
                        heroRestore.RegainControl(allowInput: true);
                        RestoreSpectatorComponents();
                    }
                }

                var camTarget = GameManager.instance?.cameraCtrl?.camTarget;
                camTarget?.EndFreeMode();

                DeathCoordinator?.ResetAfterBenchRespawn();
                DeathCoordinator?.BroadcastEpochSync();
                // A bench reload resets the world's enemies/drops.
                VitalsSync?.ClearClaimedPickups();
                SessionState?.ReturnToActive();

                var hero = HeroController.instance;
                if (hero != null && hero.gameObject != null)
                {
                    hero.cState.dead = false;
                    hero.cState.hazardDeath = false;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Coop] Failed on bench respawn completed: {ex.Message}\n{ex.StackTrace}");
            }
        }
        #endregion
    }
}
