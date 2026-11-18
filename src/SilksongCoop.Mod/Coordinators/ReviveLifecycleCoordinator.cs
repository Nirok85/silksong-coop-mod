using System;
using System.Collections;
using System.Threading;
using BepInEx.Configuration;
using SilksongCoop.Core.Localization;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Revive;
using SilksongCoop.Core.State;
using SilksongCoop.Mod.Clones;
using SilksongCoop.Mod.Services;
using SilksongCoop.Mod.State;
using SilksongCoop.Mod.UI;
using UnityEngine;

namespace SilksongCoop.Mod.Coordinators
{
    /// <summary>
    /// Coordinates death lifecycle, spectator mode, partner revive, and team wipe flows.
    /// Implements IReviveGameplayActions to decouple revive logic from the main plugin.
    /// </summary>
    public sealed class ReviveLifecycleCoordinator : IReviveGameplayActions, ISyncOrchestrator, IDisposable
    {
        private readonly ISessionService _sessionService;
        private readonly DeathLifecycleCoordinator _deathCoordinator;
        private readonly IGroundProber _groundProber;
        private readonly Func<RemoteHornetSpawner?> _cloneSpawnerProvider;
        private readonly Func<CoopMenuUI?> _menuUIProvider;
        private readonly MonoBehaviour _coroutineRunner;
        private readonly ConfigEntry<float>? _reviveCooldownConfig;
        private readonly ConfigEntry<float>? _reviveHealthPercentConfig;
        private readonly ConfigEntry<float>? _reviveInvulnerabilityConfig;

        private readonly SpectatorState _spectator = new SpectatorState();
        private bool _reviveInvulnerabilityActive;
        private int _teamWipeInProgress;
        private bool _insidePlayerDeadHook;
        private bool _spectatorCameraFollow = true;

        public SpectatorState Spectator => _spectator;
        public bool IsReviveInvulnerabilityActive
        {
            get => _reviveInvulnerabilityActive;
            set => _reviveInvulnerabilityActive = value;
        }
        public bool IsTeamWipeInProgress => Volatile.Read(ref _teamWipeInProgress) != 0;
        public bool SpectatorCameraFollow
        {
            get => _spectatorCameraFollow;
            set => _spectatorCameraFollow = value;
        }

        public ReviveLifecycleCoordinator(
            ISessionService sessionService,
            DeathLifecycleCoordinator deathCoordinator,
            IGroundProber groundProber,
            Func<RemoteHornetSpawner?> cloneSpawnerProvider,
            Func<CoopMenuUI?> menuUIProvider,
            MonoBehaviour coroutineRunner,
            ConfigEntry<float>? reviveCooldownConfig = null,
            ConfigEntry<float>? reviveHealthPercentConfig = null,
            ConfigEntry<float>? reviveInvulnerabilityConfig = null)
        {
            _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
            _deathCoordinator = deathCoordinator ?? throw new ArgumentNullException(nameof(deathCoordinator));
            _groundProber = groundProber ?? throw new ArgumentNullException(nameof(groundProber));
            _cloneSpawnerProvider = cloneSpawnerProvider ?? throw new ArgumentNullException(nameof(cloneSpawnerProvider));
            _menuUIProvider = menuUIProvider ?? throw new ArgumentNullException(nameof(menuUIProvider));
            _coroutineRunner = coroutineRunner ?? throw new ArgumentNullException(nameof(coroutineRunner));
            _reviveCooldownConfig = reviveCooldownConfig;
            _reviveHealthPercentConfig = reviveHealthPercentConfig;
            _reviveInvulnerabilityConfig = reviveInvulnerabilityConfig;
        }

        public void Initialize() { }

        public void OnSessionStarted(bool isHost)
        {
            ResetTeamWipeLatch();
            _reviveInvulnerabilityActive = false;
            _insidePlayerDeadHook = false;
        }

        public void OnSessionEnded()
        {
            ResetTeamWipeLatch();
            _reviveInvulnerabilityActive = false;
            _insidePlayerDeadHook = false;
            if (_spectator.IsActive)
            {
                ExitSpectatorMode();
            }
        }

        public void Dispose()
        {
            OnSessionEnded();
        }

        public void Update()
        {
            try
            {
                var session = _sessionService.SessionState;
                var remotePeers = session?.RemotePeerIds;

                // If spectating, tether the camera to a living partner. The
                // coordinator prefers a living peer's position when several exist.
                if (_deathCoordinator != null &&
                    _deathCoordinator.CurrentState == DeathState.Spectating &&
                    _spectatorCameraFollow &&
                    remotePeers != null && remotePeers.Count > 0)
                {
                    var (targetX, targetY) = _deathCoordinator.LastPartnerPosition;
                    var camTarget = GameManager.instance?.cameraCtrl?.camTarget;
                    if (camTarget != null)
                    {
                        var curPos = camTarget.transform.position;
                        camTarget.transform.position = new Vector3(targetX, targetY, curPos.z);
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[ReviveCoordinator] Error updating spectator follow: {ex.Message}");
            }
        }

        public void ResetTeamWipeLatch()
        {
            Interlocked.Exchange(ref _teamWipeInProgress, 0);
        }

        public void BeginPlayerDeadHook() => _insidePlayerDeadHook = true;
        public void EndPlayerDeadHook() => _insidePlayerDeadHook = false;

        public void EnterSpectatorMode() => TryEnterSpectatorMode();

        public bool TryEnterSpectatorMode()
        {
            try
            {
                var hero = HeroController.instance;
                if (hero == null || hero.gameObject == null) return false;

                hero.AffectedByGravity(false);
                hero.RelinquishControl();

                if (!_spectator.IsActive)
                {
                    _spectator.DisabledColliders.Clear();
                    _spectator.DisabledRenderers.Clear();
                }

                // HIGH-01: Disable colliders so invisible dead body doesn't block path or take hits
                foreach (var col in hero.gameObject.GetComponentsInChildren<Collider2D>())
                {
                    if (col != null && col.enabled)
                    {
                        col.enabled = false;
                        _spectator.DisabledColliders.Add(col);
                    }
                }

                foreach (var ren in hero.gameObject.GetComponentsInChildren<Renderer>())
                {
                    if (ren != null && ren.enabled)
                    {
                        ren.enabled = false;
                        _spectator.DisabledRenderers.Add(ren);
                    }
                }

                _spectator.IsActive = true;

                // Tether CameraTarget to free mode so it can smoothly follow remote partner
                var camTarget = GameManager.instance?.cameraCtrl?.camTarget;
                camTarget?.StartFreeMode(false);

                _sessionService.SessionState?.SetSpectating();
                return true;
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogError($"[ReviveCoordinator] Failed entering spectator mode: {ex.Message}\n{ex.StackTrace}");
                ExitSpectatorMode();
                return false;
            }
        }

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

                _spectator.IsActive = false;
                var camTarget = GameManager.instance?.cameraCtrl?.camTarget;
                camTarget?.EndFreeMode();
                GameManager.instance?.cameraCtrl?.PositionToHero(forceDirect: false);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[ReviveCoordinator] Failed exiting spectator mode: {ex.Message}");
            }
        }

        public void RestoreSpectatorComponents()
        {
            for (int i = 0; i < _spectator.DisabledColliders.Count; i++)
            {
                var col = _spectator.DisabledColliders[i];
                if (col != null) col.enabled = true;
            }
            _spectator.DisabledColliders.Clear();

            for (int i = 0; i < _spectator.DisabledRenderers.Count; i++)
            {
                var ren = _spectator.DisabledRenderers[i];
                if (ren != null) ren.enabled = true;
            }
            _spectator.DisabledRenderers.Clear();
        }

        public void ReviveAtPartner(float x, float y)
        {
            TryReviveAtPartner(x, y, targetPeer: null);
        }

        public bool TryReviveAtPartner(float x, float y, NetworkPeerId? targetPeer)
        {
            try
            {
                var hero = HeroController.instance;
                if (hero == null || hero.gameObject == null)
                {
                    return FallbackFromPartnerRevive(null, "HeroController or gameObject is null");
                }

                var session = _sessionService.SessionState;
                NetworkPeerId? scenePeer = targetPeer;
                if (!scenePeer.HasValue && session?.RemotePeerId != null)
                {
                    scenePeer = session.RemotePeerId.Value;
                }

                if (targetPeer.HasValue && !IsReviveTargetAvailable(targetPeer.Value))
                {
                    return FallbackFromPartnerRevive(
                        "Partner is unavailable — reviving at the last bench.",
                        $"selected peer {targetPeer.Value} is no longer alive/connected");
                }

                var cloneSpawner = _cloneSpawnerProvider();
                if (cloneSpawner != null && scenePeer.HasValue &&
                    !cloneSpawner.IsRemoteInSameScene(scenePeer.Value))
                {
                    return FallbackFromPartnerRevive(
                        "Partner is in another room — reviving at the last bench.",
                        $"partner {scenePeer.Value} is in a different scene");
                }

                if (_groundProber == null || !_groundProber.TryFindSafeGround(x, y, out float safeX, out float safeY))
                {
                    return FallbackFromPartnerRevive(
                        "No safe ground near partner — reviving at the last bench.",
                        $"Safe ground prober failed near ({x:F1}, {y:F1})");
                }

                Vector2 spawnPos = new Vector2(safeX, safeY);

                hero.transform.position = new Vector3(spawnPos.x, spawnPos.y, 0f);
                var rb = hero.GetComponent<Rigidbody2D>();
                if (rb != null)
                {
                    rb.linearVelocity = Vector2.zero;
                }

                var camTarget = GameManager.instance?.cameraCtrl?.camTarget;
                camTarget?.EndFreeMode();

                var pd = PlayerData.instance;
                if (pd != null)
                {
                    hero.MaxHealthKeepBlue();
                    float reviveHealthPercent = Math.Clamp(_reviveHealthPercentConfig?.Value ?? 0.5f, 0.1f, 1f);
                    int reviveHp = Math.Max(1, Math.Min(pd.maxHealth, (int)Math.Round(pd.maxHealth * reviveHealthPercent)));
                    pd.health = reviveHp;
                    pd.prevHealth = reviveHp;
                    pd.healthBlue = 0;
                    EventRegister.SendEvent(EventRegisterEvents.HeroHealed);
                    EventRegister.SendEvent(EventRegisterEvents.HealthUpdate);
                    SilksongCoopPlugin.Instance?.VitalsSync?.BroadcastCurrentVitals();
                }

                RestoreSpectatorComponents();

                _spectator.IsActive = false;
                hero.cState.dead = false;
                hero.cState.hazardDeath = false;
                hero.cState.recoiling = false;
                hero.hero_state = GlobalEnums.ActorStates.idle;
                hero.heroBox?.HeroBoxNormal();
                hero.AffectedByGravity(true);
                hero.RegainControl(allowInput: true);
                hero.StartAnimationControlToIdle();

                GameManager.instance?.cameraCtrl?.PositionToHero(forceDirect: false);

                float invulnerabilitySeconds = Math.Clamp(
                    _reviveInvulnerabilityConfig?.Value ?? SafeGroundSolver.InvulnerabilityDuration,
                    0f, 10f);
                _coroutineRunner.StartCoroutine(ReviveInvulnerabilityRoutine(hero, invulnerabilitySeconds));

                _deathCoordinator.ConfirmReviveAtPartner(spawnPos.x, spawnPos.y);
                session?.ReturnToActive();
                return true;
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogError($"[ReviveCoordinator] Failed reviving at partner: {ex.Message}\n{ex.StackTrace}");
                return FallbackFromPartnerRevive(null, $"revive threw: {ex.Message}");
            }
        }

        public bool IsReviveTargetAvailable(NetworkPeerId peer)
        {
            if (_deathCoordinator != null && !_deathCoordinator.IsRemotePeerAlive(peer))
                return false;

            var session = _sessionService.SessionState;
            if (session?.IsConnected != true)
                return false;

            bool registered = false;
            var peers = session.RemotePeerIds;
            for (int i = 0; i < peers.Count; i++)
            {
                if (peers[i] == peer)
                {
                    registered = true;
                    break;
                }
            }
            if (!registered) return false;

            var cloneSpawner = _cloneSpawnerProvider();
            if (cloneSpawner != null && !cloneSpawner.IsRemoteInSameScene(peer))
                return false;

            return true;
        }

        public bool FallbackFromPartnerRevive(string? message, string reason)
        {
            SilksongCoopPlugin.LogWarn($"[ReviveCoordinator] Partner revive fallback: {reason}");
            if (message != null)
            {
                _menuUIProvider()?.SetStatusMessage(Loc.T(message));
            }

            ExitSpectatorMode();
            _deathCoordinator.FailReviveAtPartner();
            _deathCoordinator.BeginSoloBenchRespawn();
            return false;
        }

        public void TriggerTeamWipe()
        {
            if (Interlocked.CompareExchange(ref _teamWipeInProgress, 1, 0) != 0) return;

            try
            {
                _sessionService.SessionState?.SetTeamWipe();

                var gm = GameManager.instance;
                if (gm != null)
                {
                    if (!_insidePlayerDeadHook)
                    {
                        gm.StartCoroutine(gm.PlayerDead(1.5f));
                    }
                }
                else
                {
                    Interlocked.Exchange(ref _teamWipeInProgress, 0);
                    SilksongCoopPlugin.LogWarn("[ReviveCoordinator] GameManager.instance is null during team wipe");
                }
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _teamWipeInProgress, 0);
                SilksongCoopPlugin.LogError($"[ReviveCoordinator] Failed triggering team wipe: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public void OnBenchRespawnCompleted()
        {
            try
            {
                Interlocked.Exchange(ref _teamWipeInProgress, 0);

                if (_spectator.IsActive)
                {
                    _spectator.IsActive = false;
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

                _deathCoordinator.ResetAfterBenchRespawn();
                _deathCoordinator.BroadcastEpochSync();
                _sessionService.SessionState?.ReturnToActive();

                var hero = HeroController.instance;
                if (hero != null && hero.gameObject != null)
                {
                    hero.cState.dead = false;
                    hero.cState.hazardDeath = false;
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogError($"[ReviveCoordinator] Failed on bench respawn completed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private IEnumerator ReviveInvulnerabilityRoutine(HeroController hero, float duration)
        {
            _reviveInvulnerabilityActive = true;
            hero.SetDamageMode(GlobalEnums.DamageMode.HAZARD_ONLY);
            hero.cState.invulnerable = true;
            var pulse = hero.GetComponent<InvulnerablePulse>();
            try
            {
                pulse?.StartInvulnerablePulse();
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[ReviveCoordinator] Failed to start invulnerable pulse: {ex.Message}");
            }

            try
            {
                float elapsed = 0f;
                while (elapsed < duration)
                {
                    if (hero == null || hero.gameObject == null)
                        yield break;
                    yield return null;
                    elapsed += Time.unscaledDeltaTime;
                }
            }
            finally
            {
                _reviveInvulnerabilityActive = false;
            }

            if (hero == null || hero.gameObject == null) yield break;

            try
            {
                pulse?.StopInvulnerablePulse();
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[ReviveCoordinator] Failed to stop invulnerable pulse: {ex.Message}");
            }

            hero.cState.invulnerable = false;
            hero.cState.recoiling = false;
            hero.SetDamageMode(GlobalEnums.DamageMode.FULL_DAMAGE);
        }
    }
}
