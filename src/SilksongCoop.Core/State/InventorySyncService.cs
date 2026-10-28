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
        private readonly List<string> _localEquippedTools = new List<string>();
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

        public event Action<string, ItemCategory>? OnItemUnlocked;
        public event Action<ItemUnlockPacket>? OnOutgoingItemUnlockPacket;

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
        /// Handles a local item discovery by numeric ID.
        /// </summary>
        /// <param name="itemNumericId">Numeric item identifier.</param>
        /// <param name="category">Item category.</param>
        /// <param name="transactionId">Optional transaction ID for deduplication.</param>
        public void OnLocalItemDiscovered(int itemNumericId, ItemCategory category, Guid? transactionId = null)
        {
            OnLocalItemDiscovered(itemNumericId.ToString(), category, transactionId);
        }

        /// <summary>
        /// Handles a local item discovery by string ID.
        /// </summary>
        /// <param name="itemId">String item identifier.</param>
        /// <param name="category">Item category.</param>
        /// <param name="transactionId">Optional transaction ID for deduplication.</param>
        public void OnLocalItemDiscovered(string itemId, ItemCategory category, Guid? transactionId = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(itemId)) return;
                if (itemId.Length > 256 || Encoding.UTF8.GetByteCount(itemId) > 400) return;

                var tx = transactionId ?? Guid.NewGuid();
                if (tx == Guid.Empty) return;

                uint seq;
                uint ts = (uint)Environment.TickCount;

                lock (_syncLock)
                {
                    if (_processedTransactions.Contains(tx)) return;
                    if (!_unlockedItems.Add(itemId)) return;

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

                OnOutgoingItemUnlockPacket?.Invoke(packet);
            }
            catch (Exception ex)
            {
                Log($"[InventorySyncService] OnLocalItemDiscovered error: {ex.Message}");
            }
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
                uint ts = (uint)Environment.TickCount;

                lock (_syncLock)
                {
                    if (_processedTransactions.Contains(tx)) return;

                    RecordTransaction(tx);
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
        {
            try
            {
                if (string.IsNullOrWhiteSpace(packet.ItemId)) return;
                if (packet.ItemId.Length > 256 || Encoding.UTF8.GetByteCount(packet.ItemId) > 400) return;
                if (packet.TransactionId == Guid.Empty) return;

                bool isNewItem;
                lock (_syncLock)
                {
                    if (!_processedTransactions.Add(packet.TransactionId))
                    {
                        return; // Duplicate transaction (retransmit/echo drop)
                    }

                    RecordTransactionOrder(packet.TransactionId);
                    // BUG-B5: counter pseudo-items must not be deduplicated by item id —
                    // the same value recurs legitimately across upgrade cycles.
                    isNewItem = IsCounterItemId(packet.ItemId) || _unlockedItems.Add(packet.ItemId);
                }

                if (isNewItem)
                {
                    OnItemUnlocked?.Invoke(packet.ItemId, packet.Category);
                    OnRemoteItemUnlocked?.Invoke(packet.ItemId, packet.Category);
                }
                // Note: Never raise OnOutgoingItemUnlockPacket here. Prevents echo loops.
            }
            catch (Exception ex)
            {
                Log($"[InventorySyncService] HandleItemUnlockPacket error: {ex.Message}");
            }
        }

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
                return _processedTransactions.Contains(transactionId);
            }
        }

        #endregion

        #region Private Methods

        private static void Log(string msg) => System.Diagnostics.Debug.WriteLine(msg);

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

        #endregion
    }
}
