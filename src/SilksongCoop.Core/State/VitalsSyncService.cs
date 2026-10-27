using System;
using System.Collections.Generic;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;

namespace SilksongCoop.Core.State
{
    /// <summary>
    /// Synchronizes player vitals (health, silk, upgrades) and currency between co-op peers.
    /// </summary>
    public class VitalsSyncService : IVitalsSyncService
    {
        private static void Log(string msg) => System.Diagnostics.Debug.WriteLine(msg);

        private readonly HashSet<Guid> _processedTransactions = new HashSet<Guid>();
        private readonly Queue<Guid> _transactionOrder = new Queue<Guid>();
        private readonly object _transactionLock = new object();
        private const int MaxTrackedTransactions = 500;

        public int LocalHealth { get; private set; } = 5;
        public int LocalSilk { get; private set; } = 9;
        public int SharedMaxHealth { get; private set; } = 5;
        public int SharedMaxSilkSpools { get; private set; } = 1;
        public int SharedNeedleUpgradeLevel { get; private set; } = 0;
        public int SharedRosaries { get; private set; } = 0;
        public int SharedShellShards { get; private set; } = 0;

        // Remote (partner) vitals for HUD display. Updated from incoming VitalsSync packets.
        private int _remoteHealth;
        public int RemoteHealth
        {
            get { lock (_stateLock) { return _remoteHealth; } }
            private set { lock (_stateLock) { _remoteHealth = value; } }
        }

        private int _remoteSilk;
        public int RemoteSilk
        {
            get { lock (_stateLock) { return _remoteSilk; } }
            private set { lock (_stateLock) { _remoteSilk = value; } }
        }

        private int _remoteMaxHealth;
        public int RemoteMaxHealth
        {
            get { lock (_stateLock) { return _remoteMaxHealth; } }
            private set { lock (_stateLock) { _remoteMaxHealth = value; } }
        }

        private int _remoteMaxSilk;
        public int RemoteMaxSilk
        {
            get { lock (_stateLock) { return _remoteMaxSilk; } }
            private set { lock (_stateLock) { _remoteMaxSilk = value; } }
        }

        private bool _hasRemoteData;
        public bool HasRemoteData
        {
            get { lock (_stateLock) { return _hasRemoteData; } }
            private set { lock (_stateLock) { _hasRemoteData = value; } }
        }

        private readonly object _stateLock = new object();

        public event Action<int, int>? OnRemoteVitalsObserved;
        public event Action<int>? OnSharedMaxHealthChanged;
        public event Action<int>? OnSharedMaxSilkChanged;
        public event Action<int>? OnSharedNeedleUpgradeChanged;
        public event Action<CurrencyType, int, int>? OnSharedCurrencyChanged;
        public event Action<VitalsSyncPacket>? OnOutgoingVitalsBroadcast;
        public event Action<CurrencySyncPacket>? OnOutgoingCurrencyBroadcast;

        /// <summary>
        /// Initializes the local player's vitals and currency state.
        /// </summary>
        /// <param name="initialHealth">Initial health value.</param>
        /// <param name="initialMaxHealth">Initial maximum health value.</param>
        /// <param name="initialSilk">Initial silk value.</param>
        /// <param name="initialMaxSilkSpools">Initial maximum silk spools.</param>
        /// <param name="needleLevel">Initial needle upgrade level.</param>
        /// <param name="initialRosaries">Initial rosaries currency.</param>
        /// <param name="initialShards">Initial shell shards currency.</param>
        public void InitializeState(int initialHealth, int initialMaxHealth, int initialSilk, int initialMaxSilkSpools, int needleLevel, int initialRosaries, int initialShards)
        {
            lock (_stateLock)
            {
                LocalHealth = initialHealth;
                SharedMaxHealth = initialMaxHealth;
                LocalSilk = initialSilk;
                SharedMaxSilkSpools = initialMaxSilkSpools;
                SharedNeedleUpgradeLevel = needleLevel;
            }
            lock (_transactionLock)
            {
                SharedRosaries = initialRosaries;
                SharedShellShards = initialShards;
            }
        }

        /// <summary>
        /// Handles local player taking damage.
        /// </summary>
        /// <param name="damageAmount">Amount of damage taken.</param>
        /// <param name="newHealth">New health value after damage.</param>
        public void OnLocalDamageTaken(int damageAmount, int newHealth)
        {
            lock (_stateLock) { LocalHealth = Math.Max(0, Math.Min(SharedMaxHealth > 0 ? SharedMaxHealth : int.MaxValue, newHealth)); }
            // Current health reduction is local only. We do not sync damage to partner's current HP.
            // We broadcast an informational packet so the partner UI can display teammate health bars.
            BroadcastCurrentVitals();
        }

        /// <summary>
        /// Handles local player using silk.
        /// </summary>
        /// <param name="silkUsed">Amount of silk used.</param>
        /// <param name="newSilk">New silk value after use.</param>
        public void OnLocalSilkUsed(int silkUsed, int newSilk)
        {
            lock (_stateLock) { LocalSilk = Math.Max(0, newSilk); }
            // Current silk is local only.
            BroadcastCurrentVitals();
        }

        /// <summary>
        /// Handles local player gaining health.
        /// </summary>
        /// <param name="healAmount">Amount of health gained.</param>
        /// <param name="newHealth">New health value after healing.</param>
        public void OnLocalHealthGained(int healAmount, int newHealth)
        {
            lock (_stateLock)
            {
                LocalHealth = Math.Min(SharedMaxHealth, newHealth);
            }
            BroadcastCurrentVitals();
        }

        /// <summary>
        /// Handles local player gaining silk.
        /// </summary>
        /// <param name="silkGain">Amount of silk gained.</param>
        /// <param name="newSilk">New silk value after gain.</param>
        public void OnLocalSilkGained(int silkGain, int newSilk)
        {
            lock (_stateLock)
            {
                LocalSilk = Math.Min(SharedMaxSilkSpools * 9, newSilk);
            }
            BroadcastCurrentVitals();
        }

        /// <summary>
        /// Handles local player's max health being upgraded.
        /// </summary>
        /// <param name="newMaxHealth">New maximum health value.</param>
        public void OnLocalMaxHealthUpgrade(int newMaxHealth)
        {
            bool changed;
            lock (_stateLock)
            {
                if (newMaxHealth > SharedMaxHealth)
                {
                    SharedMaxHealth = newMaxHealth;
                    changed = true;
                }
                else
                {
                    changed = false;
                }
            }
            if (changed)
            {
                OnSharedMaxHealthChanged?.Invoke(SharedMaxHealth);
                BroadcastCurrentVitals();
            }
        }

        /// <summary>
        /// Handles local player's max silk being upgraded.
        /// </summary>
        /// <param name="newMaxSilk">New maximum silk spools value.</param>
        public void OnLocalMaxSilkUpgrade(int newMaxSilk)
        {
            bool changed;
            lock (_stateLock)
            {
                if (newMaxSilk > SharedMaxSilkSpools)
                {
                    SharedMaxSilkSpools = newMaxSilk;
                    changed = true;
                }
                else
                {
                    changed = false;
                }
            }
            if (changed)
            {
                OnSharedMaxSilkChanged?.Invoke(SharedMaxSilkSpools);
                BroadcastCurrentVitals();
            }
        }

        /// <summary>
        /// Handles local player's needle being upgraded.
        /// </summary>
        /// <param name="newNeedleLevel">New needle upgrade level.</param>
        public void OnLocalNeedleUpgrade(int newNeedleLevel)
        {
            bool changed;
            lock (_stateLock)
            {
                if (newNeedleLevel > SharedNeedleUpgradeLevel)
                {
                    SharedNeedleUpgradeLevel = newNeedleLevel;
                    changed = true;
                }
                else
                {
                    changed = false;
                }
            }
            if (changed)
            {
                OnSharedNeedleUpgradeChanged?.Invoke(SharedNeedleUpgradeLevel);
                BroadcastCurrentVitals();
            }
        }

        /// <summary>
        /// Handles local player's currency changing.
        /// </summary>
        /// <param name="currency">Type of currency that changed.</param>
        /// <param name="deltaAmount">Amount of currency gained or lost.</param>
        /// <param name="newTotal">New total amount of currency.</param>
        /// <param name="transactionId">Unique transaction identifier for deduplication.</param>
        public void OnLocalCurrencyChanged(CurrencyType currency, int deltaAmount, int newTotal, Guid transactionId)
        {
            if (deltaAmount == 0) return;
            if (transactionId == Guid.Empty) return;

            int currentWallet;
            lock (_transactionLock)
            {
                if (!_processedTransactions.Add(transactionId))
                {
                    return; // Already processed
                }

                RecordTransactionOrder(transactionId);

                if (currency == CurrencyType.Rosaries)
                {
                    SharedRosaries = Math.Max(0, SharedRosaries + deltaAmount);
                    currentWallet = SharedRosaries;
                }
                else
                {
                    SharedShellShards = Math.Max(0, SharedShellShards + deltaAmount);
                    currentWallet = SharedShellShards;
                }
            }

            var packet = new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 0, (uint)Environment.TickCount),
                Currency = currency,
                TotalWalletAmount = currentWallet,
                DeltaAmount = (short)Math.Clamp(deltaAmount, short.MinValue, short.MaxValue),
                TransactionId = transactionId
            };

            OnOutgoingCurrencyBroadcast?.Invoke(packet);
        }

        /// <summary>
        /// Handles an incoming vitals sync packet from a remote peer.
        /// </summary>
        /// <param name="sender">The peer that sent the packet.</param>
        /// <param name="packet">The vitals sync packet to process.</param>
        public void HandleVitalsPacket(NetworkPeerId sender, VitalsSyncPacket packet)
        {
            try
            {
                // Store remote vitals for HUD display
                lock (_stateLock)
                {
                    RemoteHealth = packet.CurrentHealth;
                    RemoteSilk = packet.CurrentSilk;
                    RemoteMaxHealth = packet.MaxHealth;
                    RemoteMaxSilk = packet.MaxSilkSpools;
                    HasRemoteData = true;
                }

                // Update remote player observation (for HUD display)
                OnRemoteVitalsObserved?.Invoke(packet.CurrentHealth, packet.CurrentSilk);

                // Synchronize progression upgrades if remote has acquired a higher tier
                bool hpChanged, silkChanged, needleChanged;
                int newMaxHp, newMaxSilk, newNeedleLevel;
                lock (_stateLock)
                {
                    hpChanged = packet.MaxHealth > SharedMaxHealth;
                    silkChanged = packet.MaxSilkSpools > SharedMaxSilkSpools;
                    needleChanged = packet.NeedleUpgradeLevel > SharedNeedleUpgradeLevel;
                    if (hpChanged) SharedMaxHealth = packet.MaxHealth;
                    if (silkChanged) SharedMaxSilkSpools = packet.MaxSilkSpools;
                    if (needleChanged) SharedNeedleUpgradeLevel = packet.NeedleUpgradeLevel;
                    newMaxHp = SharedMaxHealth;
                    newMaxSilk = SharedMaxSilkSpools;
                    newNeedleLevel = SharedNeedleUpgradeLevel;
                }
                if (hpChanged) OnSharedMaxHealthChanged?.Invoke(newMaxHp);
                if (silkChanged) OnSharedMaxSilkChanged?.Invoke(newMaxSilk);
                if (needleChanged) OnSharedNeedleUpgradeChanged?.Invoke(newNeedleLevel);
            }
            catch (Exception ex)
            {
                Log($"[VitalsSyncService] HandleVitalsPacket error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles an incoming currency sync packet from a remote peer.
        /// </summary>
        /// <param name="sender">The peer that sent the packet.</param>
        /// <param name="packet">The currency sync packet to process.</param>
        public void HandleCurrencyPacket(NetworkPeerId sender, CurrencySyncPacket packet)
        {
            try
            {
                if (packet.TransactionId == Guid.Empty) return;

                int delta = packet.DeltaAmount;
                int updatedWallet;
                lock (_transactionLock)
                {
                    if (!_processedTransactions.Add(packet.TransactionId))
                    {
                        return; // Duplicate transaction, ignore
                    }

                    RecordTransactionOrder(packet.TransactionId);

                    if (packet.Currency == CurrencyType.Rosaries)
                    {
                        if (delta == 0)
                        {
                            // Initial sync: set absolute value
                            SharedRosaries = Math.Max(0, packet.TotalWalletAmount);
                        }
                        else
                        {
                            SharedRosaries = Math.Max(0, SharedRosaries + delta);
                        }
                        updatedWallet = SharedRosaries;
                    }
                    else
                    {
                        if (delta == 0)
                        {
                            // Initial sync: set absolute value
                            SharedShellShards = Math.Max(0, packet.TotalWalletAmount);
                        }
                        else
                        {
                            SharedShellShards = Math.Max(0, SharedShellShards + delta);
                        }
                        updatedWallet = SharedShellShards;
                    }
                }

                OnSharedCurrencyChanged?.Invoke(packet.Currency, delta, updatedWallet);
            }
            catch (Exception ex)
            {
                Log($"[VitalsSyncService] HandleCurrencyPacket error: {ex.Message}");
            }
        }

        /// <summary>
        /// Broadcasts the current local player's vitals to the remote peer.
        /// </summary>
        public void BroadcastCurrentVitals()
        {
            try
            {
                int health, silk, maxHp, maxSilk, needleLevel;
                lock (_stateLock)
                {
                    health = LocalHealth;
                    silk = LocalSilk;
                    maxHp = SharedMaxHealth;
                    maxSilk = SharedMaxSilkSpools;
                    needleLevel = SharedNeedleUpgradeLevel;
                }
                var packet = new VitalsSyncPacket
                {
                    Header = new PacketHeader(PacketType.VitalsSync, 2, 0, (uint)Environment.TickCount),
                    CurrentHealth = (byte)Math.Clamp(health, 0, 255),
                    CurrentSilk = (byte)Math.Clamp(silk, 0, 255),
                    MaxHealth = (byte)Math.Clamp(maxHp, 0, 255),
                    MaxSilkSpools = (byte)Math.Clamp(maxSilk, 0, 255),
                    NeedleUpgradeLevel = (byte)Math.Clamp(needleLevel, 0, 255)
                };

                OnOutgoingVitalsBroadcast?.Invoke(packet);
            }
            catch (Exception ex)
            {
                Log($"[VitalsSyncService] BroadcastCurrentVitals error: {ex.Message}");
            }
        }

        /// <summary>
        /// Broadcasts the current local player's currency to the remote peer.
        /// </summary>
        public void BroadcastCurrentCurrency()
        {
            try
            {
                int currentRosaries;
                int currentShards;
                lock (_transactionLock)
                {
                    currentRosaries = SharedRosaries;
                    currentShards = SharedShellShards;
                }

                if (currentRosaries > 0)
                {
                    var tx = Guid.NewGuid();
                    lock (_transactionLock)
                    {
                        _processedTransactions.Add(tx);
                        RecordTransactionOrder(tx);
                    }

                    var packet = new CurrencySyncPacket
                    {
                        Header = new PacketHeader(PacketType.CurrencySync, 3, 0, (uint)Environment.TickCount),
                        Currency = CurrencyType.Rosaries,
                        TotalWalletAmount = currentRosaries,
                        DeltaAmount = 0, // Initial sync: delta 0 to avoid double-counting on receiver
                        TransactionId = tx,
                        PickupUniqueId = 0 // Fix: Initialize field to prevent undefined behavior
                    };
                    OnOutgoingCurrencyBroadcast?.Invoke(packet);
                }

                if (currentShards > 0)
                {
                    var tx = Guid.NewGuid();
                    lock (_transactionLock)
                    {
                        _processedTransactions.Add(tx);
                        RecordTransactionOrder(tx);
                    }

                    var packet = new CurrencySyncPacket
                    {
                        Header = new PacketHeader(PacketType.CurrencySync, 3, 0, (uint)Environment.TickCount),
                        Currency = CurrencyType.ShellShards,
                        TotalWalletAmount = currentShards,
                        DeltaAmount = 0, // Initial sync: delta 0 to avoid double-counting on receiver
                        TransactionId = tx,
                        PickupUniqueId = 0 // Fix: Initialize field to prevent undefined behavior
                    };
                    OnOutgoingCurrencyBroadcast?.Invoke(packet);
                }
            }
            catch (Exception ex)
            {
                Log($"[VitalsSyncService] BroadcastCurrentCurrency error: {ex.Message}");
            }
        }

        private void RecordTransactionOrder(Guid transactionId)
        {
            _transactionOrder.Enqueue(transactionId);
            if (_transactionOrder.Count > MaxTrackedTransactions)
            {
                var oldest = _transactionOrder.Dequeue();
                _processedTransactions.Remove(oldest);
            }
        }
    }
}
