using System;
using System.Collections;
using BepInEx;
using HarmonyLib;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Revive;
using SilksongCoop.Core.State;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
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

        public INetworkTransport? ActiveTransport { get; private set; }
        public SteamP2PTransport? SteamTransport { get; private set; }
        public LiteNetLibTransport? UdpTransport { get; private set; }
        public CoopMenuUI MenuUI { get; private set; } = null!;
        public RemoteHornetSpawner CloneSpawner { get; private set; } = null!;

        private Harmony? _harmony;
        private bool _isTeamWipeInProgress;

        private void Awake()
        {
            Instance = this;
            Logger.LogInfo($"Initializing {PluginName} v{PluginVersion}...");

            SessionState = new CoopSessionState();
            VitalsSync = new VitalsSyncService();
            DeathCoordinator = new DeathLifecycleCoordinator();
            InventorySync = new InventorySyncService();

            // Setup outgoing network callbacks - use stackalloc to avoid heap allocation pressure
            VitalsSync.OnOutgoingVitalsBroadcast += packet =>
            {
                if (SessionState.RemotePeerId.HasValue && ActiveTransport != null)
                {
                    Span<byte> buffer = stackalloc byte[64];
                    var writer = new PacketWriter(buffer);
                    packet.Serialize(ref writer);
                    ActiveTransport.Send(SessionState.RemotePeerId.Value, buffer.ToArray(), 0, writer.Position, DeliveryMethod.ReliableOrdered, 2);
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

        public void SetActiveTransport(INetworkTransport transport)
        {
            ActiveTransport = transport;
        }

        public void RefreshLocalVisuals(bool isPlayer2)
        {
            try
            {
                var hero = HeroController.instance;
                if (hero != null && hero.gameObject != null)
                {
                    var palette = hero.gameObject.GetComponent<PaletteSwapController>() ?? hero.gameObject.AddComponent<PaletteSwapController>();
                    palette.SetPlayer2(isPlayer2);

                    var dash = hero.gameObject.GetComponent<DashTrailController>() ?? hero.gameObject.AddComponent<DashTrailController>();
                    dash.Initialize(isPlayer2);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Failed to refresh local visuals: {ex.Message}");
            }
        }

        public void EnterSpectatorMode()
        {
            try
            {
                var hero = HeroController.instance;
                if (hero != null)
                {
                    hero.AffectedByGravity(false);
                    hero.RelinquishControl();
                    foreach (var ren in hero.gameObject.GetComponentsInChildren<Renderer>())
                    {
                        ren.enabled = false;
                    }
                }

                // Tether CameraTarget to free mode so it can smoothly follow remote partner
                var camTarget = GameManager.instance?.cameraCtrl?.camTarget;
                camTarget?.StartFreeMode(false);

                SessionState.SetSpectating();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Failed entering spectator mode: {ex.Message}");
            }
        }

        public void ReviveAtPartner(float x, float y)
        {
            try
            {
                var hero = HeroController.instance;
                if (hero != null)
                {
                    // 1. Resolve safe ground position via GroundProber
                    Vector2 spawnPos = new Vector2(x, y + 0.5f);
                    if (GroundProber.TryFindSafeGround(x, y, out float safeX, out float safeY))
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
                        int reviveHp = GroundProber.CalculateReviveHealth(pd.maxHealth);
                        pd.health = reviveHp;
                        pd.prevHealth = pd.health;
                        pd.healthBlue = 0;
                        hero.MaxHealthKeepBlue();
                        EventRegister.SendEvent(EventRegisterEvents.HeroHealed);
                        EventRegister.SendEvent(EventRegisterEvents.HealthUpdate);
                        VitalsSync.BroadcastCurrentVitals();
                    }

                    // 5. Re-enable renderers
                    foreach (var ren in hero.gameObject.GetComponentsInChildren<Renderer>())
                    {
                        ren.enabled = true;
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
                }

                DeathCoordinator.CompleteRevival();
                SessionState.ReturnToActive();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Failed reviving at partner: {ex.Message}");
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
            _isTeamWipeInProgress = true;

            try
            {
                SessionState.SetTeamWipe();
                if (GameManager.instance != null)
                {
                    GameManager.instance.StartCoroutine(GameManager.instance.PlayerDead(1.5f));
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Failed triggering team wipe: {ex.Message}");
            }
        }

        public void OnBenchRespawnCompleted()
        {
            _isTeamWipeInProgress = false;

            var camTarget = GameManager.instance?.cameraCtrl?.camTarget;
            camTarget?.EndFreeMode();

            DeathCoordinator.ResetAfterBenchRespawn();
            SessionState.ReturnToActive();

            var hero = HeroController.instance;
            if (hero != null)
            {
                hero.cState.dead = false;
                hero.cState.hazardDeath = false;
                foreach (var ren in hero.gameObject.GetComponentsInChildren<Renderer>())
                {
                    ren.enabled = true;
                }
            }
        }

        public void ApplyRemoteItemUnlock(string itemId, ItemCategory category)
        {
            try
            {
                InventoryHooks.IsApplyingRemoteUnlock = true;

                switch (category)
                {
                    case ItemCategory.Crest:
                        var localCrest = PlayerData.instance?.CurrentCrestID;
                        var crest = ToolItemManager.GetCrestByName(itemId);
                        if (crest != null)
                        {
                            crest.Unlock();
                            if (PlayerData.instance != null && PlayerData.instance.CurrentCrestID != localCrest)
                            {
                                ToolItemManager.SetEquippedCrest(localCrest);
                            }
                        }
                        else if (PlayerData.instance?.ToolEquips != null)
                        {
                            var data = PlayerData.instance.ToolEquips.GetData(itemId);
                            data.IsUnlocked = true;
                            PlayerData.instance.ToolEquips.SetData(itemId, data);
                        }
                        break;

                    case ItemCategory.Tool:
                        var tool = ToolItemManager.GetToolByName(itemId);
                        if (tool != null)
                        {
                            tool.Unlock(null, ToolItem.PopupFlags.None);
                        }
                        else if (PlayerData.instance != null)
                        {
                            var data = PlayerData.instance.GetToolData(itemId);
                            data.IsUnlocked = true;
                            PlayerData.instance.SetToolData(itemId, data);
                        }
                        break;

                    case ItemCategory.Ability:
                    case ItemCategory.KeyItem:
                        var toolSkill = ToolItemManager.GetToolByName(itemId);
                        if (toolSkill != null)
                        {
                            toolSkill.Unlock(null, ToolItem.PopupFlags.None);
                        }
                        else if (PlayerData.instance != null)
                        {
                            PlayerData.instance.SetBool(itemId, true);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Coop] Failed applying remote item unlock for {itemId} ({category}): {ex.Message}");
            }
            finally
            {
                InventoryHooks.IsApplyingRemoteUnlock = false;
            }
        }

        private void BindTransportEvents(INetworkTransport? transport)
        {
            if (transport == null) return;

            transport.OnPeerConnected += peer =>
            {
                Logger.LogInfo($"[Coop] Peer connected: {peer}");
                SessionState.SetConnected(peer);

                bool remoteIsPlayer2 = transport.IsHost;
                CloneSpawner.SpawnRemotePlayer(peer, remoteIsPlayer2);

                RefreshLocalVisuals(!transport.IsHost);

                if (transport.IsHost)
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
            };

            transport.OnPeerDisconnected += (peer, reason) =>
            {
                Logger.LogInfo($"[Coop] Peer disconnected: {peer}, reason: {reason}");
                SessionState.SetDisconnected(reason);
                CloneSpawner.DespawnRemotePlayer(peer);
                RefreshLocalVisuals(false);
            };

            transport.OnDataReceived += (peer, data, offset, length) =>
            {
                SessionState.RecordReceived(length);

                if (length >= PacketHeader.HeaderSize)
                {
                    try
                    {
                        var span = new ReadOnlySpan<byte>(data, offset, length);
                        if (PacketHeader.TryReadFrom(span, out var header))
                        {
                            var reader = new PacketReader(span);
                            switch (header.PacketId)
                            {
                                case PacketType.TransformSync:
                                    var trans = TransformSyncPacket.Deserialize(ref reader);
                                    CloneSpawner.UpdateRemoteTransform(
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
                                    CloneSpawner.TriggerRemoteAction(peer, action.Action, (byte)action.Parameter);
                                    break;

                                case PacketType.VitalsSync:
                                    var vitals = VitalsSyncPacket.Deserialize(ref reader);
                                    VitalsSync.HandleVitalsPacket(peer, vitals);
                                    break;

                                case PacketType.CurrencySync:
                                    var currency = CurrencySyncPacket.Deserialize(ref reader);
                                    VitalsSync.HandleCurrencyPacket(peer, currency);
                                    break;

                                case PacketType.PlayerDeath:
                                    var death = DeathLifecyclePacket.Deserialize(ref reader);
                                    DeathCoordinator.HandleDeathPacket(peer, death);
                                    break;

                                case PacketType.ItemUnlockSync:
                                    var item = ItemUnlockPacket.Deserialize(ref reader);
                                    InventorySync.HandleItemUnlockPacket(peer, item);
                                    break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning($"[Coop] Error processing packet from {peer}: {ex.Message}");
                    }
                }
            };
        }

        private void Update()
        {
            ActiveTransport?.PollEvents();
            SessionState.AdvanceTick();

            if (SessionState.IsConnected && SessionState.RemotePeerId.HasValue)
            {
                if (CloneSpawner.TryGetRemotePosition(SessionState.RemotePeerId.Value, out float posX, out float posY))
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

            DeathCoordinator.UpdateTick(Time.deltaTime);
        }

        private void OnDestroy()
        {
            ActiveTransport?.Disconnect();
            _harmony?.UnpatchSelf();
        }
    }
}
