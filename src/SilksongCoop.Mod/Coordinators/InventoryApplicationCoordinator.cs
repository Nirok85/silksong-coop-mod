using System;
using System.Collections.Generic;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using SilksongCoop.Mod.Services;
using SilksongCoop.Mod.State;
using UnityEngine;

namespace SilksongCoop.Mod.Coordinators
{
    /// <summary>
    /// Coordinates applying item unlocks to local PlayerData, merging heart and silk fragment baselines,
    /// and managing bounded retry queues for inventory unlocks and currency receipts.
    /// Implements IInventoryGameplayActions to decouple inventory application from the main plugin.
    /// </summary>
    public sealed class InventoryApplicationCoordinator : IInventoryGameplayActions, ISyncOrchestrator, IDisposable
    {
        private readonly ISessionService _sessionService;
        private readonly IInventorySyncService _inventorySync;
        private readonly IVitalsSyncService _vitalsSync;
        private readonly Func<INetworkTransport?> _transportProvider;
        private readonly Func<INetworkDiagnostics?> _diagnosticsProvider;

        private readonly Queue<(NetworkPeerId Peer, ItemUnlockPacket Packet, float Deadline)> _inventoryRetries = new();
        private readonly HashSet<(NetworkPeerId, Guid)> _inventoryRetryIds = new();
        private readonly Queue<(NetworkPeerId Peer, Guid TransactionId, float Deadline)> _pendingCurrencyReceipts = new();
        private readonly HashSet<(NetworkPeerId, Guid)> _pendingCurrencyReceiptIds = new();

        private int _baseMaxHealth = -1;
        private int _baseMaxSilkSpools = -1;
        private int _lastHeartPiecesBroadcast = -1;
        private int _lastSilkPartsBroadcast = -1;
        private bool _pendingHeartBaseline;
        private bool _pendingSilkBaseline;

        public int BaseMaxHealth { get => _baseMaxHealth; set => _baseMaxHealth = value; }
        public int BaseMaxSilkSpools { get => _baseMaxSilkSpools; set => _baseMaxSilkSpools = value; }

        public InventoryApplicationCoordinator(
            ISessionService sessionService,
            IInventorySyncService inventorySync,
            IVitalsSyncService vitalsSync,
            Func<INetworkTransport?> transportProvider,
            Func<INetworkDiagnostics?> diagnosticsProvider)
        {
            _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
            _inventorySync = inventorySync ?? throw new ArgumentNullException(nameof(inventorySync));
            _vitalsSync = vitalsSync ?? throw new ArgumentNullException(nameof(vitalsSync));
            _transportProvider = transportProvider ?? throw new ArgumentNullException(nameof(transportProvider));
            _diagnosticsProvider = diagnosticsProvider ?? throw new ArgumentNullException(nameof(diagnosticsProvider));
        }

        public void Initialize() { }

        public void OnSessionStarted(bool isHost)
        {
            ClearInventoryRetries();
            _baseMaxHealth = -1;
            _baseMaxSilkSpools = -1;
            _lastHeartPiecesBroadcast = -1;
            _lastSilkPartsBroadcast = -1;
            _pendingHeartBaseline = false;
            _pendingSilkBaseline = false;
        }

        public void OnSessionEnded()
        {
            ClearInventoryRetries();
            _baseMaxHealth = -1;
            _baseMaxSilkSpools = -1;
            _lastHeartPiecesBroadcast = -1;
            _lastSilkPartsBroadcast = -1;
            _pendingHeartBaseline = false;
            _pendingSilkBaseline = false;
        }

        public void Dispose()
        {
            ClearInventoryRetries();
        }

        public void Update()
        {
            PumpInventoryRetries();
            PumpCurrencyReceipts();
            PumpFragmentBaselines();
        }

        public void ApplyRemoteItemUnlock(string itemId, ItemCategory category)
            => TryApplyRemoteItemUnlock(itemId, category);

        public bool TryApplyRemoteItemUnlock(string itemId, ItemCategory category)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                SilksongCoopPlugin.LogWarn("[InventoryCoordinator] ApplyRemoteItemUnlock called with null or empty itemId");
                return true;
            }

            try
            {
                using (RemoteApplyScope.Begin())
                {
                    var playerData = PlayerData.instance;
                    if (playerData == null)
                    {
                        SilksongCoopPlugin.LogWarn("[InventoryCoordinator] Cannot apply remote item unlock: PlayerData.instance is null");
                        return false;
                    }

                    // Partial health and silk fragment synchronization.
                    // Baselines max-merge a coherent maximum/partial pair. Subsequent
                    // packets carry acquisition deltas, excluding native conversion resets.
                    if (itemId.StartsWith("heartPiecesInit:") || itemId.StartsWith("silkPartsInit:"))
                    {
                        bool health = itemId.StartsWith("heartPiecesInit:");
                        string prefix = health ? "heartPiecesInit:" : "silkPartsInit:";
                        string[] values = itemId.Substring(prefix.Length).Split(':');
                        int units = health ? 4 : 2;
                        int limit = health ? PacketValidator.MaxReasonableMaxHealth : PacketValidator.MaxReasonableSilkSpools;
                        if (values.Length != 2 || !int.TryParse(values[0], out int maximum) ||
                            !int.TryParse(values[1], out int partial) || maximum < (health ? 5 : 9) ||
                            maximum > limit || partial < 0 || partial >= units) return true;
                        if (HeroController.instance == null) return false;
                        int current = health ? playerData.maxHealthBase : playerData.silkMax;
                        int earned = health ? _baseMaxHealth : _baseMaxSilkSpools;
                        if (earned < 0) earned = current;
                        int localPartial = health ? playerData.heartPieces : playerData.silkSpoolParts;
                        int shared = health ? _vitalsSync.SharedMaxHealth : _vitalsSync.SharedMaxSilkSpools;
                        if (!Core.Progression.FragmentProgress.TryMergeBaseline(ref earned, localPartial,
                                maximum, partial, units, current, shared, limit, out var progress)) return false;
                        for (int i = current; i < progress.Maximum; i++)
                        {
                            if (health) HeroController.instance.AddToMaxHealth(1);
                            else HeroController.instance.AddToMaxSilk(1);
                        }
                        if (health)
                        {
                            if (progress.Maximum > _vitalsSync.SharedMaxHealth) _vitalsSync.OnLocalMaxHealthUpgrade(progress.Maximum);
                            playerData.heartPieces = progress.Partial;
                            _baseMaxHealth = earned;
                            _lastHeartPiecesBroadcast = progress.Partial;
                        }
                        else
                        {
                            if (progress.Maximum > _vitalsSync.SharedMaxSilkSpools) _vitalsSync.OnLocalMaxSilkUpgrade(progress.Maximum);
                            playerData.silkSpoolParts = progress.Partial;
                            _baseMaxSilkSpools = earned;
                            _lastSilkPartsBroadcast = progress.Partial;
                        }
                        return true;
                    }
                    if (itemId.StartsWith("heartPieces:"))
                    {
                        if (int.TryParse(itemId.Substring("heartPieces:".Length), out int value))
                        {
                            bool isDelta = itemId["heartPieces:".Length] == '+' || itemId["heartPieces:".Length] == '-';
                            if (isDelta && (value < -8 || value > 8))
                            {
                                SilksongCoopPlugin.LogWarn($"[InventoryCoordinator] Rejected heart-piece delta {value}");
                                return true;
                            }

                            if (HeroController.instance == null) return false;
                            int currentMax = playerData.maxHealthBase;
                            int earned = _baseMaxHealth < 0 ? currentMax : _baseMaxHealth;
                            int delta = isDelta ? value : Math.Max(0, Math.Clamp(value, 0, 3) - playerData.heartPieces);
                            if (!Core.Progression.FragmentProgress.TryApply(ref earned, playerData.heartPieces, delta,
                                    4, currentMax, _vitalsSync.SharedMaxHealth, PacketValidator.MaxReasonableMaxHealth, out var progress)) return false;
                            for (int i = currentMax; i < progress.Maximum; i++) HeroController.instance.AddToMaxHealth(1);
                            if (progress.Maximum > _vitalsSync.SharedMaxHealth) _vitalsSync.OnLocalMaxHealthUpgrade(progress.Maximum);
                            int next = progress.Partial;
                            playerData.heartPieces = next;
                            _baseMaxHealth = earned;
                            _lastHeartPiecesBroadcast = next;
                        }
                        return true;
                    }
                    if (itemId.StartsWith("silkParts:"))
                    {
                        if (int.TryParse(itemId.Substring("silkParts:".Length), out int value))
                        {
                            bool isDelta = itemId["silkParts:".Length] == '+' || itemId["silkParts:".Length] == '-';
                            if (isDelta && (value < -4 || value > 4))
                            {
                                SilksongCoopPlugin.LogWarn($"[InventoryCoordinator] Rejected silk-part delta {value}");
                                return true;
                            }

                            if (HeroController.instance == null) return false;
                            int currentMax = playerData.silkMax;
                            int earned = _baseMaxSilkSpools < 0 ? currentMax : _baseMaxSilkSpools;
                            int delta = isDelta ? value : Math.Max(0, Math.Clamp(value, 0, 1) - playerData.silkSpoolParts);
                            if (!Core.Progression.FragmentProgress.TryApply(ref earned, playerData.silkSpoolParts, delta,
                                    2, currentMax, _vitalsSync.SharedMaxSilkSpools, PacketValidator.MaxReasonableSilkSpools, out var progress)) return false;
                            for (int i = currentMax; i < progress.Maximum; i++) HeroController.instance.AddToMaxSilk(1);
                            if (progress.Maximum > _vitalsSync.SharedMaxSilkSpools) _vitalsSync.OnLocalMaxSilkUpgrade(progress.Maximum);
                            int next = progress.Partial;
                            playerData.silkSpoolParts = next;
                            _baseMaxSilkSpools = earned;
                            _lastSilkPartsBroadcast = next;
                        }
                        return true;
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
                                SilksongCoopPlugin.LogError($"[InventoryCoordinator] Failed to apply crest unlock '{itemId}': {ex.Message}");
                                return false;
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
                                SilksongCoopPlugin.LogError($"[InventoryCoordinator] Failed to apply tool unlock '{itemId}': {ex.Message}");
                                return false;
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
                                else if (Hooks.InventoryHooks.IsAllowedSaveBool(itemId, category))
                                {
                                    playerData.SetBool(itemId, true);
                                }
                                else
                                {
                                    SilksongCoopPlugin.LogWarn($"[InventoryCoordinator] Rejected unauthorized {category} save bool unlock: '{itemId}'");
                                }
                            }
                            catch (Exception ex)
                            {
                                SilksongCoopPlugin.LogError($"[InventoryCoordinator] Failed to apply ability unlock '{itemId}': {ex.Message}");
                                return false;
                            }
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogError($"[InventoryCoordinator] Failed applying remote item unlock for {itemId} ({category}): {ex.Message}");
                return false;
            }
            return true;
        }

        public void ProcessRemoteUnlock(NetworkPeerId peer, ItemUnlockPacket packet)
        {
            var key = (peer, packet.TransactionId);
            if (_inventoryRetryIds.Contains(key)) return;
            if (_inventorySync is InventorySyncService concrete)
            {
                if (concrete.TryHandleItemUnlockPacket(peer, packet)) return;
            }
            else
            {
                _inventorySync.HandleItemUnlockPacket(peer, packet);
                return;
            }

            if (_inventoryRetries.Count >= 4096)
            {
                SilksongCoopPlugin.LogWarn("[InventoryCoordinator] Inventory retry queue exhausted; disconnecting before losing progression.");
                SilksongCoopPlugin.Instance?.DisconnectFromSession(DisconnectReason.ConnectionFailed);
                return;
            }
            _inventoryRetryIds.Add(key);
            _inventoryRetries.Enqueue((peer, packet, Time.realtimeSinceStartup + 30f));
        }

        private static bool ContainsPeer(IReadOnlyList<NetworkPeerId>? peers, NetworkPeerId peer)
        {
            if (peers == null) return false;
            for (int i = 0; i < peers.Count; i++)
            {
                if (peers[i] == peer) return true;
            }
            return false;
        }

        private void PumpInventoryRetries()
        {
            int budget = Math.Min(32, _inventoryRetries.Count);
            while (budget-- > 0)
            {
                var item = _inventoryRetries.Dequeue();
                var session = _sessionService.SessionState;
                if (session == null || !ContainsPeer(session.RemotePeerIds, item.Peer) ||
                    (_inventorySync is InventorySyncService concrete && concrete.TryHandleItemUnlockPacket(item.Peer, item.Packet)))
                {
                    _inventoryRetryIds.Remove((item.Peer, item.Packet.TransactionId));
                    continue;
                }
                if (Time.realtimeSinceStartup >= item.Deadline)
                {
                    SilksongCoopPlugin.LogWarn("[InventoryCoordinator] Inventory apply failed for 30 seconds; reconnect after the game is ready.");
                    ClearInventoryRetries();
                    SilksongCoopPlugin.Instance?.DisconnectFromSession(DisconnectReason.ConnectionFailed);
                    return;
                }
                _inventoryRetries.Enqueue(item);
            }
        }

        public void EnqueueCurrencyReceipt(NetworkPeerId peer, Guid transactionId)
        {
            if (_pendingCurrencyReceipts.Count < 4096 &&
                _pendingCurrencyReceiptIds.Add((peer, transactionId)))
            {
                _pendingCurrencyReceipts.Enqueue((peer, transactionId, Time.realtimeSinceStartup + 30f));
            }
        }

        private void PumpCurrencyReceipts()
        {
            int budget = Math.Min(32, _pendingCurrencyReceipts.Count);
            var transport = _transportProvider();
            var session = _sessionService.SessionState;
            while (budget-- > 0 && _pendingCurrencyReceipts.Count > 0)
            {
                var item = _pendingCurrencyReceipts.Dequeue();
                _pendingCurrencyReceiptIds.Remove((item.Peer, item.TransactionId));
                if (transport == null || session?.IsConnected != true ||
                    !ContainsPeer(session.RemotePeerIds, item.Peer))
                {
                    continue;
                }
                if (PacketSender.SendTo(transport, item.Peer, new CurrencyReceiptPacket
                {
                    Header = new PacketHeader(PacketType.CurrencyReceipt, NetworkChannels.Currency, 0, NetworkClock.NowMs),
                    TransactionId = item.TransactionId
                }, DeliveryMethod.ReliableOrdered, NetworkChannels.Currency, PacketHeader.HeaderSize + 16, _diagnosticsProvider()))
                {
                    continue;
                }
                if (Time.realtimeSinceStartup < item.Deadline)
                {
                    if (_pendingCurrencyReceipts.Count < 4096 && _pendingCurrencyReceiptIds.Add((item.Peer, item.TransactionId)))
                        _pendingCurrencyReceipts.Enqueue((item.Peer, item.TransactionId, item.Deadline));
                }
                else
                {
                    SilksongCoopPlugin.LogWarn("[InventoryCoordinator] Currency receipt could not be delivered before its retry deadline; leaving the transaction unconfirmed.");
                }
            }
        }

        public void ClearInventoryRetries()
        {
            _inventoryRetries.Clear();
            _inventoryRetryIds.Clear();
            _pendingCurrencyReceipts.Clear();
            _pendingCurrencyReceiptIds.Clear();
        }

        public void NoteLocalHealthUpgrade(int amount) => Core.Progression.FragmentProgress.NoteLocal(
            ref _baseMaxHealth, amount, PlayerData.instance != null ? PlayerData.instance.maxHealthBase : 5, PacketValidator.MaxReasonableMaxHealth);

        public void NoteLocalSilkUpgrade(int amount) => Core.Progression.FragmentProgress.NoteLocal(
            ref _baseMaxSilkSpools, amount, PlayerData.instance != null ? PlayerData.instance.silkMax : 9, PacketValidator.MaxReasonableSilkSpools);

        public void BroadcastHeartPieces(int count)
        {
            BroadcastCounterDelta("heartPieces", "heartPiecesInit", count, ref _lastHeartPiecesBroadcast);
        }

        public void BroadcastHeartPiecesBaseline(int count)
        {
            BroadcastCounterBaseline("heartPiecesInit", count, ref _lastHeartPiecesBroadcast);
        }

        public void BroadcastSilkParts(int count)
        {
            BroadcastCounterDelta("silkParts", "silkPartsInit", count, ref _lastSilkPartsBroadcast);
        }

        public void BroadcastSilkPartsBaseline(int count)
        {
            BroadcastCounterBaseline("silkPartsInit", count, ref _lastSilkPartsBroadcast);
        }

        private void BroadcastCounterBaseline(string prefix, int count, ref int lastBroadcast)
        {
            try
            {
                var session = _sessionService.SessionState;
                if (session?.IsConnected != true) return;
                var pd = PlayerData.instance;
                if (pd == null) return;
                bool health = prefix == "heartPiecesInit";
                int units = health ? 4 : 2;
                if (count < 0 || count >= units)
                {
                    if (health) _pendingHeartBaseline = true;
                    else _pendingSilkBaseline = true;
                    return;
                }
                lastBroadcast = count;
                int maximum = health ? pd.maxHealthBase : pd.silkMax;
                if (_inventorySync is InventorySyncService concrete)
                    concrete.OnLocalCounterChanged($"{prefix}:{maximum}:{count}", ItemCategory.KeyItem);
                if (health) _pendingHeartBaseline = false;
                else _pendingSilkBaseline = false;
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[InventoryCoordinator] Failed broadcasting counter baseline: {ex.Message}");
            }
        }

        private void PumpFragmentBaselines()
        {
            var session = _sessionService.SessionState;
            if (session?.IsConnected != true)
            {
                _pendingHeartBaseline = false;
                _pendingSilkBaseline = false;
                return;
            }
            if (PlayerData.instance == null) return;
            if (_pendingHeartBaseline) BroadcastHeartPiecesBaseline(PlayerData.instance.heartPieces);
            if (_pendingSilkBaseline) BroadcastSilkPartsBaseline(PlayerData.instance.silkSpoolParts);
        }

        private void BroadcastCounterDelta(string prefix, string initPrefix, int count, ref int lastBroadcast)
        {
            try
            {
                var session = _sessionService.SessionState;
                if (session?.IsConnected != true) return;
                if (lastBroadcast < 0 ||
                    (prefix == "heartPieces" ? _pendingHeartBaseline : _pendingSilkBaseline))
                {
                    BroadcastCounterBaseline(initPrefix, count, ref lastBroadcast);
                    return;
                }
                lastBroadcast = count;
                if (_inventorySync is InventorySyncService concrete)
                    concrete.OnLocalCounterChanged($"{prefix}:{count}", ItemCategory.KeyItem);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[InventoryCoordinator] Failed broadcasting counter delta: {ex.Message}");
            }
        }

        public void BroadcastInitialInventory(NetworkPeerId? targetPeer = null)
        {
            try
            {
                var pd = PlayerData.instance;
                if (pd == null || _inventorySync == null) return;

                // Collect local items to unlock
                var itemsToBroadcast = new List<(string Name, ItemCategory Category)>();

                // 1. Shared ability bools
                foreach (var boolName in Hooks.InventoryHooks.GetValidatedAbilityBools())
                {
                    if (pd.GetBool(boolName))
                    {
                        itemsToBroadcast.Add((boolName, ItemCategory.Ability));
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
                                itemsToBroadcast.Add((tool.name, ItemCategory.Tool));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    SilksongCoopPlugin.LogWarn($"[InventoryCoordinator] Tool enumeration fallback: {ex.Message}");
                }

                // 3. All unlocked crests & Current crest
                try
                {
                    var allCrests = ToolItemManager.GetAllCrests();
                    if (allCrests != null)
                    {
                        foreach (var crest in allCrests)
                        {
                            if (crest != null && crest.IsUnlocked && !string.IsNullOrEmpty(crest.name))
                            {
                                itemsToBroadcast.Add((crest.name, ItemCategory.Crest));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    SilksongCoopPlugin.LogWarn($"[InventoryCoordinator] Crest enumeration fallback: {ex.Message}");
                }

                if (!string.IsNullOrEmpty(pd.CurrentCrestID))
                {
                    itemsToBroadcast.Add((pd.CurrentCrestID, ItemCategory.Crest));
                }

                // Batch discovery into chunks of at most 20 items to avoid network queue saturation (MED-04)
                const int maxBatchSize = 20;
                for (int i = 0; i < itemsToBroadcast.Count; i += maxBatchSize)
                {
                    int count = Math.Min(maxBatchSize, itemsToBroadcast.Count - i);
                    for (int j = 0; j < count; j++)
                    {
                        var item = itemsToBroadcast[i + j];
                        if (targetPeer.HasValue && _inventorySync.HasPeerReceivedItem(targetPeer.Value, item.Name))
                            continue;

                        _inventorySync.OnLocalItemDiscovered(item.Name, item.Category, force: true);
                        if (targetPeer.HasValue)
                            _inventorySync.MarkItemKnownByPeer(targetPeer.Value, item.Name);
                    }
                }

                // 4. Heart pieces & silk parts baseline
                BroadcastHeartPiecesBaseline(pd.heartPieces);
                BroadcastSilkPartsBaseline(pd.silkSpoolParts);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[InventoryCoordinator] Failed broadcasting initial inventory: {ex.Message}");
            }
        }
    }
}
