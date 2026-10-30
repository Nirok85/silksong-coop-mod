// ============================================================================
// SilksongCoopPlugin.cs - Main BepInEx plugin entry point for Silksong Co-op.
// Coordinates session state, networking transports, vitals and inventory
// synchronization, spectator & death lifecycle, and remote player cloning.
// ============================================================================

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using BepInEx;
using HarmonyLib;
using SilksongCoop.Core;
using SilksongCoop.Core.Clones;
using SilksongCoop.Core.DI;
using SilksongCoop.Core.Events;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Revive;
using SilksongCoop.Core.State;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.Visuals;
using SilksongCoop.Mod.Clones;
using SilksongCoop.Mod.Hooks;
using SilksongCoop.Mod.Networking;
using SilksongCoop.Mod.Revive;
using SilksongCoop.Mod.Services;
using SilksongCoop.Mod.Settings;
using SilksongCoop.Mod.State;
using SilksongCoop.Mod.UI;
using SilksongCoop.Mod.Visuals;
using UnityEngine;

namespace SilksongCoop.Mod
{
    /// <summary>
    /// Main BepInEx plugin entry point for Silksong Co-op multiplayer.
    /// Coordinates session state, networking, vitals sync, death lifecycle, and remote player management.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class SilksongCoopPlugin : BaseUnityPlugin
    {
        #region Plugin Constants and Static Members
        public const string PluginGuid = "com.silksong.coop";
        public const string PluginName = "Silksong Co-op Multiplayer";
        public const string PluginVersion = "2.2.2";

        public static SilksongCoopPlugin? Instance { get; private set; }
        /// <summary>
        /// Logs an informational message using the plugin's logger.
        /// </summary>
        /// <param name="message">The message to log.</param>
        public static void LogInfo(string message) => Instance?.Logger.LogInfo(message);
        /// <summary>
        /// Logs a warning message using the plugin's logger.
        /// </summary>
        /// <param name="message">The message to log.</param>
        public static void LogWarn(string message) => Instance?.Logger.LogWarning(message);

        public static IServiceContainer? Container { get; private set; }
        public static IEventBus? EventBus { get; private set; }
        #endregion

        #region Public Properties
        public CoopSessionState SessionState { get; private set; } = null!;
        public VitalsSyncService VitalsSync { get; private set; } = null!;
        public DeathLifecycleCoordinator DeathCoordinator { get; private set; } = null!;
        public InventorySyncService InventorySync { get; private set; } = null!;
        
        /// <summary>
        /// Ground prober for finding safe spawn locations during revive.
        /// </summary>
        public IGroundProber GroundProber { get; set; } = new UnityGroundProber();
        public KeybindManager Keybinds { get; private set; } = null!;
        public QualityPresetManager QualityPresets { get; private set; } = null!;

        #region BepInEx Configuration
        // BepInEx Config entries
        public BepInEx.Configuration.ConfigEntry<float>? ReviveCooldownConfig;
        public BepInEx.Configuration.ConfigEntry<float>? ReviveHealthPercentConfig;
        public BepInEx.Configuration.ConfigEntry<float>? ReviveInvulnerabilityConfig;
        public BepInEx.Configuration.ConfigEntry<bool>? EnablePlayer2DashTrailConfig;
        public BepInEx.Configuration.ConfigEntry<float>? Player2HueShiftConfig;
        public BepInEx.Configuration.ConfigEntry<int>? DefaultUdpPortConfig;
        public BepInEx.Configuration.ConfigEntry<int>? TickRateHzConfig;
        public BepInEx.Configuration.ConfigEntry<bool>? AutoReconnectConfig;
        public BepInEx.Configuration.ConfigEntry<int>? MaxReconnectAttemptsConfig;
        public BepInEx.Configuration.ConfigEntry<bool>? SaveConnectionHistoryConfig;
        #endregion

        public INetworkTransport? ActiveTransport { get; private set; }
        public SteamP2PTransport? SteamTransport { get; private set; }
        public LiteNetLibTransport? UdpTransport { get; private set; }
        public CoopMenuUI MenuUI { get; private set; } = null!;
        public RemoteHornetSpawner CloneSpawner { get; private set; } = null!;
        public EnemySyncController? EnemySync { get; private set; }

        // Orchestrator services that decouple network hot-path from the plugin.
        private readonly System.Collections.Generic.List<ISyncOrchestrator> _orchestrators = new System.Collections.Generic.List<ISyncOrchestrator>();
        private NetworkOrchestrator? _networkOrchestrator;
        private VitalsSyncManager? _vitalsSyncManager;
        private InventorySyncManager? _inventorySyncManager;
        private DeathSyncManager? _deathSyncManager;
        #endregion

        #region Private Fields
        private Harmony? _harmony;

        // BUG-04: atomic flag (0 = not in progress, 1 = in progress). A volatile bool used
        // as check-then-set had a TOCTOU race window; Interlocked CAS makes it atomic.
        private int _teamWipeInProgress;

        // BUG-15: set while DeathHooks.PrefixPlayerDead runs its death branch. TriggerTeamWipe
        // skips its explicit PlayerDead coroutine in that case — the game's own invocation
        // already runs the original method, so starting it again would reload the bench twice.
        private bool _insidePlayerDeadHook;

        // BUG-A1: true while the mod itself disabled the hero's physics (EnterSpectatorMode).
        // The game's bench-respawn flow never restores OUR disables, so OnBenchRespawnCompleted
        // must undo them — otherwise the hero falls through geometry with no input.
        private bool _spectatorActive;

        // Components the mod itself disabled when entering spectator mode. Restoring only
        // these avoids re-enabling colliders/renderers the game disabled for its own reasons.
        private readonly List<Collider2D> _spectatorDisabledColliders = new List<Collider2D>();
        private readonly List<Renderer> _spectatorDisabledRenderers = new List<Renderer>();

        // F9: while spectating, keep the free camera tethered to the partner (default) or
        // leave it where the player died. The keybind was documented but never read.
        private bool _spectatorCameraFollow = true;

        private ulong? _pendingSteamLobbyJoin;

        // Auto-reconnect state
        private bool _reconnectPending;
        private float _nextReconnectTime;
        private int _reconnectAttempts;
        private bool _reconnectIsSteam;
        private string? _reconnectAddress;

        // Last counter value broadcast to the peer. Counters are shared progress, so after
        // the connect-time baseline both sides exchange deltas instead of absolute values —
        // an absolute overwrite let the peer's partial count clobber ours (issue #2.3).
        private int _lastHeartPiecesBroadcast = -1;
        private int _lastSilkPartsBroadcast = -1;

        private Core.State.DesyncDetector? _desyncDetector;
        private uint? _desyncRound;
        #endregion

        #region Unity Lifecycle
        private void Awake()
        {
            Instance = this;
            SteamP2PTransport.Logger = Logger;
            CoopLog.Sink = message => Logger.LogWarning(message);
            Logger.LogInfo($"Initializing {PluginName} v{PluginVersion}...");

            // Initialize DI container and EventBus
            Container = new ServiceContainer();
            EventBus = new EventBus();
            Container.Register<IEventBus>(EventBus);
            Container.Register<IServiceContainer>(Container);

            SessionState = new CoopSessionState();
            VitalsSync = new VitalsSyncService();
            DeathCoordinator = new DeathLifecycleCoordinator();
            InventorySync = new InventorySyncService();

            Container.Register(SessionState);
            Container.Register<IVitalsSyncService>(VitalsSync);
            Container.Register(DeathCoordinator);
            Container.Register<IInventorySyncService>(InventorySync);
            Container.Register(this);

            // Initialize BepInEx configuration
            try
            {
                ReviveCooldownConfig = Config.Bind("Gameplay", "ReviveCooldownSeconds", 20.0f, "Revive cooldown for solo death (15-30 seconds)");
                ReviveHealthPercentConfig = Config.Bind("Gameplay", "ReviveHealthPercent", 0.5f, "Fraction of max HP restored on revive (0.1-1.0)");
                ReviveInvulnerabilityConfig = Config.Bind("Gameplay", "ReviveInvulnerabilitySeconds", 2.5f, "Damage immunity after revive (seconds)");
                EnablePlayer2DashTrailConfig = Config.Bind("Visuals", "EnablePlayer2DashTrail", true, "Enable cyan dash trail for Player 2");
                Player2HueShiftConfig = Config.Bind("Visuals", "Player2HueShift", 0.58f, "Hue shift for Player 2 cloak (0.58 = azure blue)");
                DefaultUdpPortConfig = Config.Bind("Network", "DefaultUdpPort", 7777, "Default UDP port for direct connections");
                TickRateHzConfig = Config.Bind("Network", "TickRateHz", 30, "Network replication tick rate (20-60 Hz)");
                AutoReconnectConfig = Config.Bind("Network", "AutoReconnect", true, "Automatically attempt to reconnect after an unexpected disconnect");
                MaxReconnectAttemptsConfig = Config.Bind("Network", "MaxReconnectAttempts", 5, "Maximum number of automatic reconnection attempts (1-10)");
                SaveConnectionHistoryConfig = Config.Bind("Network", "SaveConnectionHistory", true, "Persist the last five connection targets in PlayerPrefs (Windows registry)");

                // Apply config values
                DeathCoordinator.ReviveCooldownDuration = Math.Clamp(ReviveCooldownConfig.Value, 15f, 30f);
                Logger.LogInfo($"[Coop] Config loaded: ReviveCooldown={DeathCoordinator.ReviveCooldownDuration}s, " +
                    $"ReviveHealth={Math.Clamp(ReviveHealthPercentConfig.Value, 0.1f, 1f):P0}, " +
                    $"ReviveInvuln={Math.Clamp(ReviveInvulnerabilityConfig.Value, 0f, 10f):F1}s, " +
                    $"HueShift={Player2HueShiftConfig.Value}");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Failed to load config: {ex.Message}");
            }

            // Initialize settings managers (Step 13)
            Keybinds = new KeybindManager(this);
            QualityPresets = new QualityPresetManager(this);
            // Apply the saved preset to actual settings (tick rate / dash trail). Before
            // this, the preset was read but never applied; Low/Medium/High were cosmetic.
            QualityPresets.ApplyPreset(QualityPresets.CurrentLevel);
            Logger.LogInfo($"[Coop] Settings managers initialized: Quality={QualityPresets.CurrentLevel}");

            // Setup outgoing network callbacks


            // Initialize available transports
            try
            {
                UdpTransport = new LiteNetLibTransport(1, "LocalPlayer");
                ActiveTransport = UdpTransport;
                Container.Register<LiteNetLibTransport>(UdpTransport);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Failed to initialize UDP transport: {ex.Message}");
            }

            try
            {
                SteamTransport = new SteamP2PTransport();
                if (SteamTransport != null)
                    Container.Register<SteamP2PTransport>(SteamTransport);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Steamworks transport not active: {ex.Message}");
            }

            if (UdpTransport != null)
            {
                var transportService = new TransportService(UdpTransport, SteamTransport);
                Container.RegisterSingleton<ITransportService>(_ => transportService);
                var sessionService = new SessionService(EventBus, transportService, this);
                Container.RegisterSingleton<ISessionService>(_ => sessionService);
            }

            // Check for Steam lobby join from command line (user accepted invite while game was closed)
            try
            {
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i] == "-connect_lobby" && ulong.TryParse(args[i + 1], out ulong lobbyId))
                    {
                        _pendingSteamLobbyJoin = lobbyId;
                        Logger.LogInfo($"[Coop] Pending Steam lobby join from command line: {lobbyId}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Failed to parse command line args: {ex.Message}");
            }

            // Bind transport callbacks to session state
            BindTransportEvents(UdpTransport);
            if (SteamTransport != null)
            {
                BindTransportEvents(SteamTransport);
            }

            // Initialize Harmony. Patch one class at a time: PatchAll() aborts on the first
            // signature mismatch, which used to leave every hook after the failing class
            // unpatched while the mod still looked initialized.
            try
            {
                _harmony = new Harmony(PluginGuid);
                int applied = 0;
                int failed = 0;
                foreach (var patchType in typeof(SilksongCoopPlugin).Assembly.GetTypes())
                {
                    if (patchType.GetCustomAttributes(typeof(HarmonyPatch), inherit: true).Length == 0)
                        continue;

                    try
                    {
                        _harmony.PatchAll(patchType);
                        applied++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Logger.LogError($"[Coop] Failed to apply Harmony patches for {patchType.FullName}: {ex.Message}");
                    }
                }
                Logger.LogInfo($"Harmony hooks applied: {applied} class(es), {failed} failed.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error applying Harmony patches: {ex.Message}");
            }

            // Create Clone Spawner
            var spawnerGo = new GameObject("SilksongCoop_CloneSpawner");
            DontDestroyOnLoad(spawnerGo);
            CloneSpawner = spawnerGo.AddComponent<RemoteHornetSpawner>();

            // Enemy/boss/world synchronization controller (HP, breakables, gates, pickups).
            var enemySyncGo = new GameObject("SilksongCoop_EnemySync");
            DontDestroyOnLoad(enemySyncGo);
            EnemySync = enemySyncGo.AddComponent<EnemySyncController>();
            EnemySync.Initialize(this);

            // Create orchestrator services that own the network hot-path and sync callbacks.
            // They subscribe to EventBus events raised by SessionService/TransportService.
            // Created after EnemySync/CloneSpawner so the managers receive live instances.
            if (Container != null)
            {
                try
                {
                    var sessionService = Container.Resolve<ISessionService>();
                    var transportService = Container.Resolve<ITransportService>();
                    _networkOrchestrator = new NetworkOrchestrator(sessionService, transportService, EventBus!, this);
                    _vitalsSyncManager = new VitalsSyncManager(sessionService, transportService, this, VitalsSync, DeathCoordinator, EnemySync);
                    _inventorySyncManager = new InventorySyncManager(sessionService, transportService, this, InventorySync);
                    _deathSyncManager = new DeathSyncManager(sessionService, transportService, this, DeathCoordinator);
                    _desyncDetector = new Core.State.DesyncDetector(EventBus!);
                    EventBus!.Subscribe<Core.State.DesyncDetector.DesyncDetectedEvent>(e =>
                        Logger.LogWarning($"[Coop] Desync detected ({e.Context}): local={e.LocalChecksum} remote={e.RemoteChecksum}"));

                    _orchestrators.Add(_networkOrchestrator);
                    _orchestrators.Add(_vitalsSyncManager);
                    _orchestrators.Add(_inventorySyncManager);
                    _orchestrators.Add(_deathSyncManager);

                    foreach (var orch in _orchestrators) orch.Initialize();

                    Container.Register<NetworkOrchestrator>(_networkOrchestrator);
                    Container.Register<VitalsSyncManager>(_vitalsSyncManager);
                    Container.Register<InventorySyncManager>(_inventorySyncManager);
                    Container.Register<DeathSyncManager>(_deathSyncManager);
                    Container.Register<Core.State.DesyncDetector>(_desyncDetector);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[Coop] Failed to initialize orchestrators: {ex.Message}");
                }
            }

            // Create Menu UI
            var uiGo = new GameObject("SilksongCoop_MenuUI");
            DontDestroyOnLoad(uiGo);
            MenuUI = uiGo.AddComponent<CoopMenuUI>();
            MenuUI.Initialize(SessionState, SteamTransport, UdpTransport, Keybinds, QualityPresets);
            if (Container != null)
            {
                Container.Register(MenuUI);
            }

            Logger.LogInfo("Silksong Co-op initialization complete. Press F10 in game for Co-op menu.");
        }
        #endregion

        #region Connection Management
        /// <summary>
        /// Cancels any pending auto-reconnect attempt.
        /// </summary>
        public void CancelReconnect()
        {
            _reconnectPending = false;
            _reconnectAttempts = 0;
            _reconnectAddress = null;
        }

        /// <summary>
        /// Sets the active network transport (Steam P2P or UDP).
        /// </summary>
        /// <param name="transport">The transport to use for network communication.</param>
        public void SetActiveTransport(INetworkTransport transport)
        {
            if (transport == null)
            {
                Logger.LogWarning("[Coop] SetActiveTransport called with null transport");
                return;
            }

            var oldTransport = ActiveTransport;
            if (ReferenceEquals(oldTransport, transport))
                return;

            // Close the old transport before switching. Some transports raise
            // PeerDisconnected synchronously here; the explicit cleanup below also covers
            // the asynchronous UDP path, whose event is ignored once ActiveTransport has
            // changed. Without this, an old clone and its inventory session could survive
            // into the new session.
            bool switchedService = false;
            if (Container != null && Container.TryResolve(out ITransportService? ts) && ts != null)
            {
                // TransportService closes the previous transport and updates the instance
                // the orchestrators resolve, so both views stay in sync.
                ts.SetActiveTransport(transport);
                switchedService = true;
            }

            if (!switchedService && oldTransport != null)
            {
                try
                {
                    oldTransport.Disconnect();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[Coop] Failed to close previous transport: {ex.Message}");
                }
            }

            PrepareForNewSession();

            ActiveTransport = transport;
            string transportType = transport is SteamP2PTransport ? "Steam" : "UDP";
            Logger.LogInfo($"[Coop] Active transport changed from {(oldTransport == null ? "null" : (oldTransport is SteamP2PTransport ? "Steam" : "UDP"))} to {transportType}");
        }

        /// <summary>
        /// Clears the previous connection's clone, inventory dedup and remote peer state
        /// before starting a new host/join attempt. Reused when switching transports or
        /// reconnecting on the same transport.
        /// </summary>
        public void PrepareForNewSession()
        {
            CloneSpawner?.ResetAllClones();
            InventorySync?.ResetSessionState();
            EnemySync?.ResetSession();
            SessionState?.SetDisconnected(DisconnectReason.GracefulDisconnect);
        }

        /// <summary>
        /// Refreshes local player visuals based on player index (palette swap and dash trail).
        /// </summary>
        /// <param name="isPlayer2">True if this is player 2, false for player 1.</param>
        public void RefreshLocalVisuals(bool isPlayer2)
        {
            try
            {
                var hero = HeroController.instance;
                if (hero == null || hero.gameObject == null) return;

                var palette = hero.gameObject.GetComponent<PaletteSwapController>() ?? hero.gameObject.AddComponent<PaletteSwapController>();
                float hueShift = Player2HueShiftConfig?.Value ?? PaletteSwapMath.Player2HueShift;
                palette.SetPlayer2(isPlayer2, hueShift);
                palette.ApplyPaletteSwap();

                var dash = hero.gameObject.GetComponent<DashTrailController>() ?? hero.gameObject.AddComponent<DashTrailController>();
                dash.Initialize(isPlayer2);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Coop] Failed to refresh local visuals: {ex.Message}\n{ex.StackTrace}");
            }
        }
        #endregion

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

                    if (!_spectatorActive)
                    {
                        _spectatorDisabledColliders.Clear();
                        _spectatorDisabledRenderers.Clear();
                    }

                    // HIGH-01: Disable colliders so invisible dead body doesn't block path or take hits
                    foreach (var col in hero.gameObject.GetComponentsInChildren<Collider2D>())
                    {
                        if (col != null && col.enabled)
                        {
                            col.enabled = false;
                            _spectatorDisabledColliders.Add(col);
                        }
                    }

                    foreach (var ren in hero.gameObject.GetComponentsInChildren<Renderer>())
                    {
                        if (ren != null && ren.enabled)
                        {
                            ren.enabled = false;
                            _spectatorDisabledRenderers.Add(ren);
                        }
                    }

                    _spectatorActive = true;
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

                _spectatorActive = false;
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
            for (int i = 0; i < _spectatorDisabledColliders.Count; i++)
            {
                var col = _spectatorDisabledColliders[i];
                if (col != null) col.enabled = true;
            }
            _spectatorDisabledColliders.Clear();

            for (int i = 0; i < _spectatorDisabledRenderers.Count; i++)
            {
                var ren = _spectatorDisabledRenderers[i];
                if (ren != null) ren.enabled = true;
            }
            _spectatorDisabledRenderers.Clear();
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
                _spectatorActive = false;
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
                if (_spectatorActive)
                {
                    _spectatorActive = false;
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

        #region Network Broadcasting
        private uint _actionTick;

        /// <summary>
        /// Broadcasts a player action trigger to the remote peer.
        /// </summary>
        /// <param name="action">The action type to broadcast.</param>
        /// <param name="parameter">Optional action parameter.</param>
        public void BroadcastActionTrigger(ActionType action, byte parameter = 0)
        {
            try
            {
                if (ActiveTransport == null || SessionState?.IsConnected != true || !SessionState.RemotePeerId.HasValue) return;
                var hero = HeroController.instance;
                Vector2 pos = (hero != null) ? (Vector2)hero.transform.position : Vector2.zero;
                Vector2 dir = (hero != null && hero.cState.facingRight) ? Vector2.right : Vector2.left;

                _actionTick++;
                var packet = new ActionTriggerPacket
                {
                    Header = new PacketHeader(PacketType.ActionTrigger, 0, _actionTick, NetworkClock.NowMs),
                    Action = action,
                    ActionSequenceId = _actionTick,
                    OriginX = pos.x,
                    OriginY = pos.y,
                    DirectionX = dir.x,
                    DirectionY = dir.y,
                    Parameter = parameter
                };

                // P3: heap buffer passed directly (no stackalloc + ToArray copy).
                byte[] buffer = ArrayPool<byte>.Shared.Rent(64);
                try
                {
                    var writer = new PacketWriter(buffer);
                    packet.Serialize(ref writer);
                    ActiveTransport.Send(
                        SessionState.RemotePeerId.Value,
                        buffer,
                        0,
                        writer.Position,
                        DeliveryMethod.ReliableOrdered,
                        NetworkChannels.Action);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Failed broadcasting action trigger: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears retained desync history and forces a fresh sample on the next update.
        /// Round numbers themselves are wall-clock based and therefore shared by both peers.
        /// </summary>
        private void ResetDesyncTracking()
        {
            _desyncRound = null;
            _desyncDetector?.Reset();
        }

        /// <summary>
        /// Broadcasts the local player's bench checkpoint to the remote peer.
        /// </summary>
        /// <param name="sceneName">Name of the scene where the bench is located.</param>
        /// <param name="respawnMarker">Spawn marker identifier for the bench.</param>
        /// <param name="spawnType">Type of spawn point.</param>
        /// <param name="facingRight">True if facing right at the bench.</param>
        public void BroadcastBenchCheckpoint(string sceneName, string respawnMarker, int spawnType, bool facingRight)
        {
            try
            {
                if (ActiveTransport == null || SessionState?.IsConnected != true || !SessionState.RemotePeerId.HasValue) return;

                var packet = new BenchCheckpointSyncPacket
                {
                    Header = new PacketHeader(PacketType.BenchCheckpointSync, 0, 0, NetworkClock.NowMs),
                    SceneName = sceneName,
                    RespawnMarker = respawnMarker,
                    SpawnType = spawnType,
                    FacingRight = facingRight
                };

                // BUG-C1: scene and marker names can exceed 128 bytes — a stack buffer
                // threw in EnsureCapacity and silently aborted the sync (P3: heap buffer
                // passed directly, no extra ToArray copy).
                byte[] buffer = ArrayPool<byte>.Shared.Rent(512);
                try
                {
                    var writer = new PacketWriter(buffer);
                    packet.Serialize(ref writer);
                    ActiveTransport.Send(
                        SessionState.RemotePeerId.Value,
                        buffer,
                        0,
                        writer.Position,
                        DeliveryMethod.ReliableOrdered,
                        NetworkChannels.Death);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Failed broadcasting bench checkpoint: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies a remote bench checkpoint to the local player's data.
        /// </summary>
        /// <param name="sceneName">Name of the scene where the bench is located.</param>
        /// <param name="respawnMarker">Spawn marker identifier for the bench.</param>
        /// <param name="spawnType">Type of spawn point.</param>
        /// <param name="facingRight">True if facing right at the bench.</param>
        public void ApplyRemoteBenchCheckpoint(string sceneName, string respawnMarker, int spawnType, bool facingRight)
        {
            try
            {
                using (RemoteApplyScope.Begin())
                {
                    var pd = PlayerData.instance;
                    if (pd != null)
                    {
                        pd.SetBenchRespawn(respawnMarker, sceneName, spawnType, facingRight);
                        Logger.LogInfo($"[Coop] Applied remote bench checkpoint: {sceneName} / {respawnMarker}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Failed applying remote bench checkpoint: {ex.Message}");
            }
        }

        /// <summary>
        /// Broadcasts collected heart pieces count to the remote peer.
        /// </summary>
        /// <param name="count">Number of heart pieces collected.</param>
        public void BroadcastHeartPieces(int count)
        {
            BroadcastCounterDelta("heartPieces", "heartPiecesInit", count, ref _lastHeartPiecesBroadcast);
        }

        /// <summary>
        /// Sends the connect-time heart-piece baseline. Both peers exchange it; the
        /// receiver merges with <c>Math.Max</c> so neither side regresses.
        /// </summary>
        public void BroadcastHeartPiecesBaseline(int count)
        {
            BroadcastCounterBaseline("heartPiecesInit", count, ref _lastHeartPiecesBroadcast);
        }

        /// <summary>
        /// Broadcasts collected silk parts count to the remote peer.
        /// </summary>
        /// <param name="count">Number of silk parts collected.</param>
        public void BroadcastSilkParts(int count)
        {
            BroadcastCounterDelta("silkParts", "silkPartsInit", count, ref _lastSilkPartsBroadcast);
        }

        /// <summary>
        /// Sends the connect-time silk-part baseline (max-merged on the receiver).
        /// </summary>
        public void BroadcastSilkPartsBaseline(int count)
        {
            BroadcastCounterBaseline("silkPartsInit", count, ref _lastSilkPartsBroadcast);
        }

        private void BroadcastCounterBaseline(string prefix, int count, ref int lastBroadcast)
        {
            try
            {
                lastBroadcast = count;
                if (SessionState?.IsConnected != true) return;
                InventorySync?.OnLocalCounterChanged($"{prefix}:{count}", ItemCategory.KeyItem);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Failed broadcasting counter baseline: {ex.Message}");
            }
        }

        private void BroadcastCounterDelta(string prefix, string initPrefix, int count, ref int lastBroadcast)
        {
            try
            {
                if (SessionState?.IsConnected != true) return;
                if (lastBroadcast < 0)
                {
                    // No baseline for this session yet (e.g. a pickup raced the connect
                    // handshake). Re-anchor on the current value instead of emitting a
                    // bogus delta from a previous session's count.
                    BroadcastCounterBaseline(initPrefix, count, ref lastBroadcast);
                    return;
                }

                int delta = count - lastBroadcast;
                lastBroadcast = count;
                if (delta == 0) return;

                string encodedDelta = delta > 0 ? $"+{delta}" : delta.ToString();
                InventorySync?.OnLocalCounterChanged($"{prefix}:{encodedDelta}", ItemCategory.KeyItem);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Failed broadcasting counter delta: {ex.Message}");
            }
        }

        /// <summary>
        /// Broadcasts the local player's initial inventory state to the connected peer.
        /// The current session's remote peer is the recipient; in a 2-player session there
        /// is exactly one (see BUG-12 — the peer parameter was never used).
        /// </summary>
        public void BroadcastInitialInventory()
        {
            try
            {
                var pd = PlayerData.instance;
                if (pd == null || InventorySync == null) return;

                // 1. Shared ability bools (validated against the game's PlayerData)
                foreach (var boolName in Hooks.InventoryHooks.GetValidatedAbilityBools())
                {
                    if (pd.GetBool(boolName))
                    {
                        InventorySync.OnLocalItemDiscovered(boolName, ItemCategory.Ability);
                    }
                }

                // 2. Unlocked tools
                try
                {
                    var unlocked = ToolItemManager.GetUnlockedTools();
                    if (unlocked != null)
                    {
                        foreach (var tool in unlocked)
                        {
                            if (tool != null && !string.IsNullOrEmpty(tool.name))
                            {
                                InventorySync.OnLocalItemDiscovered(tool.name, ItemCategory.Tool);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[Coop] Tool enumeration fallback: {ex.Message}");
                }

                // 3. Current crest
                if (!string.IsNullOrEmpty(pd.CurrentCrestID))
                {
                    InventorySync.OnLocalItemDiscovered(pd.CurrentCrestID, ItemCategory.Crest);
                }

                // 4. Heart pieces & silk parts (BUG-B5: counters bypass the unlock dedup —
                // the same value recurs in every upgrade cycle). Send a baseline, not a
                // delta: both sides max-merge so neither partial count is overwritten.
                BroadcastHeartPiecesBaseline(pd.heartPieces);
                BroadcastSilkPartsBaseline(pd.silkParts);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Failed broadcasting initial inventory: {ex.Message}");
            }
        }
        #endregion

        #region Inventory and Item Sync
        /// <summary>
        /// Applies a remote item unlock to the local player's inventory.
        /// </summary>
        /// <param name="itemId">The identifier of the item to unlock.</param>
        /// <param name="category">The category of the item.</param>
        public void ApplyRemoteItemUnlock(string itemId, ItemCategory category)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                Logger.LogWarning("[Coop] ApplyRemoteItemUnlock called with null or empty itemId");
                return;
            }

            try
            {
                using (RemoteApplyScope.Begin())
                {
                    var playerData = PlayerData.instance;
                    if (playerData == null)
                    {
                        Logger.LogWarning("[Coop] Cannot apply remote item unlock: PlayerData.instance is null");
                        return;
                    }

                // MED-02: Partial health and silk fragment synchronization.
                // Counters are shared progress: the connect-time baseline is max-merged,
                // and subsequent packets carry a signed delta. Absolute overwrites made
                // the two players' partial counts swap with each other on connect (#2.3).
                if (itemId.StartsWith("heartPiecesInit:"))
                {
                    if (int.TryParse(itemId.Substring("heartPiecesInit:".Length), out int baseline))
                    {
                        // Persistent state is 0..3 (4 is transiently visible before the
                        // game resets it). Cap so a baseline can never award a second mask.
                        int merged = Math.Max(playerData.heartPieces, Math.Clamp(baseline, 0, 3));
                        playerData.heartPieces = merged;
                    }
                    return;
                }
                if (itemId.StartsWith("silkPartsInit:"))
                {
                    if (int.TryParse(itemId.Substring("silkPartsInit:".Length), out int baseline))
                    {
                        int merged = Math.Max(playerData.silkParts, Math.Clamp(baseline, 0, 2));
                        playerData.silkParts = merged;
                    }
                    return;
                }
                if (itemId.StartsWith("heartPieces:"))
                {
                    if (int.TryParse(itemId.Substring("heartPieces:".Length), out int value))
                    {
                        bool isDelta = itemId["heartPieces:".Length] == '+' || itemId["heartPieces:".Length] == '-';
                        // A peer may only report the small deltas the game produces (±1 per
                        // pickup, at most one mask's worth per packet). Without this bound a
                        // modified client could ask for millions of AddToMaxHealth calls in a
                        // single frame and freeze the host.
                        if (isDelta && (value < -8 || value > 8))
                        {
                            Logger.LogWarning($"[Coop] Rejected heart-piece delta {value}");
                            return;
                        }

                        int next = isDelta
                            ? playerData.heartPieces + value
                            : Math.Max(playerData.heartPieces, Math.Clamp(value, 0, 3));

                        // 4 pieces = 1 mask. Bound the work: at most two masks can be
                        // completed by a single valid packet.
                        next = Math.Clamp(next, 0, 11);
                        int masks = next / 4;
                        next %= 4;
                        for (int i = 0; i < masks; i++)
                        {
                            HeroController.instance?.AddToMaxHealth(1);
                        }
                        playerData.heartPieces = next;
                    }
                    return;
                }
                if (itemId.StartsWith("silkParts:"))
                {
                    if (int.TryParse(itemId.Substring("silkParts:".Length), out int value))
                    {
                        bool isDelta = itemId["silkParts:".Length] == '+' || itemId["silkParts:".Length] == '-';
                        if (isDelta && (value < -6 || value > 6))
                        {
                            Logger.LogWarning($"[Coop] Rejected silk-part delta {value}");
                            return;
                        }

                        int next = isDelta
                            ? playerData.silkParts + value
                            : Math.Max(playerData.silkParts, Math.Clamp(value, 0, 2));

                        // 3 parts = 1 spool.
                        next = Math.Clamp(next, 0, 8);
                        int spools = next / 3;
                        next %= 3;
                        for (int i = 0; i < spools; i++)
                        {
                            HeroController.instance?.AddToMaxSilk(1);
                        }
                        playerData.silkParts = next;
                    }
                    return;
                }

                switch (category)
                {
                    case ItemCategory.Crest:
                        try
                        {
                            var localCrest = playerData.CurrentCrestID;
                            var crest = ToolItemManager.GetCrestByName(itemId);
                            
                            if (crest != null)
                            {
                                crest.Unlock();
                                if (playerData.CurrentCrestID != localCrest)
                                {
                                    ToolItemManager.SetEquippedCrest(localCrest);
                                }
                            }
                            else if (playerData.ToolEquips != null)
                            {
                                var data = playerData.ToolEquips.GetData(itemId);
                                data.IsUnlocked = true;
                                playerData.ToolEquips.SetData(itemId, data);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError($"[Coop] Failed to apply crest unlock '{itemId}': {ex.Message}");
                        }
                        break;

                    case ItemCategory.Tool:
                        try
                        {
                            var tool = ToolItemManager.GetToolByName(itemId);
                            if (tool != null)
                            {
                                tool.Unlock(null, ToolItem.PopupFlags.None);
                            }
                            else
                            {
                                var data = playerData.GetToolData(itemId);
                                data.IsUnlocked = true;
                                playerData.SetToolData(itemId, data);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError($"[Coop] Failed to apply tool unlock '{itemId}': {ex.Message}");
                        }
                        break;

                    case ItemCategory.Ability:
                    case ItemCategory.KeyItem:
                        try
                        {
                            var toolSkill = ToolItemManager.GetToolByName(itemId);
                            if (toolSkill != null)
                            {
                                toolSkill.Unlock(null, ToolItem.PopupFlags.None);
                            }
                            else
                            {
                                playerData.SetBool(itemId, true);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError($"[Coop] Failed to apply ability unlock '{itemId}': {ex.Message}");
                        }
                        break;
                }
            }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Coop] Failed applying remote item unlock for {itemId} ({category}): {ex.Message}");
            }
        }
        #endregion

        #region Transport Event Wiring
        private void BindTransportEvents(INetworkTransport? transport)
        {
            if (transport == null) return;

            transport.OnPeerConnected += peer =>
            {
                try
                {
                    // BUG-07: only the active transport may drive session state — the other
                    // transport is still polled for callbacks and can emit stale events
                    // (e.g. a leftover UDP peer or an old Steam lobby).
                    if (!ReferenceEquals(transport, ActiveTransport)) return;

                    // Reset auto-reconnect state on successful connection
                    _reconnectPending = false;
                    _reconnectAttempts = 0;
                    _reconnectAddress = null;

                    // Issue #2.2: never inherit a wipe/spectator latch from the previous
                    // session. If the old session ended mid-wipe, this flag stayed set until
                    // the game was restarted and every later team wipe silently no-oped.
                    Interlocked.Exchange(ref _teamWipeInProgress, 0);
                    // Clearing the flag without restoring the hero would leave it with
                    // gravity/colliders/rendering disabled if a reconnect raced the bench
                    // reload. ExitSpectatorMode performs the actual restore.
                    if (_spectatorActive) ExitSpectatorMode();
                    _lastHeartPiecesBroadcast = -1;
                    _lastSilkPartsBroadcast = -1;
                    EnemySync?.ResetSession();
                    InventorySync?.ResetSessionState();

                    Logger.LogInfo($"[Coop] Peer connected: {peer}");
                    SessionState?.SetConnected(peer, transport.IsHost ? SessionRole.Host : SessionRole.Client);
                    ResetDesyncTracking();
                    EventBus?.Publish(new PeerConnectedEvent { PeerId = peer });
                    MenuUI?.Diagnostics?.OnSessionStarted();
                    MenuUI?.SetStatusMessage($"Connected to {peer.DisplayName}");

                    if (CloneSpawner == null)
                    {
                        Logger.LogWarning("[Coop] CloneSpawner is null, cannot spawn remote player");
                        return;
                    }

                    bool remoteIsPlayer2 = transport.IsHost;
                    CloneSpawner.ResetAllClones();
                    CloneSpawner.SpawnRemotePlayer(peer, remoteIsPlayer2);

                    RefreshLocalVisuals(!transport.IsHost);

                    // BUG-A2: BOTH sides must initialize and broadcast — previously only the
                    // host did, so the host never received the client's vitals, currency or
                    // inventory and its HUD stayed empty with 0-level remote damage.
                    // BUG-A4: also resync the death epoch at session start.
                    DeathCoordinator?.SynchronizeEpochForNewSession();

                    if (VitalsSync != null)
                    {
                        var pd = PlayerData.instance;
                        if (pd != null)
                        {
                            VitalsSync.InitializeState(
                                pd.health,
                                pd.maxHealthBase,
                                pd.silk,
                                Math.Max(1, pd.silkMax / 9),
                                pd.nailUpgrades,
                                pd.geo,
                                pd.ShellShards
                            );
                        }
                        VitalsSync.BroadcastCurrentVitals();
                        VitalsSync.BroadcastCurrentCurrency();
                        // Tell the newly connected peer which world drops were already
                        // collected, so its local copies are disabled before it can pick them.
                        VitalsSync.BroadcastClaimedPickups();

                        // HIGH-02: Broadcast initial inventory dump (abilities, tools, crests) to client
                        BroadcastInitialInventory();
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[Coop] Exception in OnPeerConnected handler: {ex.Message}\n{ex.StackTrace}");
                }
            };

            transport.OnPeerDisconnected += (peer, reason) =>
            {
                try
                {
                    // BUG-07: ignore disconnect events from a transport that is not active —
                    // otherwise a stale UDP/Steam event would tear down a live session.
                    if (!ReferenceEquals(transport, ActiveTransport)) return;

                    Logger.LogInfo($"[Coop] Peer disconnected: {peer}, reason: {reason}");

                    // SetDisconnected() clears CurrentRoomCode and SteamLobbyId, so the
                    // reconnect target must be captured before it runs. A host has
                    // nobody to dial back, so it just keeps listening.
                    bool wasHost = SessionState?.Role == SessionRole.Host;
                    string? reconnectTarget = wasHost
                        ? null
                        : (transport == SteamTransport
                            ? SessionState?.SteamLobbyId?.ToString()
                            : SessionState?.CurrentRoomCode);
                    string? hostRoomCode = wasHost ? SessionState?.CurrentRoomCode : null;

                    // CRIT-02: If partner disconnected while local player was spectating/pending revive,
                    // the local player must fall back to a bench respawn to avoid a perma-softlock.
                    // The session is torn down FIRST: while still "connected", TriggerTeamWipe's
                    // PlayerDead coroutine re-enters DeathHooks.PrefixPlayerDead, which sees a
                    // Spectating coordinator and suppresses the bench reload (entering spectator
                    // mode again) — leaving the hero without controls/colliders. After
                    // SetDisconnected the prefix takes the normal "not connected" path.
                    bool recoveringFromSpectator =
                        DeathCoordinator?.CurrentState == DeathState.Spectating ||
                        DeathCoordinator?.CurrentState == DeathState.RespawningAtPartner;

                    SessionState?.SetDisconnected(reason);
                    ResetDesyncTracking();
                    EventBus?.Publish(new PeerDisconnectedEvent { PeerId = peer, Reason = reason });
                    EventBus?.Publish(new SessionEndedEvent { Reason = reason });
                    MenuUI?.Diagnostics?.OnSessionEnded();
                    CloneSpawner?.DespawnRemotePlayer(peer);
                    RefreshLocalVisuals(false);

                    // New peer = new inventory session. Clear the once-per-item dedup on
                    // disconnect (before any packet of the next session can arrive) so the
                    // next BroadcastInitialInventory is not swallowed.
                    InventorySync?.ResetSessionState();
                    EnemySync?.ResetSession();

                    if (recoveringFromSpectator)
                    {
                        Logger.LogWarning("[Coop] Partner disconnected while in spectator mode. Exiting spectator and respawning at bench.");
                        ExitSpectatorMode();
                        TriggerTeamWipe();
                    }

                    // A UDP host keeps its socket bound after a peer timeout (so the guest
                    // can reconnect). Do not leave the UI in "Disconnected" with the room
                    // code hidden: the host is still listening and must be able to share the
                    // same code again. A deliberate Disconnect() reports GracefulDisconnect
                    // and must not re-enter the hosting state.
                    if (wasHost && (reason == DisconnectReason.Timeout ||
                                    reason == DisconnectReason.PeerDisconnected))
                    {
                        SessionState?.SetHosting(transport.LocalPeerId, hostRoomCode ?? string.Empty);
                        MenuUI?.SetStatusMessage(reason == DisconnectReason.Timeout
                            ? "Partner timed out — still hosting. Share the same room code to reconnect."
                            : "Partner left — still hosting. Share the same room code to reconnect.");
                    }
                    else if (wasHost && reason == DisconnectReason.VersionMismatch)
                    {
                        // Don't keep hosting on version mismatch; user needs to update
                        SessionState?.SetDisconnected();
                        MenuUI?.SetStatusMessage("Version mismatch — update both players to the same version.");
                    }

                    // Schedule auto-reconnect on unexpected disconnect
                    bool autoReconnect = AutoReconnectConfig?.Value ?? true;
                    int maxAttempts = Math.Clamp(MaxReconnectAttemptsConfig?.Value ?? 5, 1, 10);
                    if (autoReconnect && !wasHost && reason != DisconnectReason.GracefulDisconnect && reason != DisconnectReason.PeerDisconnected && reason != DisconnectReason.None && reason != DisconnectReason.VersionMismatch)
                    {
                        if (_reconnectAttempts < maxAttempts)
                        {
                            _reconnectIsSteam = (transport == SteamTransport);
                            _reconnectAddress = reconnectTarget;

                            if (!string.IsNullOrEmpty(_reconnectAddress))
                            {
                                _reconnectAttempts++;
                                float backoff = Math.Min((float)Math.Pow(2, _reconnectAttempts - 1), 16f);
                                _nextReconnectTime = Time.unscaledTime + backoff;
                                _reconnectPending = true;
                                Logger.LogInfo($"[Coop] Auto-reconnect scheduled in {backoff}s (attempt {_reconnectAttempts}/{maxAttempts})");
                                MenuUI?.SetStatusMessage($"Disconnected. Reconnecting in {backoff}s...");
                            }
                        }
                        else
                        {
                            Logger.LogWarning($"[Coop] Max reconnect attempts ({maxAttempts}) reached. Giving up.");
                            MenuUI?.SetStatusMessage("Connection lost. Max reconnect attempts reached.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[Coop] Exception in OnPeerDisconnected handler: {ex.Message}\n{ex.StackTrace}");
                }
            };

            transport.OnDataReceived += (peer, data, offset, length) =>
            {
                try
                {
                    // BUG-07: ignore data from a transport that is not active — a stale peer
                    // on the other transport must not inject packets into the live session.
                    if (!ReferenceEquals(transport, ActiveTransport)) return;

                    SessionState?.RecordReceived(length);
                    MenuUI?.Diagnostics?.RecordReceived(length);

                    ProcessPacket(peer, new ReadOnlySpan<byte>(data, offset, length));
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[Coop] Error processing packet from {peer}: {ex.Message}");
                }
            };
        }

        /// <summary>
        /// Dispatches one framed packet.
        /// </summary>
        private void ProcessPacket(NetworkPeerId peer, ReadOnlySpan<byte> span)
        {
            try
            {
                // Desync checksums use their own magic, so they are recognised before the
                // regular header parser and never reach it as "garbage".
                if (DesyncChecksumFrame.TryRead(span, out uint round, out uint checksum))
                {
                    _desyncDetector?.OnRemoteChecksumReceived(checksum, round, Time.unscaledTime);
                    return;
                }

                if (span.Length < PacketHeader.HeaderSize) return;

                if (!PacketHeader.TryReadFrom(span, out var header))
                    return;

                if (!header.IsValidPacketType)
                    return;

                var reader = new PacketReader(span);
                switch (header.PacketId)
                {
                    case PacketType.TransformSync:
                        var trans = TransformSyncPacket.Deserialize(ref reader);
                        if (!PacketValidator.ValidateTransformSync(trans)) return;
                        int localSceneHash = GameManager.instance != null ? GameManager.instance.sceneNameHash : 0;
                        // BUG-B6: strict equality — while a scene is loading the local hash
                        // is 0, and the old "either side 0 counts as same scene" rule made
                        // the clone pop in and flicker on the black loading screen. Hide
                        // until both hashes are known and equal.
                        bool isSameScene = trans.SceneNameHash != 0 && trans.SceneNameHash == localSceneHash;
                        CloneSpawner?.UpdateRemoteTransform(
                            peer,
                            trans.PositionX, trans.PositionY,
                            trans.VelocityX, trans.VelocityY,
                            trans.FacingDirection,
                            trans.StateFlags,
                            trans.AnimationClipHash,
                            trans.NormalizedTime,
                            header.Timestamp,
                            isSameScene,
                            header.TickSequence);
                        break;

                    case PacketType.BenchCheckpointSync:
                        var bench = BenchCheckpointSyncPacket.Deserialize(ref reader);
                        if (!PacketValidator.ValidateBenchCheckpoint(bench)) return;
                        ApplyRemoteBenchCheckpoint(bench.SceneName, bench.RespawnMarker, bench.SpawnType, bench.FacingRight);
                        break;

                    case PacketType.ActionTrigger:
                        var action = ActionTriggerPacket.Deserialize(ref reader);
                        if (!PacketValidator.ValidateActionTrigger(action)) return;
                        // A hidden clone still sits at its last position in this scene.
                        // Its attack must not damage enemies in a room the partner has
                        // already left (issue #2.4).
                        if (CloneSpawner == null || !CloneSpawner.IsRemoteInSameScene(peer))
                        {
                            break;
                        }
                        CloneSpawner.TriggerRemoteAction(peer, action.Action, (byte)action.Parameter, action.OriginX, action.OriginY);
                        break;

                    case PacketType.VitalsSync:
                        var vitals = VitalsSyncPacket.Deserialize(ref reader);
                        if (!PacketValidator.ValidateVitalsSync(vitals)) return;
                        VitalsSync?.HandleVitalsPacket(peer, vitals);
                        break;

                    case PacketType.CurrencySync:
                        var currency = CurrencySyncPacket.Deserialize(ref reader);
                        if (!PacketValidator.ValidateCurrencySync(currency)) return;
                        VitalsSync?.HandleCurrencyPacket(peer, currency);
                        break;

                    case PacketType.PlayerDeath:
                        if (SessionState?.RemotePeerId == null || SessionState.RemotePeerId.Value != peer) return;
                        var death = DeathLifecyclePacket.Deserialize(ref reader);
                        if (!PacketValidator.ValidateDeathLifecycle(death)) return;
                        if (DeathCoordinator != null)
                        {
                            var before = DeathCoordinator.IsPartnerAlive;
                            DeathCoordinator.HandleDeathPacket(peer, death);
                            // Only apply visibility if the coordinator state actually changed
                            if (DeathCoordinator.IsPartnerAlive != before)
                            {
                                ApplyRemoteDeathVisibility(peer, death.EventType);
                            }
                            else if (death.EventType == DeathEventType.PartnerReviveTrigger && DeathCoordinator.IsPartnerAlive)
                            {
                                ApplyRemoteDeathVisibility(peer, death.EventType);
                            }
                            else if ((death.EventType == DeathEventType.SoloDeathOccurred || death.EventType == DeathEventType.TeamWipeOccurred) && !DeathCoordinator.IsPartnerAlive)
                            {
                                ApplyRemoteDeathVisibility(peer, death.EventType);
                            }
                        }
                        else
                        {
                            ApplyRemoteDeathVisibility(peer, death.EventType);
                        }
                        break;

                    case PacketType.SceneTransition:
                        // BUG-20: explicit "partner left the scene" signal — hide the clone
                        // immediately and drop buffered snapshots so it never lingers at
                        // its last position during the remote's scene load.
                        var scene = SceneTransitionPacket.Deserialize(ref reader);
                        if (!PacketValidator.ValidateSceneTransition(scene)) return;
                        if (scene.TransitionPhase == 0)
                        {
                            CloneSpawner?.SetRemoteScenePresence(peer, false);
                        }
                        else
                        {
                            CloneSpawner?.SetRemoteScenePresence(peer, true);
                        }
                        break;

                    case PacketType.EnemySync:
                        var enemyPacket = EnemySyncPacket.Deserialize(ref reader);
                        if (!PacketValidator.ValidateEnemySync(enemyPacket)) return;
                        EnemySync?.ApplyRemoteHit(enemyPacket);
                        break;

                    case PacketType.EnemySnapshot:
                        var enemySnapshot = EnemySnapshotPacket.Deserialize(ref reader);
                        if (!PacketValidator.ValidateEnemySnapshot(enemySnapshot)) return;
                        EnemySync?.ApplyRemoteSnapshot(enemySnapshot);
                        break;

                    case PacketType.WorldEvent:
                        var worldEvent = WorldEventPacket.Deserialize(ref reader);
                        if (!PacketValidator.ValidateWorldEvent(worldEvent)) return;
                        EnemySync?.ApplyRemoteWorldEvent(worldEvent);
                        break;

                    case PacketType.PersistentState:
                        var persistentState = PersistentStatePacket.Deserialize(ref reader);
                        if (!PacketValidator.ValidatePersistentState(persistentState)) return;
                        EnemySync?.ApplyRemotePersistentState(persistentState);
                        break;

                    case PacketType.ItemUnlockSync:
                        var item = ItemUnlockPacket.Deserialize(ref reader);
                        if (!PacketValidator.ValidateItemUnlock(item)) return;
                        InventorySync?.HandleItemUnlockPacket(peer, item);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Error processing packet from {peer}: {ex.Message}");
            }
        }

        #endregion

        #region Frame Update and Lifecycle
        /// <summary>
        /// BUG-20: sends an explicit "scene transition started" signal so the remote peer
        /// hides our clone immediately, instead of waiting for the next transform tick
        /// (up to ~50 ms) or for transforms to stop during the scene load. The remote clone
        /// reappears automatically when the arriving peer's transform packets carry a
        /// matching scene hash again.
        /// </summary>
        public void BroadcastSceneTransitionDeparture()
        {
            try
            {
                if (ActiveTransport == null || SessionState?.IsConnected != true || !SessionState.RemotePeerId.HasValue) return;

                // World drops from the scene we are leaving must not be replayed later.
                VitalsSync?.ClearClaimedPickups();

                int sceneHash = GameManager.instance != null ? GameManager.instance.sceneNameHash : 0;
                var packet = new SceneTransitionPacket
                {
                    Header = new PacketHeader(PacketType.SceneTransition, 0, 0, NetworkClock.NowMs),
                    TargetScene = string.Empty,
                    EntryGate = string.Empty,
                    SceneNameHash = sceneHash,
                    TransitionPhase = 0 // 0 = departure (scene left), 1 = arrival
                };

                // P3: heap buffer passed directly (see BroadcastLocalTransform).
                byte[] buffer = ArrayPool<byte>.Shared.Rent(64);
                try
                {
                    var writer = new PacketWriter(buffer);
                    packet.Serialize(ref writer);
                    ActiveTransport.Send(
                        SessionState.RemotePeerId.Value,
                        buffer,
                        0,
                        writer.Position,
                        DeliveryMethod.ReliableOrdered,
                        NetworkChannels.Action);
                    SessionState?.RecordSent(writer.Position);
                    MenuUI?.Diagnostics?.RecordSent(writer.Position);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Failed broadcasting scene transition: {ex.Message}");
            }
        }

        /// <summary>
        /// BUG-N8: the partner's clone used to stay frozen and visible at the death spot
        /// forever, because a dead player stops streaming transforms but nothing hid the
        /// clone. Death packets now toggle its visibility: revived players reappear with
        /// their next transform packet, so a lost "revive" signal self-heals.
        /// </summary>
        private void ApplyRemoteDeathVisibility(NetworkPeerId peer, DeathEventType eventType)
        {
            try
            {
                switch (eventType)
                {
                    case DeathEventType.SoloDeathOccurred:
                    case DeathEventType.TeamWipeOccurred:
                        CloneSpawner?.SetRemotePartnerAlive(peer, false);
                        break;
                    case DeathEventType.PartnerReviveTrigger:
                    case DeathEventType.SoloBenchRespawn:
                        CloneSpawner?.SetRemotePartnerAlive(peer, true);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Failed to update remote clone visibility: {ex.Message}");
            }
        }


        private void Update()
        {
            try
            {
                // Always poll Steam transport for callbacks (lobby invites, session requests, etc.)
                // even when UDP is the active data transport.
                if (SteamTransport != null && ActiveTransport != SteamTransport)
                {
                    SteamTransport.PollEvents();
                }
                ActiveTransport?.PollEvents();

                _networkOrchestrator?.Update();
                foreach (var orch in _orchestrators)
                {
                    if (orch != _networkOrchestrator)
                        orch.Update();
                }

                UpdateDesyncDetector();

                if (Keybinds != null && Keybinds.IsKeyDown(KeybindManager.ToggleSpectatorCamera))
                {
                    _spectatorCameraFollow = !_spectatorCameraFollow;
                    MenuUI?.SetStatusMessage(_spectatorCameraFollow
                        ? "Spectator camera: follow partner"
                        : "Spectator camera: hold position");
                }

                // Auto-reconnect attempt
                if (_reconnectPending && Time.unscaledTime >= _nextReconnectTime)
                {
                    _reconnectPending = false;
                    string? addr = _reconnectAddress;
                    bool isSteam = _reconnectIsSteam;
                    if (!string.IsNullOrEmpty(addr))
                    {
                        try
                        {
                            INetworkTransport? transport = isSteam ? (INetworkTransport?)SteamTransport : UdpTransport;
                            if (transport != null)
                            {
                                SetActiveTransport(transport);
                                transport.Connect(addr);
                                SessionState?.SetConnecting(transport.LocalPeerId, addr);
                                MenuUI?.SetStatusMessage($"Reconnecting (attempt {_reconnectAttempts})...");
                                Logger.LogInfo($"[Coop] Auto-reconnect attempt {_reconnectAttempts} to {addr}");
                            }
                            else
                            {
                                _reconnectPending = true;
                                _nextReconnectTime = Time.unscaledTime + 4f;
                                Logger.LogWarning($"[Coop] Auto-reconnect deferred: transport for {addr} is unavailable");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError($"[Coop] Auto-reconnect failed: {ex.Message}");

                            // Count failed attempts too: a permanently invalid address must
                            // not retry every 4 seconds forever.
                            _reconnectAttempts++;
                            int maxAttempts = Math.Clamp(MaxReconnectAttemptsConfig?.Value ?? 5, 1, 10);
                            if (_reconnectAttempts < maxAttempts)
                            {
                                _reconnectPending = true;
                                _nextReconnectTime = Time.unscaledTime + 4f;
                            }
                            else
                            {
                                _reconnectPending = false;
                                MenuUI?.SetStatusMessage("Connection lost. Max reconnect attempts reached.");
                            }
                        }
                    }
                }

                // Process pending Steam lobby join from command line
                if (_pendingSteamLobbyJoin.HasValue && SteamTransport != null)
                {
                    try
                    {
                        SetActiveTransport(SteamTransport);
                        PrepareForNewSession();
                        SteamTransport.Connect(_pendingSteamLobbyJoin.Value.ToString());
                        SessionState?.SetConnecting(SteamTransport.LocalPeerId, _pendingSteamLobbyJoin.Value.ToString());
                        MenuUI?.SetStatusMessage($"Joining Steam Lobby {_pendingSteamLobbyJoin.Value}...");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"[Coop] Failed to join pending Steam lobby: {ex.Message}");
                    }
                    finally
                    {
                        _pendingSteamLobbyJoin = null;
                    }
                }

                SessionState?.AdvanceTick();

                if (SessionState?.IsConnected == true && SessionState.RemotePeerId.HasValue)
                {
                    if (CloneSpawner != null && CloneSpawner.TryGetRemotePosition(SessionState.RemotePeerId.Value, out float posX, out float posY))
                    {
                        if (DeathCoordinator != null)
                        {
                            DeathCoordinator.LastPartnerPosition = (posX, posY);

                            // If spectating, tether camera target to remote partner position
                            if (DeathCoordinator.CurrentState == DeathState.Spectating && _spectatorCameraFollow)
                            {
                                var camTarget = GameManager.instance?.cameraCtrl?.camTarget;
                                if (camTarget != null)
                                {
                                    var curPos = camTarget.transform.position;
                                    camTarget.transform.position = new Vector3(posX, posY, curPos.z);
                                }
                            }
                        }
                    }
                }

                DeathCoordinator?.UpdateTick(Time.unscaledDeltaTime);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Coop] Exception in Update: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Samples the shared state once per desync round and compares it with the peer.
        /// The round number is derived from wall-clock time, so both peers label the same
        /// time bucket even if one missed an interval or runs at a different frame rate.
        /// </summary>
        private void UpdateDesyncDetector()
        {
            if (_desyncDetector == null) return;

            // While the partner is in a different scene, dead/spectating, or the clone does
            // not exist yet, the shared-progress view is legitimately different (the scene
            // name itself is part of the checksum) and would report a desync every cooldown.
            // Detection resumes on the next round after the partner is visible again.
            if (SessionState == null || !SessionState.RemotePeerId.HasValue) return;
            var remotePeer = SessionState.RemotePeerId.Value;
            if (CloneSpawner == null || !CloneSpawner.IsRemoteInSameScene(remotePeer)) return;

            int intervalMs = (int)Math.Round(Math.Max(1f, _desyncDetector.CheckIntervalSeconds) * 1000.0);
            uint round = Core.State.DesyncRoundSchedule.ComputeRound(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                intervalMs);
            if (_desyncRound == round) return;
            _desyncRound = round;

            float now = Time.unscaledTime;

            var pd = PlayerData.instance;
            string scene = GameManager.instance != null ? GameManager.instance.sceneName : "unknown";

            // Only shared state may be part of the checksum: per-player health and position
            // legitimately differ between peers and would report a desync on every tick.
            int sharedRosaries = VitalsSync?.SharedRosaries ?? 0;
            int sharedShards = VitalsSync?.SharedShellShards ?? 0;
            string signature = $"{scene}|{pd?.maxHealth ?? 0}|{pd?.nailUpgrades ?? 0}|{pd?.silkMax ?? 0}|{sharedRosaries}|{sharedShards}";
            uint checksum = _desyncDetector.ComputeChecksumValue(signature);

            _desyncDetector.Update(now, checksum, round);

            if (ActiveTransport == null || SessionState?.IsConnected != true || !SessionState.RemotePeerId.HasValue)
                return;

            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(DesyncChecksumFrame.FrameSize);
            try
            {
                DesyncChecksumFrame.Write(
                    new Span<byte>(buffer, 0, DesyncChecksumFrame.FrameSize),
                    round,
                    checksum);

                ActiveTransport.Send(
                    SessionState.RemotePeerId.Value,
                    buffer,
                    0,
                    DesyncChecksumFrame.FrameSize,
                    DeliveryMethod.Unreliable,
                    NetworkChannels.Desync);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Failed to send desync checksum: {ex.Message}");
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        private void OnDestroy()
        {
            try
            {
                StopAllCoroutines();
                // BUG-C3: disconnect BOTH transports — previously only the active one was
                // disconnected, so a Steam session leaked the UDP socket and held port
                // 7777 until the game process exited.
                UdpTransport?.Disconnect();
                SteamTransport?.Disconnect();
                ActiveTransport?.Disconnect();
                CancelReconnect();
                
                _harmony?.UnpatchSelf();

                // Release static GUI resources (textures are HideFlags.DontSave and are not
                // collected with the scene).
                SilksongSkin.Release();
                DiagnosticsOverlay.Release();
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Coop] Exception in OnDestroy: {ex.Message}");
            }
            finally
            {
                CoopLog.Sink = null;
                Instance = null;
            }
        }
        #endregion
    }
}
