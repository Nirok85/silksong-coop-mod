using System;
using System.Collections.Generic;
using System.Text;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;

namespace SilksongCoop.Core.State
{
    /// <summary>
    /// Synchronizes item unlocks, crests, and tool loadouts between co-op peers with duplicate detection.
    /// </summary>
    public class InventorySyncService : IInventorySyncService
    {
        #region Fields

        private readonly HashSet<string> _unlockedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Guid> _processedTransactions = new HashSet<Guid>();
        private readonly Queue<Guid> _transactionOrder = new Queue<Guid>();

        // Defect 14: Counter pseudo-items (heart pieces, silk parts) recur across upgrade cycles.
        // Bounded by FIFO queue (MaxTrackedTransactions = 500) to prevent unbounded memory growth over long sessions.
        private readonly HashSet<Guid> _processedCounterTransactions = new HashSet<Guid>();
        private readonly Queue<Guid> _counterTransactionOrder = new Queue<Guid>();
        private readonly List<string> _localEquippedTools = new List<string>();
        // Sequence watermarks remain after GUID eviction; reliable ordered delivery permits
        // rejection of every older counter event without retaining unbounded GUID history.
        private readonly Dictionary<NetworkPeerId, uint> _counterSequences = new Dictionary<NetworkPeerId, uint>();
        private readonly Dictionary<NetworkPeerId, (uint Sequence, Guid Transaction)> _pendingCounterApplications =
            new Dictionary<NetworkPeerId, (uint, Guid)>();
        private readonly HashSet<(NetworkPeerId Peer, Guid Transaction)> _legacyCounterTransactions =
            new HashSet<(NetworkPeerId, Guid)>();
        private readonly Dictionary<NetworkPeerId, HashSet<string>> _peerKnownUnlockedItems =
            new Dictionary<NetworkPeerId, HashSet<string>>();
        private const int MaxCounterPeers = 64;
        private const int MaxLegacyCounterTransactions = 4096;

        private readonly object _syncLock = new object();
        private const int MaxTrackedTransactions = 500;
        private uint _sequenceCounter = 0;
        private string _localEquippedCrest = string.Empty;

        #endregion

        #region Properties

        public IReadOnlyCollection<string> UnlockedItems
        {
            get
            {
                lock (_syncLock)
                {
                    return new List<string>(_unlockedItems);
                }
            }
        }

        public string LocalEquippedCrest
        {
            get
            {
                lock (_syncLock)
                {
                    return _localEquippedCrest;
                }
            }
            set
            {
                lock (_syncLock)
                {
                    _localEquippedCrest = value ?? string.Empty;
                }
            }
        }

        public IReadOnlyList<string> LocalEquippedTools
        {
            get
            {
                lock (_syncLock)
                {
                    return new List<string>(_localEquippedTools);
                }
            }
        }

        public bool IsEquippedLoadoutIsolated => true;

        public int ProcessedCounterTransactionsCount
        {
            get
            {
                lock (_syncLock)
                {
                    return _processedCounterTransactions.Count;
                }
            }
        }

        public int ProcessedTransactionsCount
        {
            get
            {
                lock (_syncLock)
                {
                    return _processedTransactions.Count;
                }
            }
        }

        public event Action<string, ItemCategory>? OnItemUnlocked;
        public event Action<ItemUnlockPacket>? OnOutgoingItemUnlockPacket;
        public event Action<NetworkPeerId?, ItemUnlockPacket>? OnOutgoingPeerItemUnlockPacket;

        /// <summary>
        /// BUG-N14: raised ONLY for unlocks received from the network. <see cref="OnItemUnlocked"/>
        /// also fires for local discoveries (its contract), so consumers that *apply* remote
        /// state to the local game must use this event — otherwise the local player's own
        /// unlock was re-applied through the remote-application path (double Unlock()).
        /// </summary>
        public event Action<string, ItemCategory>? OnRemoteItemUnlocked;

        #endregion

        #region Public Methods

        /// <summary>
        /// Optional game application callback. Return false when the game is not ready;
        /// no unlock, GUID, or sequence is committed until application succeeds.
        /// Notifications remain available for observers after successful application.
        /// </summary>
        public Func<string, ItemCategory, bool>? RemoteItemApplier { get; set; }


        /// <summary>
        /// Handles a local item discovery by numeric ID.
        /// </summary>
        /// <param name="itemNumericId">Numeric item identifier.</param>
        /// <param name="category">Item category.</param>
        /// <param name="transactionId">Optional transaction ID for deduplication.</param>
        public void OnLocalItemDiscovered(int itemNumericId, ItemCategory category, Guid? transactionId = null, bool force = false)
        {
            OnLocalItemDiscovered(itemNumericId.ToString(), category, transactionId, force);
        }

        public void OnLocalInitialItemDiscovered(int itemNumericId, ItemCategory category, Guid? transactionId = null)
        {
            OnLocalItemDiscovered(itemNumericId, category, transactionId, force: true);
        }

        /// <summary>
        /// Handles a local item discovery by string ID.
        /// </summary>
        /// <param name="itemId">String item identifier.</param>
        /// <param name="category">Item category.</param>
        /// <param name="transactionId">Optional transaction ID for deduplication.</param>
        public void OnLocalItemDiscovered(string itemId, ItemCategory category, Guid? transactionId = null, bool force = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(itemId)) return;
                if (itemId.Length > 256 || Encoding.UTF8.GetByteCount(itemId) > 400) return;

                var tx = transactionId ?? Guid.NewGuid();
                if (tx == Guid.Empty) return;

                uint seq;
                uint ts = NetworkClock.NowMs;

                lock (_syncLock)
                {
                    if (_processedTransactions.Contains(tx)) return;
                    if (force)
                    {
                        _unlockedItems.Add(itemId);
                    }
                    else
                    {
                        if (!_unlockedItems.Add(itemId)) return;
                    }

                    RecordTransaction(tx);
                    seq = ++_sequenceCounter;
                }

                OnItemUnlocked?.Invoke(itemId, category);

                var packet = new ItemUnlockPacket
                {
                    Header = new PacketHeader(PacketType.ItemUnlockSync, 3, seq, ts),
                    ItemId = itemId,
                    Category = category,
                    TransactionId = tx,
                    Timestamp = ts
                };

                OnOutgoingPeerItemUnlockPacket?.Invoke(null, packet);
                OnOutgoingItemUnlockPacket?.Invoke(packet);
            }
            catch (Exception ex)
            {
                Log($"[InventorySyncService] OnLocalItemDiscovered error: {ex.Message}");
            }
        }

        public void OnLocalInitialItemDiscovered(string itemId, ItemCategory category, Guid? transactionId = null)
        {
            OnLocalItemDiscovered(itemId, category, transactionId, force: true);
        }

        /// <summary>
        /// BUG-B5: handles a repeating counter pseudo-item (heart pieces / silk parts).
        /// Unlike regular unlocks, counter values RECUR across upgrade cycles ("heartPieces:1"
        /// is reported again after a completed mask reset the count to 0), so the
        /// once-per-item unlock dedup must not swallow them. Transaction dedup alone
        /// still protects against retransmit/echo duplicates.
        /// </summary>
        public void OnLocalCounterChanged(string itemId, ItemCategory category, Guid? transactionId = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(itemId)) return;
                if (itemId.Length > 256 || Encoding.UTF8.GetByteCount(itemId) > 400) return;

                var tx = transactionId ?? Guid.NewGuid();
                if (tx == Guid.Empty) return;

                uint seq;
                uint ts = NetworkClock.NowMs;

                lock (_syncLock)
                {
                    if (_processedCounterTransactions.Contains(tx)) return;

                    _processedCounterTransactions.Add(tx);
                    RecordCounterTransactionOrder(tx);
                    seq = ++_sequenceCounter;
                }

                var packet = new ItemUnlockPacket
                {
                    Header = new PacketHeader(PacketType.ItemUnlockSync, 3, seq, ts),
                    ItemId = itemId,
                    Category = category,
                    TransactionId = tx,
                    Timestamp = ts
                };

                OnOutgoingPeerItemUnlockPacket?.Invoke(null, packet);
                OnOutgoingItemUnlockPacket?.Invoke(packet);
            }
            catch (Exception ex)
            {
                Log($"[InventorySyncService] OnLocalCounterChanged error: {ex.Message}");
            }
        }

        /// <summary>
        /// BUG-B5: identifies counter pseudo-items whose values repeat across upgrade cycles
        /// and therefore bypass the once-per-item unlock dedup.
        /// </summary>
        private static bool IsCounterItemId(string itemId)
        {
            return itemId.StartsWith("heartPieces:", StringComparison.Ordinal)
                || itemId.StartsWith("heartPiecesInit:", StringComparison.Ordinal)
                || itemId.StartsWith("silkParts:", StringComparison.Ordinal)
                || itemId.StartsWith("silkPartsInit:", StringComparison.Ordinal);
        }

        /// <summary>
        /// Handles an incoming item unlock packet from a remote peer.
        /// </summary>
        /// <param name="sender">The peer that sent the packet.</param>
        /// <param name="packet">The item unlock packet to process.</param>
        public void HandleItemUnlockPacket(NetworkPeerId sender, ItemUnlockPacket packet)
            => TryHandleItemUnlockPacket(sender, packet);

        /// <summary>
        /// Returns false only when game application failed and the caller should retry.
        /// Invalid, duplicate, and stale packets are terminal and return true.
        /// </summary>
        public bool TryHandleItemUnlockPacket(NetworkPeerId sender, ItemUnlockPacket packet)
        {
            if (string.IsNullOrWhiteSpace(packet.ItemId)) return true;
            if (packet.ItemId.Length > 256 || Encoding.UTF8.GetByteCount(packet.ItemId) > 400) return true;
            if (packet.TransactionId == Guid.Empty) return true;

            bool isCounter = IsCounterItemId(packet.ItemId);
            lock (_syncLock)
            {
                MarkItemKnownByPeerLocked(sender, packet.ItemId);

                uint sequence = packet.Header.TickSequence;
                bool hasSequence = _counterSequences.TryGetValue(sender, out uint previousSequence);
                bool legacyCounter = isCounter && sequence == 0 && !hasSequence;
                if (isCounter && _pendingCounterApplications.TryGetValue(sender, out var pending) &&
                    (pending.Sequence != sequence || pending.Transaction != packet.TransactionId)) return false;
                if (isCounter)
                {
                    if (!hasSequence && !_pendingCounterApplications.ContainsKey(sender) &&
                        _counterSequences.Count + _pendingCounterApplications.Count >= MaxCounterPeers)
                    {
                        Log("[InventorySyncService] Counter peer history exhausted; refusing unsafe counter application.");
                        return true;
                    }
                    if (legacyCounter)
                    {
                        if (_legacyCounterTransactions.Contains((sender, packet.TransactionId))) return true;
                        if (_legacyCounterTransactions.Count >= MaxLegacyCounterTransactions)
                        {
                            Log("[InventorySyncService] Legacy counter history exhausted; refusing unsafe counter application.");
                            return true;
                        }
                    }
                    else if (hasSequence && unchecked((int)(sequence - previousSequence)) <= 0)
                        return true;
                }

                var transactionSet = isCounter ? _processedCounterTransactions : _processedTransactions;
                if (!transactionSet.Add(packet.TransactionId))
                {
                    return true; // Duplicate transaction (retransmit/echo drop)
                }

                // For regular items, if already committed and unlocked, record order and return
                if (!isCounter && _unlockedItems.Contains(packet.ItemId))
                {
                    RecordTransactionOrder(packet.TransactionId);
                    return true;
                }

                var applier = RemoteItemApplier;
                try
                {
                    // Defect 9: Dispatch handlers atomically before committing to _unlockedItems.
                    // If a handler throws (e.g. PlayerData.instance is null during scene loading),
                    // the transaction is rolled back and _unlockedItems is never dirtied,
                    // allowing concurrent or subsequent retries to succeed without losing unlocks.
                    if (applier != null)
                    {
                        if (!applier(packet.ItemId, packet.Category))
                        {
                            transactionSet.Remove(packet.TransactionId);
                            if (isCounter) _pendingCounterApplications[sender] = (sequence, packet.TransactionId);
                            return false;
                        }
                    }
                    else
                    {
                        OnItemUnlocked?.Invoke(packet.ItemId, packet.Category);
                        OnRemoteItemUnlocked?.Invoke(packet.ItemId, packet.Category);
                    }
                }
                catch (Exception ex)
                {
                    transactionSet.Remove(packet.TransactionId);
                    if (isCounter) _pendingCounterApplications[sender] = (sequence, packet.TransactionId);
                    Log($"[InventorySyncService] HandleItemUnlockPacket error: {ex.Message}");
                    return false;
                }

                if (isCounter)
                {
                    _pendingCounterApplications.Remove(sender);
                    if (legacyCounter) _legacyCounterTransactions.Add((sender, packet.TransactionId));
                    else _counterSequences[sender] = sequence;
                    RecordCounterTransactionOrder(packet.TransactionId);
                }
                else
                {
                    _unlockedItems.Add(packet.ItemId);
                    RecordTransactionOrder(packet.TransactionId);
                }

                // Observers must not roll back an already successful game mutation.
                if (applier != null)
                {
                    NotifyObservers(OnItemUnlocked, packet);
                    NotifyObservers(OnRemoteItemUnlocked, packet);
                }
                // Note: Never raise OnOutgoingItemUnlockPacket here. Prevents echo loops.
                return true;
            }
        }

        private static void NotifyObservers(Action<string, ItemCategory>? observers, ItemUnlockPacket packet)
        {
            if (observers == null) return;
            foreach (Action<string, ItemCategory> observer in observers.GetInvocationList())
            {
                try { observer(packet.ItemId, packet.Category); }
                catch (Exception ex) { Log($"[InventorySyncService] Unlock observer error: {ex.Message}"); }
            }
        }

        /// <summary>
        /// Resets the sequence for a peer whose authoritative save-instance identity changed.
        /// Ordinary reconnects must retain this watermark to keep delayed packets stale.
        /// </summary>
        public void ResetCounterPeer(NetworkPeerId peer)
        {
            lock (_syncLock)
            {
                _counterSequences.Remove(peer);
                _pendingCounterApplications.Remove(peer);
            }
        }

        /// <summary>
        /// Cancels a counter application that could not be applied and is no longer
        /// serviceable (for example because its peer disconnected while the session
        /// continued). The committed sequence watermark is deliberately retained so
        /// delayed or replayed older packets stay stale; only the unserviceable
        /// pending marker is removed.
        /// </summary>
        public void CancelPendingCounterApplication(NetworkPeerId peer)
        {
            lock (_syncLock)
            {
                _pendingCounterApplications.Remove(peer);
            }
        }

        /// <summary>
        /// Resets deduplication and sequence state for a single peer on disconnect or state reset,
        /// preserving global unlocks and other peers' state in a multi-peer session.
        /// </summary>
        public void ResetPeerState(NetworkPeerId peer)
        {
            lock (_syncLock)
            {
                _counterSequences.Remove(peer);
                _pendingCounterApplications.Remove(peer);
                _legacyCounterTransactions.RemoveWhere(t => t.Peer == peer);
                _peerKnownUnlockedItems.Remove(peer);
            }
        }

        /// <summary>
        /// Checks whether a specific peer is already known to have received an unlocked item.
        /// </summary>
        public bool HasPeerReceivedItem(NetworkPeerId peer, string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId)) return false;
            lock (_syncLock)
            {
                return _peerKnownUnlockedItems.TryGetValue(peer, out var set) && set.Contains(itemId);
            }
        }

        /// <summary>
        /// Marks an unlocked item as known by a specific peer.
        /// </summary>
        public void MarkItemKnownByPeer(NetworkPeerId peer, string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId)) return;
            lock (_syncLock)
            {
                MarkItemKnownByPeerLocked(peer, itemId);
            }
        }

        private void MarkItemKnownByPeerLocked(NetworkPeerId peer, string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId)) return;
            if (!_peerKnownUnlockedItems.TryGetValue(peer, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _peerKnownUnlockedItems[peer] = set;
            }
            set.Add(itemId);
        }

        /// <summary>
        /// Retrieves the collection of items known to have been received or unlocked by the specified peer.
        /// </summary>
        public IReadOnlyCollection<string> GetKnownItemsForPeer(NetworkPeerId peer)
        {
            lock (_syncLock)
            {
                if (_peerKnownUnlockedItems.TryGetValue(peer, out var set))
                    return new List<string>(set);
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Broadcasts initial inventory to a specific connecting peer in batches of at most 20 items.
        /// </summary>
        public void BroadcastInitialInventory(NetworkPeerId targetPeer)
            => BroadcastInitialInventory(targetPeer, 20);

        /// <summary>
        /// Broadcasts initial inventory in batches of at most maxBatchSize items.
        /// When targetPeer is specified, only sends to that peer and skips items already known by that peer.
        /// </summary>
        public void BroadcastInitialInventory(NetworkPeerId? targetPeer = null, int maxBatchSize = 20)
        {
            if (maxBatchSize <= 0) maxBatchSize = 20;
            List<string> items;
            lock (_syncLock)
            {
                items = new List<string>(_unlockedItems);
            }

            for (int i = 0; i < items.Count; i += maxBatchSize)
            {
                int count = Math.Min(maxBatchSize, items.Count - i);
                for (int j = 0; j < count; j++)
                {
                    string itemId = items[i + j];
                    if (targetPeer.HasValue && HasPeerReceivedItem(targetPeer.Value, itemId))
                        continue;

                    var tx = Guid.NewGuid();
                    uint seq;
                    uint ts = NetworkClock.NowMs;
                    lock (_syncLock)
                    {
                        seq = ++_sequenceCounter;
                        RecordTransaction(tx);
                        if (targetPeer.HasValue)
                            MarkItemKnownByPeerLocked(targetPeer.Value, itemId);
                    }

                    var packet = new ItemUnlockPacket
                    {
                        Header = new PacketHeader(PacketType.ItemUnlockSync, 3, seq, ts),
                        ItemId = itemId,
                        Category = ItemCategory.Ability,
                        TransactionId = tx,
                        Timestamp = ts
                    };

                    OnOutgoingPeerItemUnlockPacket?.Invoke(targetPeer, packet);
                    if (!targetPeer.HasValue)
                        OnOutgoingItemUnlockPacket?.Invoke(packet);
                }
            }
        }

        /// <summary>
        /// Chunks currently unlocked items into batches of at most maxBatchSize items.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<string>> CreateInitialInventoryBatches(int maxBatchSize = 20)
        {
            if (maxBatchSize <= 0) maxBatchSize = 20;
            List<string> items;
            lock (_syncLock)
            {
                items = new List<string>(_unlockedItems);
            }

            var batches = new List<IReadOnlyList<string>>();
            for (int i = 0; i < items.Count; i += maxBatchSize)
            {
                int count = Math.Min(maxBatchSize, items.Count - i);
                var batch = new List<string>(count);
                for (int j = 0; j < count; j++)
                    batch.Add(items[i + j]);
                batches.Add(batch);
            }
            return batches;
        }

        /// <summary>
        /// Clears per-session deduplication state when a new peer connects. The local
        /// PlayerData still holds every unlock; without this reset the initial inventory dump
        /// is swallowed by the once-per-item dedup and a reconnecting partner never receives
        /// abilities/tools/crests that were synced in an earlier session.
        /// </summary>
        public void ResetSessionState()
        {
            lock (_syncLock)
            {
                _unlockedItems.Clear();
                _processedTransactions.Clear();
                _transactionOrder.Clear();
                _processedCounterTransactions.Clear();
                _counterTransactionOrder.Clear();
                _counterSequences.Clear();
                _pendingCounterApplications.Clear();
                _legacyCounterTransactions.Clear();
                _peerKnownUnlockedItems.Clear();
                _sequenceCounter = 0;
            }
        }

        /// <summary>
        /// Resets deduplication state, clearing tracked transaction histories.
        /// </summary>
        public void Reset() => ResetSessionState();

        /// <summary>
        /// Sets the local player's equipped crest.
        /// </summary>
        /// <param name="crestId">The crest identifier to equip.</param>
        public void SetLocalEquippedCrest(string crestId)
        {
            lock (_syncLock)
            {
                _localEquippedCrest = crestId ?? string.Empty;
            }
        }

        /// <summary>
        /// Sets the local player's equipped tools.
        /// </summary>
        /// <param name="toolIds">Collection of tool identifiers to equip.</param>
        public void SetLocalEquippedTools(IEnumerable<string> toolIds)
        {
            lock (_syncLock)
            {
                _localEquippedTools.Clear();
                if (toolIds != null)
                {
                    _localEquippedTools.AddRange(toolIds);
                }
            }
        }

        /// <summary>
        /// Checks whether the specified item has been unlocked.
        /// </summary>
        /// <param name="itemId">The item identifier to check.</param>
        /// <returns>True if the item is unlocked; otherwise, false.</returns>
        public bool IsItemUnlocked(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId)) return false;
            lock (_syncLock)
            {
                return _unlockedItems.Contains(itemId);
            }
        }

        /// <summary>
        /// Checks whether the specified transaction has already been processed.
        /// </summary>
        /// <param name="transactionId">The transaction identifier to check.</param>
        /// <returns>True if the transaction has been processed; otherwise, false.</returns>
        public bool IsTransactionProcessed(Guid transactionId)
        {
            if (transactionId == Guid.Empty) return false;
            lock (_syncLock)
            {
                return _processedTransactions.Contains(transactionId)
                    || _processedCounterTransactions.Contains(transactionId);
            }
        }

        #endregion

        #region Private Methods

        private static void Log(string msg) => CoopLog.Warn(msg);

        private void RecordTransaction(Guid transactionId)
        {
            _processedTransactions.Add(transactionId);
            RecordTransactionOrder(transactionId);
        }

        private void RecordTransactionOrder(Guid transactionId)
        {
            try
            {
                _transactionOrder.Enqueue(transactionId);
                if (_transactionOrder.Count > MaxTrackedTransactions)
                {
                    var oldest = _transactionOrder.Dequeue();
                    _processedTransactions.Remove(oldest);
                }
            }
            catch (Exception ex)
            {
                Log($"[InventorySyncService] RecordTransactionOrder error: {ex.Message}");
            }
        }

        private void RecordCounterTransactionOrder(Guid transactionId)
        {
            try
            {
                _counterTransactionOrder.Enqueue(transactionId);
                if (_counterTransactionOrder.Count > MaxTrackedTransactions)
                {
                    var oldest = _counterTransactionOrder.Dequeue();
                    _processedCounterTransactions.Remove(oldest);
                }
            }
            catch (Exception ex)
            {
                Log($"[InventorySyncService] RecordCounterTransactionOrder error: {ex.Message}");
            }
        }

        #endregion
    }
}
