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
using BepInEx.Configuration;
using HarmonyLib;
using SilksongCoop.Core;
using SilksongCoop.Core.Clones;
using SilksongCoop.Core.DI;
using SilksongCoop.Core.Events;
using SilksongCoop.Core.Localization;
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
    public partial class SilksongCoopPlugin : BaseUnityPlugin, IGameplayActions, ICoopLogger
    {
        #region Plugin Constants and Static Members
        public const string PluginGuid = "com.silksong.coop";
        public const string PluginName = "Silksong Co-op Multiplayer";
        public const string PluginVersion = "3.3.9";

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
        public static void LogError(string message) => Instance?.Logger.LogError(message);

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
        public BepInEx.Configuration.ConfigEntry<int>? SteamMaxPlayersConfig;
        public BepInEx.Configuration.ConfigEntry<bool>? SaveConnectionHistoryConfig;
        public BepInEx.Configuration.ConfigEntry<bool>? SyncQuestFlagsConfig;
        public BepInEx.Configuration.ConfigEntry<string>? LanguageConfig;
        public BepInEx.Configuration.ConfigEntry<bool>? OnboardingCompletedConfig;
        #endregion

        private readonly Dictionary<NetworkPeerId, RemoteSceneSequence> _remoteScenes = new();
        private readonly Dictionary<NetworkPeerId, bool> _walletPeerBootstrapCompleted = new();

        internal bool CanTrackCurrency => SessionState?.IsConnected == true ||
            (HandshakeCoordinator?.WalletSave != null && ReferenceEquals(HandshakeCoordinator.WalletSave, PlayerData.instance) &&
             (_reconnectPending || (SessionState?.CurrentRoomCode != null &&
                                    SessionState.CurrentRoomCode == HandshakeCoordinator.WalletRoom)));
        private uint _sceneGeneration;
        private int? _departedSceneHandle;

        private bool IsSaveReady => PlayerData.instance != null &&
                                    GameManager.instance?.IsGameplayScene() == true &&
                                    UnityEngine.SceneManagement.SceneManager.GetActiveScene().isLoaded &&
                                    _departedSceneHandle != UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;

        internal bool CanSendSceneTransform => IsSaveReady;

        internal uint SceneGeneration => _sceneGeneration;

        public INetworkTransport? ActiveTransport { get; private set; }
        public SteamP2PTransport? SteamTransport { get; private set; }
        public LiteNetLibTransport? UdpTransport { get; private set; }
        public CoopMenuUI MenuUI { get; private set; } = null!;
        /// <summary>Current adaptive transform tick rate, or 0 before initialization.</summary>
        public float CurrentTickHz => _networkOrchestrator?.CurrentTickHz ?? 0f;
        public RemoteHornetSpawner CloneSpawner { get; private set; } = null!;
        public EnemySyncController? EnemySync { get; private set; }
        public QuestStateSyncManager? QuestStateSync { get; private set; }

        public Coordinators.ReviveLifecycleCoordinator ReviveCoordinator { get; private set; } = null!;
        public Coordinators.InventoryApplicationCoordinator InventoryCoordinator { get; private set; } = null!;
        public Coordinators.PeerHandshakeCoordinator HandshakeCoordinator { get; private set; } = null!;
        public Coordinators.IncomingPacketRouter PacketRouter { get; private set; } = null!;

        // Orchestrator services that decouple network hot-path from the plugin.
        private readonly System.Collections.Generic.List<ISyncOrchestrator> _orchestrators = new System.Collections.Generic.List<ISyncOrchestrator>();
        private NetworkOrchestrator? _networkOrchestrator;
        private VitalsSyncManager? _vitalsSyncManager;
        private readonly SaveScopeTracker _sessionSaveScope = new SaveScopeTracker();
        private InventorySyncManager? _inventorySyncManager;
        private DeathSyncManager? _deathSyncManager;
        #endregion

        #region Private Fields
        private Harmony? _harmony;

        // BUG-A1: true while the mod itself disabled the hero's physics (EnterSpectatorMode).
        // The game's bench-respawn flow never restores OUR disables, so OnBenchRespawnCompleted
        // must undo them — otherwise the hero falls through geometry with no input.
        public readonly Services.SpectatorState Spectator = new Services.SpectatorState();

        // Components the mod itself disabled when entering spectator mode. Restoring only
        // these avoids re-enabling colliders/renderers the game disabled for its own reasons.


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

        private Core.State.DesyncDetector? _desyncDetector;
        private Action<Core.State.DesyncDetector.DesyncDetectedEvent>? _desyncHandler;
        private uint? _desyncRound;
        #endregion

        #region ICoopLogger
        public void Debug(string message) => Logger.LogDebug(message);
        public new void Info(string message) => Logger.LogInfo(message);
        public void Warn(string message) => Logger.LogWarning(message);
        public void Error(string message) => Logger.LogError(message);
        #endregion

        #region Unity Lifecycle
        private void Awake()
        {
            Instance = this;
            SteamP2PTransport.Logger = Logger;
            CoopLog.Logger = this;
            Logger.LogInfo($"Initializing {PluginName} v{PluginVersion}...");

            // Startup compatibility report. When a game update moves a Harmony target, the
            // first thing needed is the exact game/Unity build; log it once instead of
            // asking a player to dig it out of the Steam client.
            try
            {
                Logger.LogInfo(
                    $"[Coop] Compatibility: game {Application.version}, Unity {Application.unityVersion}, " +
                    $"mod {PluginVersion}, protocol v{NetworkProtocol.Version}, runtime {Environment.Version}");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Compatibility report failed: {ex.Message}");
            }

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
            Container.Register<IGameplayActions>(this);
            Container.Register<IInventoryGameplayActions>(this);
            Container.Register<IReviveGameplayActions>(this);

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
                TickRateHzConfig.SettingChanged += (_, _) => _networkOrchestrator?.SetTickRate(TickRateHzConfig.Value);
                AutoReconnectConfig = Config.Bind("Network", "AutoReconnect", true, "Automatically attempt to reconnect after an unexpected disconnect");
                MaxReconnectAttemptsConfig = Config.Bind("Network", "MaxReconnectAttempts", 5, "Maximum number of automatic reconnection attempts (1-10)");
                SteamMaxPlayersConfig = Config.Bind("Network", "SteamMaxPlayers", 2,
                    "Maximum Steam lobby size including yourself (2-8). Values above 2 enable the experimental full-mesh mode; death/revive and the partner HUD are still optimized for two players.");
                SaveConnectionHistoryConfig = Config.Bind("Network", "SaveConnectionHistory", false, "Persist recent non-secret addresses and Steam lobby IDs in PlayerPrefs");
                SyncQuestFlagsConfig = Config.Bind("Gameplay", "SyncQuestFlags", true,
                    "Replicate quest-related PlayerData bool/int flags changed during NPC dialogue (best-effort; disable if a quest misbehaves)");
                LanguageConfig = Config.Bind("UI", "Language", "Auto",
                    new ConfigDescription("UI language: Auto (system), English or Russian",
                        new AcceptableValueList<string>("Auto", "English", "Russian")));
                OnboardingCompletedConfig = Config.Bind("UI", "OnboardingCompleted", false,
                    "Set to true after the first-run hint is dismissed (the Settings tab can show it again)");

                // Normalize once at startup instead of silently clamping at each use site:
                // the value written back to the .cfg matches what the session actually uses,
                // and an out-of-range edit produces one clear warning.
                NormalizeConfigValues();
                ApplyUiLanguage();

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
                UdpTransport = new LiteNetLibTransport(1, "LocalPlayer", Application.version) { QueueReliableWhenBusy = true };
                ActiveTransport = UdpTransport;
                Container.Register<LiteNetLibTransport>(UdpTransport);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Failed to initialize UDP transport: {ex.Message}");
            }

            try
            {
                SteamTransport = new SteamP2PTransport(
                    Math.Clamp(SteamMaxPlayersConfig?.Value ?? 2, 2, 8));
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
                var sessionService = new SessionService(EventBus, transportService, this, SessionState);
                if (!ReferenceEquals(SessionState, sessionService.SessionState))
                {
                    Logger.LogError("[Coop] Composition error: SessionService.SessionState is not the plugin's SessionState. " +
                        "The UI and network managers would observe different session state (Connected/RemotePeerId).");
                }
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
            QuestStateSync = new QuestStateSyncManager(this);

            // Create orchestrator services that own the network hot-path and sync callbacks.
            // They subscribe to EventBus events raised by SessionService/TransportService.
            // Created after EnemySync/CloneSpawner so the managers receive live instances.
            if (Container != null)
            {
                try
                {
                    var sessionService = Container.Resolve<ISessionService>();
                    var transportService = Container.Resolve<ITransportService>();
                    _networkOrchestrator = new NetworkOrchestrator(
                        sessionService,
                        transportService,
                        EventBus!,
                        CloneSpawner!,
                        TickRateHzConfig?.Value ?? 30f);
                    _vitalsSyncManager = new VitalsSyncManager(sessionService, transportService, VitalsSync, DeathCoordinator, EnemySync);

                    ReviveCoordinator = new Coordinators.ReviveLifecycleCoordinator(
                        sessionService, DeathCoordinator, GroundProber,
                        () => CloneSpawner, () => MenuUI, coroutineRunner: this,
                        ReviveCooldownConfig, ReviveHealthPercentConfig, ReviveInvulnerabilityConfig);
                    InventoryCoordinator = new Coordinators.InventoryApplicationCoordinator(
                        sessionService, InventorySync, VitalsSync,
                        () => ActiveTransport, () => MenuUI?.Diagnostics);
                    HandshakeCoordinator = new Coordinators.PeerHandshakeCoordinator(
                        sessionService, VitalsSync, DeathCoordinator, InventorySync,
                        QuestStateSync, EnemySync, ReviveCoordinator, InventoryCoordinator,
                        () => _vitalsSyncManager, () => ActiveTransport, () => MenuUI?.Diagnostics, () => IsSaveReady,
                        reason => DisconnectFromSession(reason));
                    PacketRouter = new Coordinators.IncomingPacketRouter(
                        sessionService, HandshakeCoordinator, ReviveCoordinator,
                        InventoryCoordinator, VitalsSync, DeathCoordinator,
                        QuestStateSync, EnemySync, () => CloneSpawner,
                        () => MenuUI, () => MenuUI?.Diagnostics, () => _vitalsSyncManager,
                        () => IsSaveReady, () => CanSendSceneTransform, () => ActiveTransport,
                        reason => DisconnectFromSession(reason), () => _desyncDetector);

                    Container.Register<IReviveGameplayActions>(ReviveCoordinator);
                    Container.Register<IInventoryGameplayActions>(InventoryCoordinator);
                    Container.Register<IGameplayActions>(this);
                    Container.Register(ReviveCoordinator);
                    Container.Register(InventoryCoordinator);
                    Container.Register(HandshakeCoordinator);
                    Container.Register(PacketRouter);

                    _orchestrators.Add(ReviveCoordinator);
                    _orchestrators.Add(InventoryCoordinator);
                    _orchestrators.Add(HandshakeCoordinator);

                    var inventoryActions = Container.Resolve<IInventoryGameplayActions>();
                    var reviveActions = Container.Resolve<IReviveGameplayActions>();
                    _inventorySyncManager = new InventorySyncManager(sessionService, transportService, inventoryActions, InventorySync);
                    _deathSyncManager = new DeathSyncManager(sessionService, transportService, reviveActions, DeathCoordinator);
                    _desyncDetector = new Core.State.DesyncDetector(EventBus!);
                    _desyncHandler = e =>
                        Logger.LogWarning($"[Coop] Desync detected ({e.Context}): local={e.LocalChecksum} remote={e.RemoteChecksum}");
                    EventBus!.Subscribe(_desyncHandler);

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
            if (_networkOrchestrator != null) _networkOrchestrator.Diagnostics = MenuUI?.Diagnostics;
            if (_vitalsSyncManager != null) _vitalsSyncManager.Diagnostics = MenuUI?.Diagnostics;
            if (_inventorySyncManager != null) _inventorySyncManager.Diagnostics = MenuUI?.Diagnostics;
            if (_deathSyncManager != null) _deathSyncManager.Diagnostics = MenuUI?.Diagnostics;
            if (Container != null)
            {
                Container.Register(MenuUI!);
            }

            // The first-run hint is shown once per install until the player dismisses the
            // onboarding card. It is a toast rather than a modal so it never blocks play.
            if (OnboardingCompletedConfig?.Value == false)
            {
                MenuUI!.Toasts.ShowInfo(
                    Loc.T("Press F10 for the co-op menu. F11 shows network diagnostics."), 6f);
            }

            Logger.LogInfo("Silksong Co-op initialization complete. Press F10 in game for Co-op menu.");
        }

        /// <summary>
        /// Clamps every user-editable numeric config to its documented range and writes the
        /// corrected value back to the .cfg. A silent clamp at each use site made the file
        /// disagree with the running session; this keeps them in sync and logs one warning.
        /// </summary>
        private void NormalizeConfigValues()
        {
            NormalizeFloat(ReviveCooldownConfig, 15f, 30f, 20f, "ReviveCooldownSeconds");
            NormalizeFloat(ReviveHealthPercentConfig, 0.1f, 1f, 0.5f, "ReviveHealthPercent");
            NormalizeFloat(ReviveInvulnerabilityConfig, 0f, 10f, 2.5f, "ReviveInvulnerabilitySeconds");
            NormalizeFloat(Player2HueShiftConfig, 0f, 1f, 0.58f, "Player2HueShift");
            NormalizeInt(DefaultUdpPortConfig, 1024, 65535, 7777, "DefaultUdpPort");
            NormalizeInt(TickRateHzConfig, 20, 60, 30, "TickRateHz");
            NormalizeInt(MaxReconnectAttemptsConfig, 1, 10, 5, "MaxReconnectAttempts");
            NormalizeInt(SteamMaxPlayersConfig, 2, 8, 2, "SteamMaxPlayers");
        }

        private void NormalizeFloat(
            BepInEx.Configuration.ConfigEntry<float>? entry,
            float min,
            float max,
            float fallback,
            string name)
        {
            if (entry == null) return;

            float oldValue = entry.Value;
            float normalized = float.IsNaN(oldValue) || float.IsInfinity(oldValue)
                ? fallback
                : Math.Clamp(oldValue, min, max);
            if (Math.Abs(normalized - oldValue) < 0.0001f) return;

            entry.Value = normalized;
            Logger.LogWarning($"[Coop] Config {name}={oldValue} is outside {min}..{max}; using {normalized}");
        }

        private void NormalizeInt(
            BepInEx.Configuration.ConfigEntry<int>? entry,
            int min,
            int max,
            int fallback,
            string name)
        {
            if (entry == null) return;

            int oldValue = entry.Value;
            int normalized = Math.Clamp(oldValue, min, max);
            if (normalized == oldValue) return;

            // A corrupted value is still clamped; log the fallback separately to make it
            // obvious that the file was not simply edited to the boundary by the user.
            if (oldValue == 0) normalized = fallback;
            entry.Value = normalized;
            Logger.LogWarning($"[Coop] Config {name}={oldValue} is outside {min}..{max}; using {normalized}");
        }

        /// <summary>
        /// Resolves the configured UI language (Auto/English/Russian) and applies it to
        /// <see cref="Loc"/>. Called at startup and when the user changes it in Settings.
        /// </summary>
        public void ApplyUiLanguage()
        {
            string requested = LanguageConfig?.Value ?? "Auto";
            if (!Enum.TryParse(requested, ignoreCase: true, out CoopLanguage language))
            {
                Logger.LogWarning($"[Coop] Unknown UI language '{requested}'; using Auto");
                language = CoopLanguage.Auto;
                requested = "Auto";
                if (LanguageConfig != null) LanguageConfig.Value = "Auto";
            }

            Loc.SetLanguage(language, Application.systemLanguage == SystemLanguage.Russian || IsSteamLanguageRussian());
            Logger.LogInfo($"[Coop] UI language: {Loc.Current} (configured: {requested})");
        }

        /// <summary>
        /// Steam's UI language is a better default than the OS language for players who
        /// run the game in a different language than Windows. The Steam API is not always
        /// initialized (direct exe launch), so every failure falls back to false.
        /// </summary>
        private static bool IsSteamLanguageRussian()
        {
            try
            {
                string language = Steamworks.SteamApps.GetCurrentGameLanguage();
                return !string.IsNullOrEmpty(language) &&
                       language.StartsWith("russian", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
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
            ResetPeerReadiness();
            _vitalsSyncManager?.OnSessionEnded();
            CloneSpawner?.ResetAllClones();
            InventorySync?.ResetSessionState();
            EnemySync?.ResetSession();
            QuestStateSync?.ResetSession();
            VitalsSync?.ClearRemotePeers();
            DeathCoordinator?.ClearRemotePeers();
            SessionState?.SetDisconnected(DisconnectReason.GracefulDisconnect);

            if (_reviveInvulnerabilityActive)
            {
                var hero = HeroController.instance;
                if (hero != null && hero.gameObject != null)
                {
                    hero.cState.invulnerable = false;
                    hero.cState.recoiling = false;
                    hero.SetDamageMode(GlobalEnums.DamageMode.FULL_DAMAGE);
                    var pulse = hero.GetComponent<InvulnerablePulse>();
                    pulse?.StopInvulnerablePulse();
                }
                _reviveInvulnerabilityActive = false;
            }
        }

        /// <summary>
        /// Ends the current session from user input (menu button, transport switch) and
        /// performs the gameplay cleanup synchronously. The transport's PeerDisconnected
        /// event is deferred for UDP and its handler deliberately ignores events after the
        /// state is already Disconnected, so relying on that event left the partner clone,
        /// death state and spectator view behind until the next session.
        /// </summary>
        public void DisconnectFromSession(DisconnectReason reason = DisconnectReason.GracefulDisconnect,
            bool respawnSpectator = true)
        {
            try
            {
                // Capture the death state before it is overwritten by SetDisconnected.
                bool recoveringFromSpectator =
                    DeathCoordinator?.CurrentState == DeathState.Spectating ||
                    DeathCoordinator?.CurrentState == DeathState.RespawningAtPartner;

                // Mark the session ended FIRST: Steam delivers its disconnect callback
                // synchronously from Disconnect(), and the transport handler must not run
                // the same teardown twice.
                SessionState?.SetDisconnected(reason);
                ResetPeerReadiness();

                try
                {
                    ActiveTransport?.Disconnect();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[Coop] Failed to close transport: {ex.Message}");
                }

                ResetDesyncTracking();
                _vitalsSyncManager?.OnSessionEnded();
                EventBus?.Publish(new SessionEndedEvent { Reason = reason });
                MenuUI?.Diagnostics?.OnSessionEnded();

                CloneSpawner?.ResetAllClones();
                VitalsSync?.ClearRemotePeers();
                DeathCoordinator?.ClearRemotePeers();
                InventorySync?.ResetSessionState();
                EnemySync?.ResetSession();
                QuestStateSync?.ResetSession();

                bool isWipeOrRecoveryActive = IsTeamWipeInProgress ||
                    DeathCoordinator?.CurrentState == DeathState.TeamWipe;

                if ((recoveringFromSpectator || Spectator.IsActive) && !isWipeOrRecoveryActive)
                {
                    ExitSpectatorMode();
                    if (respawnSpectator) TriggerTeamWipe();
                }
                else if (Spectator.IsActive)
                {
                    ExitSpectatorMode();
                }

                RefreshLocalVisuals(false);
                MenuUI?.SetStatusMessage(Loc.T("Disconnected"));
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Coop] Failed to disconnect session: {ex.Message}\n{ex.StackTrace}");
            }
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
                if (ActiveTransport == null || SessionState?.IsConnected != true || !CanSendSceneTransform) return;
                var hero = HeroController.instance;
                Vector2 pos = (hero != null) ? (Vector2)hero.transform.position : Vector2.zero;
                Vector2 dir = (hero != null && hero.cState.facingRight) ? Vector2.right : Vector2.left;

                _actionTick++;
                var packet = new ActionTriggerPacket
                {
                    Header = new PacketHeader(PacketType.ActionTrigger, 0, _actionTick, NetworkClock.NowMs),
                    Action = action,
                    SceneGeneration = _sceneGeneration,
                    ActionSequenceId = _actionTick,
                    OriginX = pos.x,
                    OriginY = pos.y,
                    DirectionX = dir.x,
                    DirectionY = dir.y,
                    Parameter = parameter
                };

                // Actions used to go only to the primary peer, so in an experimental 3+
                // session one guest saw movement without the matching actions.
                // A refused reliable send is logged: a lost action leaves the partner's
                // world one trigger behind until the next interaction.
                if (!PacketSender.SendToAll(
                    ActiveTransport,
                    packet,
                    DeliveryMethod.ReliableOrdered,
                    NetworkChannels.Action,
                    64,
                    MenuUI?.Diagnostics,
                    "[Coop]"))
                    SilksongCoopPlugin.LogWarn("[Coop] Transport refused an action trigger; the partner may miss it until the next interaction.");
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
            var pd = PlayerData.instance;
            if (pd == null) return;

            var packet = new BenchCheckpointSyncPacket
            {
                Header = new PacketHeader(PacketType.BenchCheckpointSync, NetworkChannels.Death, 0, NetworkClock.NowMs),
                SceneName = sceneName,
                RespawnMarker = respawnMarker,
                SpawnType = spawnType,
                FacingRight = facingRight
            };
            if (!PacketValidator.ValidateBenchCheckpoint(packet)) return;
            HandshakeCoordinator?.BroadcastBenchCheckpoint(packet);
            if (ActiveTransport != null && SessionState?.IsConnected == true)
            {
                // The checkpoint is kept in _lastBenchCheckpoint and re-sent during peer
                // catch-up, so a refusal only delays it — but it must not be silent.
                if (!PacketSender.SendToAll(ActiveTransport, packet, DeliveryMethod.ReliableOrdered,
                        NetworkChannels.Death, 1024, MenuUI?.Diagnostics))
                    SilksongCoopPlugin.LogWarn("[Coop] Transport refused a bench checkpoint broadcast.");
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
            TryApplyRemoteBenchCheckpoint(sceneName, respawnMarker, spawnType, facingRight);
        }

        private bool TryApplyRemoteBenchCheckpoint(string sceneName, string respawnMarker, int spawnType, bool facingRight)
        {
            return HandshakeCoordinator != null && HandshakeCoordinator.TryApplyRemoteBenchCheckpoint(sceneName, respawnMarker, spawnType, facingRight);
        }

        public void NoteLocalHealthUpgrade(int amount) => InventoryCoordinator?.NoteLocalHealthUpgrade(amount);
        public void NoteLocalSilkUpgrade(int amount) => InventoryCoordinator?.NoteLocalSilkUpgrade(amount);

        public void BroadcastHeartPieces(int count) => InventoryCoordinator?.BroadcastHeartPieces(count);
        public void BroadcastHeartPiecesBaseline(int count) => InventoryCoordinator?.BroadcastHeartPiecesBaseline(count);

        public void BroadcastSilkParts(int count) => InventoryCoordinator?.BroadcastSilkParts(count);
        public void BroadcastSilkPartsBaseline(int count) => InventoryCoordinator?.BroadcastSilkPartsBaseline(count);

        public void BroadcastInitialInventory() => InventoryCoordinator?.BroadcastInitialInventory(null);
        public void BroadcastInitialInventory(NetworkPeerId? targetPeer) => InventoryCoordinator?.BroadcastInitialInventory(targetPeer);

        public void ApplyRemoteItemUnlock(string itemId, ItemCategory category) => InventoryCoordinator?.ApplyRemoteItemUnlock(itemId, category);
        public bool TryApplyRemoteItemUnlock(string itemId, ItemCategory category) => InventoryCoordinator?.TryApplyRemoteItemUnlock(itemId, category) ?? false;
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

                    bool firstPeer = SessionState?.IsConnected != true;

                    if (firstPeer)
                    {
                        // Issue #2.2: never inherit a wipe/spectator latch from the previous
                        // session. If the old session ended mid-wipe, this flag stayed set until
                        // the game was restarted and every later team wipe silently no-oped.
                        ReviveCoordinator?.ResetTeamWipeLatch();
                        // Clearing the flag without restoring the hero would leave it with
                        // gravity/colliders/rendering disabled if a reconnect raced the bench
                        // reload. ExitSpectatorMode performs the actual restore.
                        if (Spectator.IsActive) ExitSpectatorMode();
                        InventoryCoordinator?.OnSessionStarted(transport.IsHost);
                        EnemySync?.ResetSession();
                        InventorySync?.ResetSessionState();
                        QuestStateSync?.ResetSession();
                    }

                    Logger.LogInfo($"[Coop] Peer connected: {peer}");
                    if (firstPeer)
                    {
                        SessionState?.SetConnected(peer, transport.IsHost ? SessionRole.Host : SessionRole.Client);
                        ResetDesyncTracking();
                    }
                    else
                    {
                        SessionState?.AddRemotePeer(peer);
                    }

                    EventBus?.Publish(new PeerConnectedEvent { PeerId = peer });
                    if (firstPeer)
                    {
                        MenuUI?.Diagnostics?.OnSessionStarted();
                    }
                    MenuUI?.SetStatusMessage(Loc.T("Connected to {0}", peer.DisplayName));

                    if (CloneSpawner == null)
                    {
                        Logger.LogWarning("[Coop] CloneSpawner is null, cannot spawn remote player");
                        return;
                    }

                    bool remoteIsPlayer2 = transport.IsHost;
                    if (firstPeer)
                    {
                        CloneSpawner.ResetAllClones();
                        RefreshLocalVisuals(!transport.IsHost);
                        DeathCoordinator?.SynchronizeEpochForNewSession();
                    }
                    CloneSpawner.SpawnRemotePlayer(peer, remoteIsPlayer2);
                    DeathCoordinator?.RegisterRemotePeer(peer);
                    DeathCoordinator?.SetRemotePeerInSameScene(peer, CloneSpawner.IsRemoteInSameScene(peer));

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
                    if (SessionState == null || SessionState.Status == SessionStatus.Disconnected) return;

                    Logger.LogInfo($"[Coop] Peer disconnected: {peer}, reason: {reason}");

                    bool wasHost = SessionState.Role == SessionRole.Host;
                    bool wasPrimary = SessionState.RemotePeerId == peer;
                    bool wasLastPeer = SessionState.RemotePeerIds.Count <= 1;

                    // On a client the primary peer is the host; if the host leaves, the
                    // lobby is over even when additional peers are still registered.
                    bool endSession = wasLastPeer || (!wasHost && wasPrimary);

                    _vitalsSyncManager?.OnCurrencyPeerDisconnected(peer);
                    _remoteScenes.Remove(peer);
                    HandshakeCoordinator?.ResetPeerState(peer);
                    SessionState.RemoveRemotePeer(peer);
                    CloneSpawner?.DespawnRemotePlayer(peer);
                    VitalsSync?.RemoveRemotePeer(peer);
                    DeathCoordinator?.RemoveRemotePeer(peer);
                    EventBus?.Publish(new PeerDisconnectedEvent { PeerId = peer, Reason = reason });

                    if (!endSession)
                    {
                        // Keep per-peer state consistent when a peer leaves but the session
                        // continues: otherwise a reconnecting peer can have stale scene
                        // sequences/counter state that reject fresh updates.
                        InventorySync?.ResetPeerState(peer);
                        PacketRouter?.ClearPeer(peer);
                        MenuUI?.SetStatusMessage(Loc.T("Player left: {0}", peer.DisplayName));
                        return;
                    }

                    // SetDisconnected() clears CurrentRoomCode and SteamLobbyId, so the
                    // reconnect target must be captured before it runs. A host has
                    // nobody to dial back, so it just keeps listening.
                    string? reconnectTarget = wasHost
                        ? null
                        : (transport == SteamTransport
                            ? SessionState.SteamLobbyId?.ToString()
                            : SessionState.CurrentRoomCode);
                    string? hostRoomCode = wasHost ? SessionState.CurrentRoomCode : null;

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

                    SessionState.SetDisconnected(reason);
                    ResetDesyncTracking();
                    _vitalsSyncManager?.OnSessionEnded();
                    EventBus?.Publish(new SessionEndedEvent { Reason = reason });
                    MenuUI?.Diagnostics?.OnSessionEnded();
                    // In a 3+ session the transport may disconnect several peers in one
                    // teardown; drop every clone, not just the first event's peer.
                    CloneSpawner?.ResetAllClones();
                    RefreshLocalVisuals(false);
                    DeathCoordinator?.ClearRemotePeers();

                    // Multi-peer safe: If other peers remain connected in a 3+ session, only reset this peer's state.
                    if (SessionState.RemotePeerIds.Count > 1)
                    {
                        InventorySync?.ResetPeerState(peer);
                        HandshakeCoordinator?.ResetPeerState(peer);
                        PacketRouter?.ClearPeer(peer);
                    }
                    else
                    {
                        InventorySync?.ResetSessionState();
                        HandshakeCoordinator?.ResetPeerReadiness();
                        PacketRouter?.Clear();
                    }
                    EnemySync?.ResetSession();
                    QuestStateSync?.ResetSession();

                    bool isWipeOrRecoveryActive = IsTeamWipeInProgress ||
                        DeathCoordinator?.CurrentState == DeathState.TeamWipe;

                    if (recoveringFromSpectator && !isWipeOrRecoveryActive)
                    {
                        Logger.LogWarning("[Coop] Partner disconnected while in spectator mode. Exiting spectator and respawning at bench.");
                        ExitSpectatorMode();
                        TriggerTeamWipe();
                    }
                    else if (Spectator.IsActive)
                    {
                        ExitSpectatorMode();
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
                            ? Loc.T("Partner timed out — still hosting. Share the same room code to reconnect.")
                            : Loc.T("Partner left — still hosting. Share the same room code to reconnect."));
                    }
                    else if (wasHost && reason == DisconnectReason.VersionMismatch)
                    {
                        // Don't keep hosting on version mismatch; user needs to update
                        SessionState?.SetDisconnected();
                        MenuUI?.SetStatusMessage(Loc.T("Version mismatch — update both players to the same version."));
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
                                MenuUI?.SetStatusMessage(Loc.T("Disconnected. Reconnecting in {0}s...", backoff));
                            }
                        }
                        else
                        {
                            Logger.LogWarning($"[Coop] Max reconnect attempts ({maxAttempts}) reached. Giving up.");
                            MenuUI?.SetStatusMessage(Loc.T("Connection lost. Max reconnect attempts reached."));
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
                PacketRouter?.HandleDataReceived(transport, ActiveTransport, peer, data, offset, length);
            };
        }

        private void ApplyWalletSeed(NetworkPeerId peer, WalletSeedPacket seed) => HandshakeCoordinator?.ApplyWalletSeed(peer, seed);

        internal void OnCurrencyQueueDrained() => _vitalsSyncManager?.OnCurrencyQueueDrained();
        internal void ScheduleWalletReconcile() => _vitalsSyncManager?.ScheduleWalletReconcile();

        private void ResetPeerReadiness() => HandshakeCoordinator?.ResetPeerReadiness();
        private void PumpPeerReadiness() => HandshakeCoordinator?.PumpPeerReadiness();

        private void ProcessPacket(NetworkPeerId peer, ReadOnlySpan<byte> span) => PacketRouter?.ProcessPacket(peer, span);
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
                _departedSceneHandle = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
                _sceneGeneration++;
                if (ActiveTransport == null || SessionState?.IsConnected != true) return;

                foreach (var peer in SessionState.RemotePeerIds)
                    CloneSpawner?.SetRemoteScenePresence(peer, false);

                int sceneHash = GameManager.instance != null ? GameManager.instance.sceneNameHash : 0;
                var packet = new SceneTransitionPacket
                {
                    Header = new PacketHeader(PacketType.SceneTransition, NetworkChannels.Action, _sceneGeneration, NetworkClock.NowMs),
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
                    ActiveTransport.SendToAll(
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
                    case DeathEventType.LifecycleSnapshot:
                        CloneSpawner?.SetRemotePartnerAlive(peer, DeathCoordinator?.IsRemotePeerAlive(peer) ?? false);
                        break;
                    case DeathEventType.SoloDeathOccurred:
                    case DeathEventType.TeamWipeOccurred:
                        CloneSpawner?.SetRemotePartnerAlive(peer, false);
                        break;
                    case DeathEventType.PartnerReviveTrigger:
                    case DeathEventType.SoloBenchRespawn:
                    case DeathEventType.BenchCheckpointUpdate:
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
                // Run before polling either transport so queued packets cannot touch a new save.
                var game = GameManager.instance;
                if (game != null && game.profileID > 0 && !game.IsMenuScene() &&
                    _sessionSaveScope.Observe(game.profileID, PlayerData.instance))
                {
                    OnLeavingSave();
                }

                // Always poll Steam transport for callbacks (lobby invites, session requests, etc.)
                // even when UDP is the active data transport.
                if (SteamTransport != null && ActiveTransport != SteamTransport)
                {
                    SteamTransport.PollEvents();
                }
                PumpPeerReadiness();
                ActiveTransport?.PollEvents();
                PumpPeerReadiness();
                PacketRouter?.DrainDeferredPackets();
                _networkOrchestrator?.Update();
                foreach (var orch in _orchestrators)
                {
                    if (orch != _networkOrchestrator)
                        orch.Update();
                }

                UpdateDesyncDetector();
                QuestStateSync?.Poll();

                bool isSpectating = Spectator.IsActive || DeathCoordinator?.CurrentState == DeathState.Spectating;
                if (isSpectating && Keybinds != null && Keybinds.IsKeyDown(KeybindManager.ToggleSpectatorCamera))
                {
                    _spectatorCameraFollow = !_spectatorCameraFollow;
                    MenuUI?.SetStatusMessage(_spectatorCameraFollow
                        ? Loc.T("Spectator camera: follow partner")
                        : Loc.T("Spectator camera: hold position"));
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
                                // A reconnect is always a client join. Keep its peer id
                                // distinct from the host's without requiring a game restart
                                // after a previous hosting session used id 1.
                                if (transport is LiteNetLibTransport udpTransport)
                                    udpTransport.SetLocalPeerId(2, "Client");

                                SetActiveTransport(transport);
                                transport.Connect(addr);
                                SessionState?.SetConnecting(transport.LocalPeerId, addr);
                                MenuUI?.SetStatusMessage(Loc.T("Reconnecting (attempt {0})...", _reconnectAttempts));
                                Logger.LogInfo($"[Coop] Auto-reconnect attempt {_reconnectAttempts}");
                            }
                            else
                            {
                                _reconnectPending = true;
                                _nextReconnectTime = Time.unscaledTime + 4f;
                                Logger.LogWarning("[Coop] Auto-reconnect deferred: transport unavailable");
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
                                MenuUI?.SetStatusMessage(Loc.T("Connection lost. Max reconnect attempts reached."));
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
                        MenuUI?.SetStatusMessage(Loc.T("Joining Steam Lobby {0}...", _pendingSteamLobbyJoin.Value));
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

                if (SessionState?.IsConnected == true)
                {
                    var remotePeers = SessionState.RemotePeerIds;
                    if (CloneSpawner != null)
                    {
                        for (int i = 0; i < remotePeers.Count; i++)
                        {
                            var remotePeer = remotePeers[i];
                            DeathCoordinator?.SetRemotePeerInSameScene(
                                remotePeer,
                                CloneSpawner.IsRemoteInSameScene(remotePeer));

                            if (CloneSpawner.TryGetRemotePosition(remotePeer, out float posX, out float posY))
                            {
                                DeathCoordinator?.UpdatePartnerPosition(remotePeer, posX, posY);
                            }
                        }
                    }

                    // If spectating, tether the camera to a living partner. The
                    // coordinator prefers a living peer's position when several exist.
                    if (DeathCoordinator != null &&
                        DeathCoordinator.CurrentState == DeathState.Spectating &&
                        _spectatorCameraFollow &&
                        remotePeers.Count > 0)
                    {
                        var (targetX, targetY) = DeathCoordinator.LastPartnerPosition;
                        var camTarget = GameManager.instance?.cameraCtrl?.camTarget;
                        if (camTarget != null)
                        {
                            var curPos = camTarget.transform.position;
                            camTarget.transform.position = new Vector3(targetX, targetY, curPos.z);
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

        internal void OnLeavingSave()
        {
            CancelReconnect();
            if (SessionState != null && SessionState.Status != SessionStatus.Disconnected)
                DisconnectFromSession(respawnSpectator: false);
            VitalsSync?.ClearClaimedPickups();
            _departedSceneHandle = null;
            HandshakeCoordinator?.OnSessionEnded();
            QuestStateSync?.ClearAuthoredChanges();
            _vitalsSyncManager?.ResetCurrencyJournal();
            _sessionSaveScope.Reset();
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

        /// <summary>
        /// Restores the local hero after a plugin unload. Spectator mode and revive
        /// invulnerability are applied by this plugin, and their normal cleanup runs only
        /// when the corresponding coroutine finishes.
        /// </summary>
        private void RestoreGameplayStateForTeardown()
        {
            try
            {
                if (Spectator.IsActive ||
                    DeathCoordinator?.CurrentState == DeathState.Spectating ||
                    DeathCoordinator?.CurrentState == DeathState.RespawningAtPartner ||
                    DeathCoordinator?.CurrentState == DeathState.TeamWipe)
                {
                    ExitSpectatorMode();
                }

                // Only undo the damage mode this plugin applied. The game may own
                // HAZARD_ONLY/FULL_DAMAGE for its own hazard or scripted sequence.
                if (!_reviveInvulnerabilityActive) return;

                var hero = HeroController.instance;
                if (hero == null || hero.gameObject == null) return;

                hero.cState.invulnerable = false;
                hero.cState.recoiling = false;
                hero.SetDamageMode(GlobalEnums.DamageMode.FULL_DAMAGE);
                _reviveInvulnerabilityActive = false;

                var pulse = hero.GetComponent<InvulnerablePulse>();
                pulse?.StopInvulnerablePulse();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Failed restoring hero state on unload: {ex.Message}");
            }
        }

        private static void DestroyPersistentObject(Component? component)
        {
            try
            {
                if (component != null && component.gameObject != null)
                    Destroy(component.gameObject);
            }
            catch
            {
                // Unity may already be shutting down; the object is gone with the process.
            }
        }

        private void OnDestroy()
        {
            try
            {
                StopAllCoroutines();

                // StopAllCoroutines() also cancels ReviveInvulnerabilityRoutine, whose
                // cleanup lives after the wait loop. Restore the hero explicitly so a
                // plugin unload cannot leave it invulnerable, physics-disabled or without
                // control when the plugin is removed/reloaded.
                RestoreGameplayStateForTeardown();

                // Unsubscribe managers before disposing transports so no callback can run
                // against a half-torn-down session.
                foreach (var orchestrator in _orchestrators)
                {
                    if (orchestrator is IDisposable disposable)
                        disposable.Dispose();
                }
                _orchestrators.Clear();
                if (_desyncHandler != null)
                {
                    EventBus?.Unsubscribe(_desyncHandler);
                    _desyncHandler = null;
                }

                // BUG-C3: dispose BOTH transports — previously only the active one was
                // disconnected, so a Steam session leaked the UDP socket and held port
                // 7777 until the game process exited.
                UdpTransport?.Dispose();
                SteamTransport?.Dispose();
                if (ActiveTransport != null &&
                    !ReferenceEquals(ActiveTransport, UdpTransport) &&
                    !ReferenceEquals(ActiveTransport, SteamTransport))
                {
                    ActiveTransport.Dispose();
                }
                CancelReconnect();

                _harmony?.UnpatchSelf();

                // The spawner, enemy sync and menu are separate DontDestroyOnLoad objects;
                // destroying only the plugin's own GameObject leaked them into a reloaded
                // plugin instance (two spawners/two menu UIs sharing static state).
                DestroyPersistentObject(CloneSpawner);
                DestroyPersistentObject(EnemySync);
                DestroyPersistentObject(MenuUI);

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
                CoopLog.Logger = null;
                Instance = null;
            }
        }
        #endregion
    }
}
