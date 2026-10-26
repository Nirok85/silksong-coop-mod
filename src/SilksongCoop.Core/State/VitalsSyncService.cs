using System;
using System.Collections.Generic;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;

namespace SilksongCoop.Core.State
{
    public class VitalsSyncService : IVitalsSyncService
    {
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

        private readonly object _stateLock = new object();

        public event Action<int, int>? OnRemoteVitalsObserved;
        public event Action<int>? OnSharedMaxHealthChanged;
        public event Action<int>? OnSharedMaxSilkChanged;
        public event Action<int>? OnSharedNeedleUpgradeChanged;
        public event Action<CurrencyType, int, int>? OnSharedCurrencyChanged;
        public event Action<VitalsSyncPacket>? OnOutgoingVitalsBroadcast;
        public event Action<CurrencySyncPacket>? OnOutgoingCurrencyBroadcast;

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

        public void OnLocalDamageTaken(int damageAmount, int newHealth)
        {
            lock (_stateLock) { LocalHealth = Math.Max(0, newHealth); }
            // Current health reduction is local only. We do not sync damage to partner's current HP.
            // We broadcast an informational packet so the partner UI can display teammate health bars.
            BroadcastCurrentVitals();
        }

        public void OnLocalSilkUsed(int silkUsed, int newSilk)
        {
            lock (_stateLock) { LocalSilk = Math.Max(0, newSilk); }
            // Current silk is local only.
            BroadcastCurrentVitals();
        }

        public void OnLocalHealthGained(int healAmount, int newHealth)
        {
            lock (_stateLock)
            {
                LocalHealth = Math.Min(SharedMaxHealth, newHealth);
            }
            BroadcastCurrentVitals();
        }

        public void OnLocalSilkGained(int silkGain, int newSilk)
        {
            lock (_stateLock)
            {
                LocalSilk = Math.Min(SharedMaxSilkSpools * 9, newSilk);
            }
            BroadcastCurrentVitals();
        }

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

        public void HandleVitalsPacket(NetworkPeerId sender, VitalsSyncPacket packet)
        {
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

        public void HandleCurrencyPacket(NetworkPeerId sender, CurrencySyncPacket packet)
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
                    SharedRosaries = Math.Max(0, SharedRosaries + delta);
                    updatedWallet = SharedRosaries;
                }
                else
                {
                    SharedShellShards = Math.Max(0, SharedShellShards + delta);
                    updatedWallet = SharedShellShards;
                }
            }

            OnSharedCurrencyChanged?.Invoke(packet.Currency, delta, updatedWallet);
        }

        public void BroadcastCurrentVitals()
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

        public void BroadcastCurrentCurrency()
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
                    DeltaAmount = (short)Math.Clamp(currentRosaries, short.MinValue, short.MaxValue),
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
                    DeltaAmount = (short)Math.Clamp(currentShards, short.MinValue, short.MaxValue),
                    TransactionId = tx,
                    PickupUniqueId = 0 // Fix: Initialize field to prevent undefined behavior
                };
                OnOutgoingCurrencyBroadcast?.Invoke(packet);
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
