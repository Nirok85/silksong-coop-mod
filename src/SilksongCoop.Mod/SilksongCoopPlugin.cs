using System;
using System.Collections;
using BepInEx;
using HarmonyLib;
using SilksongCoop.Core.Clones;
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
using SilksongCoop.Mod.UI;
using SilksongCoop.Mod.Visuals;
using UnityEngine;

namespace SilksongCoop.Mod
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class SilksongCoopPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.silksong.coop";
        public const string PluginName = "Silksong Co-op Multiplayer";
        public const string PluginVersion = "1.0.0";

        public static SilksongCoopPlugin? Instance { get; private set; }
        public static void LogWarn(string message) => Instance?.Logger.LogWarning(message);

        public CoopSessionState SessionState { get; private set; } = null!;
        public VitalsSyncService VitalsSync { get; private set; } = null!;
        public DeathLifecycleCoordinator DeathCoordinator { get; private set; } = null!;
        public InventorySyncService InventorySync { get; private set; } = null!;
        public IGroundProber GroundProber { get; set; } = new UnityGroundProber();

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

        public INetworkTransport? ActiveTransport { get; private set; }
        public SteamP2PTransport? SteamTransport { get; private set; }
        public LiteNetLibTransport? UdpTransport { get; private set; }
        public CoopMenuUI MenuUI { get; private set; } = null!;
        public RemoteHornetSpawner CloneSpawner { get; private set; } = null!;

        private Harmony? _harmony;
        private volatile bool _isTeamWipeInProgress;
        private ulong? _pendingSteamLobbyJoin;

        // Outgoing transform replication. Start above one interval so the first
        // packet after connecting is sent immediately instead of waiting a tick.
        private float _transformSendAccumulator = 1f;
        private uint _transformTick;

        // Auto-reconnect state
        private bool _reconnectPending;
        private float _nextReconnectTime;
        private int _reconnectAttempts;
        private bool _reconnectIsSteam;
        private string? _reconnectAddress;

        private void Awake()
        {
            Instance = this;
            SteamP2PTransport.Logger = Logger;
            Logger.LogInfo($"Initializing {PluginName} v{PluginVersion}...");

            SessionState = new CoopSessionState();
            VitalsSync = new VitalsSyncService();
            DeathCoordinator = new DeathLifecycleCoordinator();
            InventorySync = new InventorySyncService();

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

                // Apply config values
                DeathCoordinator.ReviveCooldownDuration = Math.Clamp(ReviveCooldownConfig.Value, 15f, 30f);
                Logger.LogInfo($"[Coop] Config loaded: ReviveCooldown={DeathCoordinator.ReviveCooldownDuration}s, HueShift={Player2HueShiftConfig.Value}");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Failed to load config: {ex.Message}");
            }

            // Setup outgoing network callbacks - use stackalloc to avoid heap allocation pressure
            VitalsSync.OnOutgoingVitalsBroadcast += packet =>
            {
                if (SessionState.RemotePeerId.HasValue && ActiveTransport != null)
                {
                    Span<byte> buffer = stackalloc byte[64];
                    var writer = new PacketWriter(buffer);
                    packet.Serialize(ref writer);
                    ActiveTransport.Send(SessionState.RemotePeerId.Value, buffer.ToArray(), 0, writer.Position, DeliveryMethod.ReliableOrdered, 2);
                    MenuUI?.Diagnostics?.RecordSent(writer.Position);
                }
            };

            VitalsSync.OnOutgoingCurrencyBroadcast += packet =>
            {
                if (SessionState.RemotePeerId.HasValue && ActiveTransport != null)
                {
                    Span<byte> buffer = stackalloc byte[64];
                    var writer = new PacketWriter(buffer);
                    packet.Serialize(ref writer);
                    ActiveTransport.Send(SessionState.RemotePeerId.Value, buffer.ToArray(), 0, writer.Position, DeliveryMethod.ReliableOrdered, 3);
                    MenuUI?.Diagnostics?.RecordSent(writer.Position);
                }
            };

            DeathCoordinator.OnOutgoingDeathPacket += packet =>
            {
                if (SessionState.RemotePeerId.HasValue && ActiveTransport != null)
                {
                    Span<byte> buffer = stackalloc byte[64];
                    var writer = new PacketWriter(buffer);
                    packet.Serialize(ref writer);
                    ActiveTransport.Send(SessionState.RemotePeerId.Value, buffer.ToArray(), 0, writer.Position, DeliveryMethod.ReliableOrdered, 4);
                    MenuUI?.Diagnostics?.RecordSent(writer.Position);
                }
            };

            InventorySync.OnOutgoingItemUnlockPacket += packet =>
            {
                if (SessionState.RemotePeerId.HasValue && ActiveTransport != null)
                {
                    Span<byte> buffer = stackalloc byte[512];
                    var writer = new PacketWriter(buffer);
                    packet.Serialize(ref writer);
                    ActiveTransport.Send(SessionState.RemotePeerId.Value, buffer.ToArray(), 0, writer.Position, DeliveryMethod.ReliableOrdered, 3);
                    MenuUI?.Diagnostics?.RecordSent(writer.Position);
                }
            };

            // Setup game application callbacks
            VitalsSync.OnSharedMaxHealthChanged += newMax =>
            {
                var pd = PlayerData.instance;
                if (pd != null)
                {
                    HeroVitalsHooks.IsApplyingRemoteUpgrade = true;
                    try
                    {
                        pd.maxHealthBase = newMax;
                        pd.maxHealth = newMax;
                        HeroController.instance?.MaxHealthKeepBlue();
                    }
                    finally
                    {
                        HeroVitalsHooks.IsApplyingRemoteUpgrade = false;
                    }
                }
            };

            VitalsSync.OnSharedNeedleUpgradeChanged += newLevel =>
            {
                var pd = PlayerData.instance;
                if (pd != null)
                {
                    HeroVitalsHooks.IsApplyingRemoteUpgrade = true;
                    try
                    {
                        if (newLevel > pd.nailUpgrades)
                        {
                            pd.nailUpgrades = newLevel;
                        }
                    }
                    finally
                    {
                        HeroVitalsHooks.IsApplyingRemoteUpgrade = false;
                    }
                }
            };

            VitalsSync.OnSharedMaxSilkChanged += newSpools =>
            {
                var pd = PlayerData.instance;
                if (pd != null)
                {
                    HeroVitalsHooks.IsApplyingRemoteUpgrade = true;
                    try
                    {
                        int newMaxSilk = newSpools * 9;
                        if (newMaxSilk > pd.silkMax)
                        {
                            pd.silkMax = newMaxSilk;
                            HeroController.instance?.RefreshSilk();
                        }
                    }
                    finally
                    {
                        HeroVitalsHooks.IsApplyingRemoteUpgrade = false;
                    }
                }
            };

            VitalsSync.OnSharedCurrencyChanged += (type, delta, total) =>
            {
                var pd = PlayerData.instance;
                if (pd != null)
                {
                    CurrencyHooks.IsApplyingRemoteCurrency = true;
                    try
                    {
                        var silkgurrencyType = (type == Core.Protocol.Packets.CurrencyType.Rosaries) ? CurrencyType.Money : CurrencyType.Shard;
                        CurrencyManager.ChangeCurrency(delta, silkgurrencyType, true);
                    }
                    finally
                    {
                        CurrencyHooks.IsApplyingRemoteCurrency = false;
                    }
                }
            };

            DeathCoordinator.OnReviveAtPartnerRequested += (x, y) =>
            {
                ReviveAtPartner(x, y);
            };

            DeathCoordinator.OnTeamWipeTriggered += () =>
            {
                TriggerTeamWipe();
            };

            InventorySync.OnItemUnlocked += (itemId, cat) =>
            {
                ApplyRemoteItemUnlock(itemId, cat);
            };

            // Initialize available transports
            try
            {
                UdpTransport = new LiteNetLibTransport(1, "LocalPlayer");
                ActiveTransport = UdpTransport;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Failed to initialize UDP transport: {ex.Message}");
            }

            try
            {
                SteamTransport = new SteamP2PTransport();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Steamworks transport not active: {ex.Message}");
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

            // Initialize Harmony
            try
            {
                _harmony = new Harmony(PluginGuid);
                _harmony.PatchAll();
                Logger.LogInfo("Harmony hooks applied successfully.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error applying Harmony patches: {ex.Message}");
            }

            // Create Clone Spawner
            var spawnerGo = new GameObject("SilksongCoop_CloneSpawner");
            DontDestroyOnLoad(spawnerGo);
            CloneSpawner = spawnerGo.AddComponent<RemoteHornetSpawner>();

            // Create Menu UI
            var uiGo = new GameObject("SilksongCoop_MenuUI");
            DontDestroyOnLoad(uiGo);
            MenuUI = uiGo.AddComponent<CoopMenuUI>();
            MenuUI.Initialize(SessionState, SteamTransport, UdpTransport);

            Logger.LogInfo("Silksong Co-op initialization complete. Press F10 in game for Co-op menu.");
        }

        public void CancelReconnect()
        {
            _reconnectPending = false;
            _reconnectAttempts = 0;
            _reconnectAddress = null;
        }

        /// <summary>
        /// Schedules another automatic reconnect attempt after a failed one.
        /// Backoff doubles per attempt (1s, 2s, 4s, ... capped at 16s).
        /// </summary>
        private void ScheduleReconnectRetry(string reason)
        {
            int maxAttempts = Math.Clamp(MaxReconnectAttemptsConfig?.Value ?? 5, 1, 10);
            _reconnectAttempts++;

            if (_reconnectAttempts < maxAttempts && !string.IsNullOrEmpty(_reconnectAddress))
            {
                float backoff = Math.Min((float)Math.Pow(2, _reconnectAttempts - 1), 16f);
                _nextReconnectTime = Time.unscaledTime + backoff;
                _reconnectPending = true;
                Logger.LogWarning($"[Coop] {reason}; retrying in {backoff}s (attempt {_reconnectAttempts}/{maxAttempts})");
                MenuUI?.SetStatusMessage($"Reconnect failed. Retrying in {backoff}s...");
            }
            else
            {
                _reconnectPending = false;
                _reconnectAddress = null;
                Logger.LogWarning($"[Coop] {reason}; max reconnect attempts ({maxAttempts}) reached.");
                MenuUI?.SetStatusMessage("Connection lost. Max reconnect attempts reached.");
            }
        }

        public void SetActiveTransport(INetworkTransport transport)
        {
            if (transport == null)
            {
                Logger.LogWarning("[Coop] SetActiveTransport called with null transport");
                return;
            }

            var oldTransport = ActiveTransport;
            ActiveTransport = transport;
            
            string transportType = transport is SteamP2PTransport ? "Steam" : "UDP";
            Logger.LogInfo($"[Coop] Active transport changed from {(oldTransport == null ? "null" : (oldTransport is SteamP2PTransport ? "Steam" : "UDP"))} to {transportType}");
        }

        public void RefreshLocalVisuals(bool isPlayer2)
        {
            try
            {
                var hero = HeroController.instance;
                if (hero == null || hero.gameObject == null) return;

                var palette = hero.gameObject.GetComponent<PaletteSwapController>() ?? hero.gameObject.AddComponent<PaletteSwapController>();
                float hueShift = Player2HueShiftConfig?.Value ?? PaletteSwapMath.Player2HueShift;
                palette.SetPlayer2(isPlayer2, hueShift);

                var dash = hero.gameObject.GetComponent<DashTrailController>() ?? hero.gameObject.AddComponent<DashTrailController>();
                dash.Initialize(isPlayer2);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Coop] Failed to refresh local visuals: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public void EnterSpectatorMode()
        {
            try
            {
                var hero = HeroController.instance;
                if (hero != null && hero.gameObject != null)
                {
                    hero.AffectedByGravity(false);
                    hero.RelinquishControl();
                    
                    foreach (var ren in hero.gameObject.GetComponentsInChildren<Renderer>())
                    {
                        if (ren != null)
                        {
                            ren.enabled = false;
                        }
                    }
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

        public void ReviveAtPartner(float x, float y)
        {
            try
            {
                var hero = HeroController.instance;
                if (hero == null || hero.gameObject == null)
                {
                    Logger.LogWarning("[Coop] Cannot revive: HeroController or gameObject is null");
                    return;
                }

                // 1. Resolve safe ground position via GroundProber
                Vector2 spawnPos = new Vector2(x, y + 0.5f);
                if (GroundProber != null && GroundProber.TryFindSafeGround(x, y, out float safeX, out float safeY))
                {
                    spawnPos = new Vector2(safeX, safeY);
                }
                else
                {
                    var pdFallback = PlayerData.instance;
                    if (pdFallback != null && pdFallback.hazardRespawnLocation != Vector3.zero)
                    {
                        spawnPos = pdFallback.hazardRespawnLocation;
                    }
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

                // 4. Calculate and apply 50% max HP
                var pd = PlayerData.instance;
                if (pd != null)
                {
                    int reviveHp = GroundProber?.CalculateReviveHealth(pd.maxHealth) ?? Math.Max(1, pd.maxHealth / 2);
                    pd.health = reviveHp;
                    pd.prevHealth = pd.health;
                    pd.healthBlue = 0;
                    hero.MaxHealthKeepBlue();
                    EventRegister.SendEvent(EventRegisterEvents.HeroHealed);
                    EventRegister.SendEvent(EventRegisterEvents.HealthUpdate);
                    VitalsSync?.BroadcastCurrentVitals();
                }

                // 5. Re-enable renderers
                foreach (var ren in hero.gameObject.GetComponentsInChildren<Renderer>())
                {
                    if (ren != null)
                    {
                        ren.enabled = true;
                    }
                }

                // 6. Restore player state in exact engine sequence: clear dead before RegainControl!
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

                // 8. Apply 2.5s invulnerability routine
                StartCoroutine(ReviveInvulnerabilityRoutine(hero, SafeGroundSolver.InvulnerabilityDuration));

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
                elapsed += Time.deltaTime;
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

        public void TriggerTeamWipe()
        {
            if (_isTeamWipeInProgress) return;
            
            try
            {
                _isTeamWipeInProgress = true;
                SessionState?.SetTeamWipe();
                
                var gm = GameManager.instance;
                if (gm != null)
                {
                    gm.StartCoroutine(gm.PlayerDead(1.5f));
                }
                else
                {
                    Logger.LogWarning("[Coop] GameManager.instance is null during team wipe");
                }
            }
            catch (Exception ex)
            {
                _isTeamWipeInProgress = false;
                Logger.LogError($"[Coop] Failed triggering team wipe: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public void OnBenchRespawnCompleted()
        {
            try
            {
                _isTeamWipeInProgress = false;

                var camTarget = GameManager.instance?.cameraCtrl?.camTarget;
                camTarget?.EndFreeMode();

                DeathCoordinator?.ResetAfterBenchRespawn();
                SessionState?.ReturnToActive();

                var hero = HeroController.instance;
                if (hero != null && hero.gameObject != null)
                {
                    hero.cState.dead = false;
                    hero.cState.hazardDeath = false;
                    
                    foreach (var ren in hero.gameObject.GetComponentsInChildren<Renderer>())
                    {
                        if (ren != null)
                        {
                            ren.enabled = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Coop] Failed on bench respawn completed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public void ApplyRemoteItemUnlock(string itemId, ItemCategory category)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                Logger.LogWarning("[Coop] ApplyRemoteItemUnlock called with null or empty itemId");
                return;
            }

            try
            {
                InventoryHooks.RemoteUnlockDepth++;

                var playerData = PlayerData.instance;
                if (playerData == null)
                {
                    Logger.LogWarning("[Coop] Cannot apply remote item unlock: PlayerData.instance is null");
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
            catch (Exception ex)
            {
                Logger.LogError($"[Coop] Failed applying remote item unlock for {itemId} ({category}): {ex.Message}");
            }
            finally
            {
                InventoryHooks.RemoteUnlockDepth--;
            }
        }

        private void BindTransportEvents(INetworkTransport? transport)
        {
            if (transport == null) return;

            transport.OnPeerConnected += peer =>
            {
                try
                {
                    // Reset auto-reconnect state on successful connection
                    _reconnectPending = false;
                    _reconnectAttempts = 0;
                    _reconnectAddress = null;
                    Logger.LogInfo($"[Coop] Peer connected: {peer}");
                    SessionState?.SetConnected(peer);
                    MenuUI?.Diagnostics?.OnSessionStarted();
                    MenuUI?.SetStatusMessage($"Connected to {peer.DisplayName}");

                    if (CloneSpawner == null)
                    {
                        Logger.LogWarning("[Coop] CloneSpawner is null, cannot spawn remote player");
                        return;
                    }

                    bool remoteIsPlayer2 = transport.IsHost;
                    CloneSpawner.SpawnRemotePlayer(peer, remoteIsPlayer2);

                    RefreshLocalVisuals(!transport.IsHost);

                    if (transport.IsHost && VitalsSync != null)
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

                    SessionState?.SetDisconnected(reason);
                    MenuUI?.Diagnostics?.OnSessionEnded();
                    CloneSpawner?.DespawnRemotePlayer(peer);
                    RefreshLocalVisuals(false);

                    // Schedule auto-reconnect on unexpected disconnect
                    bool autoReconnect = AutoReconnectConfig?.Value ?? true;
                    int maxAttempts = Math.Clamp(MaxReconnectAttemptsConfig?.Value ?? 5, 1, 10);
                    if (autoReconnect && !wasHost && reason != DisconnectReason.GracefulDisconnect && reason != DisconnectReason.None)
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
                    SessionState?.RecordReceived(length);
                    MenuUI?.Diagnostics?.RecordReceived(length);

                    if (length < PacketHeader.HeaderSize) return;

                    var span = new ReadOnlySpan<byte>(data, offset, length);
                    if (!PacketHeader.TryReadFrom(span, out var header)) return;

                    if (!header.IsValidPacketType) return; // Drop packets with unknown packet type

                    var reader = new PacketReader(span);
                    switch (header.PacketId)
                    {
                        case PacketType.TransformSync:
                            var trans = TransformSyncPacket.Deserialize(ref reader);
                            CloneSpawner?.UpdateRemoteTransform(
                                peer,
                                trans.PositionX, trans.PositionY,
                                trans.VelocityX, trans.VelocityY,
                                trans.FacingDirection,
                                trans.StateFlags,
                                trans.AnimationClipHash,
                                trans.NormalizedTime,
                                header.Timestamp);
                            break;

                        case PacketType.ActionTrigger:
                            var action = ActionTriggerPacket.Deserialize(ref reader);
                            CloneSpawner?.TriggerRemoteAction(peer, action.Action, (byte)action.Parameter);
                            break;

                        case PacketType.VitalsSync:
                            var vitals = VitalsSyncPacket.Deserialize(ref reader);
                            VitalsSync?.HandleVitalsPacket(peer, vitals);
                            break;

                        case PacketType.CurrencySync:
                            var currency = CurrencySyncPacket.Deserialize(ref reader);
                            VitalsSync?.HandleCurrencyPacket(peer, currency);
                            break;

                        case PacketType.PlayerDeath:
                            var death = DeathLifecyclePacket.Deserialize(ref reader);
                            DeathCoordinator?.HandleDeathPacket(peer, death);
                            break;

                        case PacketType.ItemUnlockSync:
                            var item = ItemUnlockPacket.Deserialize(ref reader);
                            InventorySync?.HandleItemUnlockPacket(peer, item);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[Coop] Error processing packet from {peer}: {ex.Message}");
                }
            };
        }

        /// <summary>
        /// Broadcasts the local hero's authoritative position, velocity, state flags and
        /// animation state to the remote peer at the configured tick rate.
        /// Without this the remote clone would spawn but never move, which is the
        /// root cause of the "connected but partner is frozen/invisible" reports.
        /// </summary>
        private void BroadcastLocalTransform(float deltaTime)
        {
            if (ActiveTransport == null) return;
            if (SessionState?.IsConnected != true) return;
            if (!SessionState.RemotePeerId.HasValue) return;

            // Do not stream transforms while we are spectating or during a team wipe;
            // the local hero is hidden/disabled and its position is meaningless.
            if (SessionState.Status == SessionStatus.Spectating || SessionState.Status == SessionStatus.TeamWipe)
            {
                return;
            }

            var hero = HeroController.instance;
            if (hero == null || hero.gameObject == null) return;

            var state = hero.cState;
            if (state == null || state.dead || state.hazardDeath || state.hazardRespawning) return;

            int tickHz = Math.Clamp(TickRateHzConfig?.Value ?? 30, 20, 60);
            float interval = 1f / tickHz;
            _transformSendAccumulator += deltaTime;
            if (_transformSendAccumulator < interval) return;

            // Cap catch-up so a long frame cannot produce a burst of packets.
            _transformSendAccumulator = Math.Min(_transformSendAccumulator - interval, interval);

            try
            {
                var position = hero.transform.position;

                float velocityX = 0f;
                float velocityY = 0f;
                var body = hero.GetComponent<Rigidbody2D>();
                if (body != null)
                {
                    Vector2 velocity = body.linearVelocity;
                    velocityX = velocity.x;
                    velocityY = velocity.y;
                }

                ushort flags = 0;
                if (state.onGround) flags |= TransformSyncPacket.FlagGrounded;
                if (state.dashing || state.airDashing || state.shadowDashing || state.superDashing)
                    flags |= TransformSyncPacket.FlagDashing;
                if (state.wallSliding) flags |= TransformSyncPacket.FlagWallSliding;
                if (state.jumping || state.doubleJumping || state.wallJumping)
                    flags |= TransformSyncPacket.FlagJumping;
                if (state.falling) flags |= TransformSyncPacket.FlagFalling;
                if (state.attacking) flags |= TransformSyncPacket.FlagAttacking;
                if (state.invulnerable) flags |= TransformSyncPacket.FlagInvulnerable;
                if (state.swimming) flags |= TransformSyncPacket.FlagSwimming;

                int clipHash = 0;
                ushort normalizedTime = 0;
                var animator = hero.AnimCtrl?.animator;
                var clip = animator?.CurrentClip;
                if (clip != null)
                {
                    clipHash = AnimationClipHasher.ComputeHash(clip.name);
                    float duration = clip.Duration;
                    if (duration > 0f)
                    {
                        normalizedTime = AnimationClipHasher.EncodeNormalizedTime(animator!.ClipTimeSeconds / duration);
                    }
                }

                _transformTick++;
                var packet = new TransformSyncPacket
                {
                    // Session-relative milliseconds; the receiver remaps this to its own
                    // local clock so interpolation is not tied to machine uptime.
                    Header = new PacketHeader(PacketType.TransformSync, 0, _transformTick, (uint)(Time.timeAsDouble * 1000.0)),
                    PositionX = position.x,
                    PositionY = position.y,
                    VelocityX = velocityX,
                    VelocityY = velocityY,
                    FacingDirection = (byte)(state.facingRight ? 1 : 0),
                    StateFlags = flags,
                    AnimationClipHash = clipHash,
                    NormalizedTime = normalizedTime
                };

                Span<byte> buffer = stackalloc byte[64];
                var writer = new PacketWriter(buffer);
                packet.Serialize(ref writer);
                ActiveTransport.Send(
                    SessionState.RemotePeerId.Value,
                    buffer.ToArray(),
                    0,
                    writer.Position,
                    DeliveryMethod.Unreliable,
                    0);
                MenuUI?.Diagnostics?.RecordSent(writer.Position);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Failed broadcasting local transform: {ex.Message}");
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
                            _reconnectPending = true;
                            _nextReconnectTime = Time.unscaledTime + 4f;
                        }
                    }
                }

                // Process pending Steam lobby join from command line
                if (_pendingSteamLobbyJoin.HasValue && SteamTransport != null)
                {
                    try
                    {
                        SteamTransport.Connect(_pendingSteamLobbyJoin.Value.ToString());
                        SetActiveTransport(SteamTransport);
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

                // Stream our own transform to the partner (this was missing entirely).
                BroadcastLocalTransform(Time.deltaTime);

                if (SessionState?.IsConnected == true && SessionState.RemotePeerId.HasValue)
                {
                    if (CloneSpawner != null && CloneSpawner.TryGetRemotePosition(SessionState.RemotePeerId.Value, out float posX, out float posY))
                    {
                        if (DeathCoordinator != null)
                        {
                            DeathCoordinator.LastPartnerPosition = (posX, posY);

                            // If spectating, tether camera target to remote partner position
                            if (DeathCoordinator.CurrentState == DeathState.Spectating)
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

                DeathCoordinator?.UpdateTick(Time.deltaTime);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Coop] Exception in Update: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void OnDestroy()
        {
            try
            {
                // Disconnect both transports
                ActiveTransport?.Disconnect();
                CancelReconnect();
                if (SteamTransport != null && ActiveTransport != SteamTransport)
                {
                    SteamTransport.Disconnect();
                }
                
                _harmony?.UnpatchSelf();
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Coop] Exception in OnDestroy: {ex.Message}");
            }
            finally
            {
                Instance = null;
            }
        }
    }
}
