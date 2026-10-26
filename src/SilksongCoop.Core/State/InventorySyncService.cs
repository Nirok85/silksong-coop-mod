using System;
using System.Collections.Generic;
using System.Text;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;

namespace SilksongCoop.Core.State
{
    public class InventorySyncService : IInventorySyncService
    {
        private readonly HashSet<string> _unlockedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Guid> _processedTransactions = new HashSet<Guid>();
        private readonly Queue<Guid> _transactionOrder = new Queue<Guid>();
        private readonly List<string> _localEquippedTools = new List<string>();
        private readonly object _syncLock = new object();
        private const int MaxTrackedTransactions = 500;
        private uint _sequenceCounter = 0;
        private string _localEquippedCrest = string.Empty;

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

        public void OnLocalItemDiscovered(int itemNumericId, ItemCategory category, Guid? transactionId = null)
        {
            OnLocalItemDiscovered(itemNumericId.ToString(), category, transactionId);
        }

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
                System.Diagnostics.Debug.WriteLine($"[InventorySyncService] OnLocalItemDiscovered error: {ex.Message}");
            }
        }

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
                    isNewItem = _unlockedItems.Add(packet.ItemId);
                }

                if (isNewItem)
                {
                    OnItemUnlocked?.Invoke(packet.ItemId, packet.Category);
                }
                // Note: Never raise OnOutgoingItemUnlockPacket here. Prevents echo loops.
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[InventorySyncService] HandleItemUnlockPacket error: {ex.Message}");
            }
        }

        public void SetLocalEquippedCrest(string crestId)
        {
            lock (_syncLock)
            {
                _localEquippedCrest = crestId ?? string.Empty;
            }
        }

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

        public bool IsItemUnlocked(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId)) return false;
            lock (_syncLock)
            {
                return _unlockedItems.Contains(itemId);
            }
        }

        public bool IsTransactionProcessed(Guid transactionId)
        {
            if (transactionId == Guid.Empty) return false;
            lock (_syncLock)
            {
                return _processedTransactions.Contains(transactionId);
            }
        }

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
                System.Diagnostics.Debug.WriteLine($"[InventorySyncService] RecordTransactionOrder error: {ex.Message}");
            }
        }
    }
}
