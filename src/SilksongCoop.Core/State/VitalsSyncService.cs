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
        #region Fields and Properties

        private static void Log(string msg) => System.Diagnostics.Debug.WriteLine(msg);

        private readonly HashSet<Guid> _processedTransactions = new HashSet<Guid>();
        private readonly Queue<Guid> _transactionOrder = new Queue<Guid>();
        private readonly object _transactionLock = new object();
        private const int MaxTrackedTransactions = 500;

        // Monotonic per-sender sequence carried in PickupUniqueId. The GUID window is
        // intentionally capped at 500 (pinned by tests); after eviction a replayed old
        // packet has an older sequence and is still rejected here.
        private readonly Dictionary<(NetworkPeerId Peer, CurrencyType Currency), uint> _lastCurrencySequences = new();
        private uint _currencySequence;

        // Physical drops exist in BOTH simulations (each client kills the same enemy and
        // spawns its own copy). A shared key (plus a small proximity window for physics
        // drift) lets the second pickup be credited only once.
        private struct ClaimedPickup
        {
            public ulong Key;
            public float X;
            public float Y;
            public CurrencyType Currency;
        }

        private readonly List<ClaimedPickup> _claimedPickups = new List<ClaimedPickup>();
        private const int MaxTrackedPickupKeys = 1024;
        private const float PickupDuplicateRadius = 0.5f;

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

        private int _remoteNeedleUpgradeLevel;
        /// <summary>
        /// The remote player's needle upgrade level as observed from their last
        /// <see cref="VitalsSyncPacket"/> (BUG-06: authoritative source for remote clone
        /// damage instead of the local PlayerData).
        /// </summary>
        public int RemoteNeedleUpgradeLevel
        {
            get { lock (_stateLock) { return _remoteNeedleUpgradeLevel; } }
            private set { lock (_stateLock) { _remoteNeedleUpgradeLevel = value; } }
        }

        private bool _hasRemoteData;
        public bool HasRemoteData
        {
            get { lock (_stateLock) { return _hasRemoteData; } }
            private set { lock (_stateLock) { _hasRemoteData = value; } }
        }

        private readonly object _stateLock = new object();

        #endregion

        #region Events

        public event Action<int, int>? OnRemoteVitalsObserved;
        public event Action<int>? OnSharedMaxHealthChanged;
        public event Action<int>? OnSharedMaxSilkChanged;
        public event Action<int>? OnSharedNeedleUpgradeChanged;
        public event Action<CurrencyType, int, int>? OnSharedCurrencyChanged;
        public event Action<VitalsSyncPacket>? OnOutgoingVitalsBroadcast;
        public event Action<CurrencySyncPacket>? OnOutgoingCurrencyBroadcast;

        /// <summary>
        /// Raised when a remote pickup was credited; the Unity layer destroys the local copy
        /// of the same drop so both players cannot collect it twice.
        /// </summary>
        public event Action<CurrencyType, ulong, float, float>? OnRemotePickupClaimed;

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

                // BUG-11: reset the remote vitals HUD state too — otherwise a reconnect
                // shows the previous partner's stale health/silk until a new packet arrives.
                _remoteHealth = 0;
                _remoteSilk = 0;
                _remoteMaxHealth = 0;
                _remoteMaxSilk = 0;
                _remoteNeedleUpgradeLevel = 0;
                _hasRemoteData = false;
            }
            lock (_transactionLock)
            {
                SharedRosaries = initialRosaries;
                SharedShellShards = initialShards;
                // New session: peer sequences restart from 1 on both sides.
                _lastCurrencySequences.Clear();
                _currencySequence = 0;
                // _claimedPickups intentionally survives: a peer that connects later must be
                // told which drops were already collected so it can remove its local copies.
            }
        }

        #endregion

        #region State Synchronization

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
            OnLocalCurrencyChanged(currency, deltaAmount, newTotal, transactionId, pickupKey: 0, float.NaN, float.NaN);
        }

        /// <summary>
        /// Handles a local currency change attributed to a physical pickup (key only).
        /// </summary>
        public void OnLocalCurrencyChanged(CurrencyType currency, int deltaAmount, int newTotal, Guid transactionId, ulong pickupKey)
        {
            OnLocalCurrencyChanged(currency, deltaAmount, newTotal, transactionId, pickupKey, float.NaN, float.NaN);
        }

        /// <summary>
        /// Handles a local currency change, optionally attributing it to a physical pickup.
        /// When the same pickup was already credited by the peer, the local game already added
        /// the amount, so it is reverted instead of being broadcast twice.
        /// </summary>
        /// <param name="currency">Type of currency that changed.</param>
        /// <param name="deltaAmount">Amount of currency gained or lost.</param>
        /// <param name="newTotal">New total amount of currency (0 = derive from the delta).</param>
        /// <param name="transactionId">Unique transaction identifier for deduplication.</param>
        /// <param name="pickupKey">Stable physical-drop key, or 0 for non-pickup changes.</param>
        /// <param name="pickupX">World X of the collected drop (NaN when unknown).</param>
        /// <param name="pickupY">World Y of the collected drop (NaN when unknown).</param>
        public void OnLocalCurrencyChanged(CurrencyType currency, int deltaAmount, int newTotal, Guid transactionId, ulong pickupKey, float pickupX, float pickupY)
        {
            if (deltaAmount == 0) return;
            if (transactionId == Guid.Empty) return;

            int currentWallet = 0;
            uint packetSequence = 0;
            bool duplicatePickup = false;

            lock (_transactionLock)
            {
                if (!_processedTransactions.Add(transactionId))
                {
                    return; // Already processed
                }

                RecordTransactionOrder(transactionId);

                if (pickupKey != 0)
                {
                    if (IsPickupAlreadyClaimed(currency, pickupKey, pickupX, pickupY))
                    {
                        duplicatePickup = true;
                        currentWallet = currency == CurrencyType.Rosaries ? SharedRosaries : SharedShellShards;
                    }
                    else
                    {
                        TrackClaimedPickup(currency, pickupKey, pickupX, pickupY);
                    }
                }

                if (!duplicatePickup)
                {
                    packetSequence = ++_currencySequence;

                    // The local game is authoritative for this player's wallet snapshot.
                    // When the caller supplies a real total, prefer it over the arithmetic —
                    // this also repairs drift from a previously lost packet. Tests and older
                    // call sites pass 0, which means "derive from the delta".
                    if (currency == CurrencyType.Rosaries)
                    {
                        int next = (int)Math.Clamp((long)SharedRosaries + deltaAmount, 0, PacketValidator.MaxReasonableCurrency);
                        if (newTotal > 0) next = Math.Min(newTotal, PacketValidator.MaxReasonableCurrency);
                        SharedRosaries = next;
                        currentWallet = SharedRosaries;
                    }
                    else
                    {
                        int next = (int)Math.Clamp((long)SharedShellShards + deltaAmount, 0, PacketValidator.MaxReasonableCurrency);
                        if (newTotal > 0) next = Math.Min(newTotal, PacketValidator.MaxReasonableCurrency);
                        SharedShellShards = next;
                        currentWallet = SharedShellShards;
                    }
                }
            }

            if (duplicatePickup)
            {
                // The peer already credited this drop. The local game added it anyway, so
                // restore PlayerData to the shared total without broadcasting anything.
                OnSharedCurrencyChanged?.Invoke(currency, -deltaAmount, currentWallet);
                return;
            }

            var packet = new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 0, (uint)Environment.TickCount),
                Currency = currency,
                TotalWalletAmount = currentWallet,
                DeltaAmount = (short)Math.Clamp(deltaAmount, short.MinValue, short.MaxValue),
                PickupUniqueId = packetSequence,
                TransactionId = transactionId,
                PickupKey = pickupKey,
                PickupX = pickupX,
                PickupY = pickupY
            };

            OnOutgoingCurrencyBroadcast?.Invoke(packet);
        }

        private bool IsPickupAlreadyClaimed(CurrencyType currency, ulong pickupKey, float x, float y)
        {
            for (int i = 0; i < _claimedPickups.Count; i++)
            {
                var claimed = _claimedPickups[i];
                if (claimed.Currency != currency) continue;

                if (pickupKey != 0 && claimed.Key == pickupKey)
                    return true;

                if (!float.IsNaN(x) && !float.IsNaN(y) &&
                    !float.IsNaN(claimed.X) && !float.IsNaN(claimed.Y))
                {
                    float dx = claimed.X - x;
                    float dy = claimed.Y - y;
                    if (dx * dx + dy * dy <= PickupDuplicateRadius * PickupDuplicateRadius)
                        return true;
                }
            }

            return false;
        }

        private void TrackClaimedPickup(CurrencyType currency, ulong pickupKey, float x, float y)
        {
            _claimedPickups.Add(new ClaimedPickup
            {
                Key = pickupKey,
                X = x,
                Y = y,
                Currency = currency
            });

            while (_claimedPickups.Count > MaxTrackedPickupKeys)
            {
                _claimedPickups.RemoveAt(0);
            }
        }

        #endregion

        #region Packet Handling

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
                    RemoteNeedleUpgradeLevel = packet.NeedleUpgradeLevel;
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
                bool claimedPickup = false;
                lock (_transactionLock)
                {
                    // Reject replayed/out-of-order snapshots even after their GUID has been
                    // evicted from the 500-entry FIFO. Packets from this build always carry
                    // a per-sender sequence; the value 0 keeps older/manual callers working.
                    if (packet.PickupUniqueId > 0)
                    {
                        var sequenceKey = (sender, packet.Currency);
                        if (_lastCurrencySequences.TryGetValue(sequenceKey, out uint lastSequence) &&
                            packet.PickupUniqueId <= lastSequence)
                        {
                            return;
                        }
                        _lastCurrencySequences[sequenceKey] = packet.PickupUniqueId;
                    }

                    // The same physical drop exists in both simulations. If this drop was
                    // already credited locally, the local player collected its copy — do not
                    // apply the peer's delta a second time.
                    if (packet.PickupKey != 0 &&
                        IsPickupAlreadyClaimed(packet.Currency, packet.PickupKey, packet.PickupX, packet.PickupY))
                    {
                        return;
                    }

                    if (!_processedTransactions.Add(packet.TransactionId))
                    {
                        return; // Duplicate transaction, ignore
                    }

                    RecordTransactionOrder(packet.TransactionId);

                    if (packet.PickupKey != 0)
                    {
                        TrackClaimedPickup(packet.Currency, packet.PickupKey, packet.PickupX, packet.PickupY);
                        claimedPickup = true;
                    }

                    if (packet.Currency == CurrencyType.Rosaries)
                    {
                        if (delta == 0)
                        {
                            // Initial sync: take the higher of the two values so a local
                            // pickup that has not been synced yet is not clobbered.
                            SharedRosaries = Math.Max(SharedRosaries, Math.Max(0, packet.TotalWalletAmount));
                        }
                        else
                        {
                            int next = (int)Math.Clamp((long)SharedRosaries + delta, 0, PacketValidator.MaxReasonableCurrency);

                            // Reconcile upward against the sender's snapshot on gains only.
                            // This repairs a missed positive delta without resurrecting a
                            // stale balance after a concurrent spend (negative deltas are
                            // applied arithmetically and stay clamped at zero).
                            if (delta > 0 && packet.TotalWalletAmount > 0)
                            {
                                next = Math.Max(next, packet.TotalWalletAmount);
                            }

                            SharedRosaries = next;
                        }
                        updatedWallet = SharedRosaries;
                    }
                    else
                    {
                        if (delta == 0)
                        {
                            SharedShellShards = Math.Max(SharedShellShards, Math.Max(0, packet.TotalWalletAmount));
                        }
                        else
                        {
                            int next = (int)Math.Clamp((long)SharedShellShards + delta, 0, PacketValidator.MaxReasonableCurrency);
                            if (delta > 0 && packet.TotalWalletAmount > 0)
                            {
                                next = Math.Max(next, packet.TotalWalletAmount);
                            }
                            SharedShellShards = next;
                        }
                        updatedWallet = SharedShellShards;
                    }
                }

                OnSharedCurrencyChanged?.Invoke(packet.Currency, delta, updatedWallet);

                if (claimedPickup)
                {
                    // Destroy the local copy of the drop that the peer just collected.
                    OnRemotePickupClaimed?.Invoke(packet.Currency, packet.PickupKey, packet.PickupX, packet.PickupY);
                }
            }
            catch (Exception ex)
            {
                Log($"[VitalsSyncService] HandleCurrencyPacket error: {ex.Message}");
            }
        }

        #endregion

        #region Broadcasting

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
                    uint seq;
                    lock (_transactionLock)
                    {
                        _processedTransactions.Add(tx);
                        RecordTransactionOrder(tx);
                        seq = ++_currencySequence;
                    }

                    var packet = new CurrencySyncPacket
                    {
                        Header = new PacketHeader(PacketType.CurrencySync, 3, 0, (uint)Environment.TickCount),
                        Currency = CurrencyType.Rosaries,
                        TotalWalletAmount = currentRosaries,
                        DeltaAmount = 0, // Initial sync: delta 0 to avoid double-counting on receiver
                        TransactionId = tx,
                        PickupUniqueId = seq
                    };
                    OnOutgoingCurrencyBroadcast?.Invoke(packet);
                }

                if (currentShards > 0)
                {
                    var tx = Guid.NewGuid();
                    uint seq;
                    lock (_transactionLock)
                    {
                        _processedTransactions.Add(tx);
                        RecordTransactionOrder(tx);
                        seq = ++_currencySequence;
                    }

                    var packet = new CurrencySyncPacket
                    {
                        Header = new PacketHeader(PacketType.CurrencySync, 3, 0, (uint)Environment.TickCount),
                        Currency = CurrencyType.ShellShards,
                        TotalWalletAmount = currentShards,
                        DeltaAmount = 0, // Initial sync: delta 0 to avoid double-counting on receiver
                        TransactionId = tx,
                        PickupUniqueId = seq
                    };
                    OnOutgoingCurrencyBroadcast?.Invoke(packet);
                }
            }
            catch (Exception ex)
            {
                Log($"[VitalsSyncService] BroadcastCurrentCurrency error: {ex.Message}");
            }
        }

        /// <summary>
        /// Re-broadcasts the most recent claimed pickups as delta-0 packets. A client that
        /// connects after a drop was collected uses this to disable its local copy; without
        /// it the reconnecting player could collect the same world drop a second time.
        /// </summary>
        public void BroadcastClaimedPickups()
        {
            try
            {
                List<ClaimedPickup> snapshot;
                int rosaries, shards;
                lock (_transactionLock)
                {
                    // Only the most recent drops matter for a newly connected scene; sending
                    // the whole 1024-entry window would flood the handshake.
                    int skip = Math.Max(0, _claimedPickups.Count - 64);
                    snapshot = _claimedPickups.GetRange(skip, _claimedPickups.Count - skip);
                    rosaries = SharedRosaries;
                    shards = SharedShellShards;
                }

                foreach (var claimed in snapshot)
                {
                    if (claimed.Key == 0) continue;

                    var tx = Guid.NewGuid();
                    uint seq;
                    lock (_transactionLock)
                    {
                        _processedTransactions.Add(tx);
                        RecordTransactionOrder(tx);
                        seq = ++_currencySequence;
                    }

                    var packet = new CurrencySyncPacket
                    {
                        Header = new PacketHeader(PacketType.CurrencySync, 3, 0, (uint)Environment.TickCount),
                        Currency = claimed.Currency,
                        TotalWalletAmount = claimed.Currency == CurrencyType.Rosaries ? rosaries : shards,
                        DeltaAmount = 0,
                        TransactionId = tx,
                        PickupUniqueId = seq,
                        PickupKey = claimed.Key,
                        PickupX = claimed.X,
                        PickupY = claimed.Y
                    };
                    OnOutgoingCurrencyBroadcast?.Invoke(packet);
                }
            }
            catch (Exception ex)
            {
                Log($"[VitalsSyncService] BroadcastClaimedPickups error: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears the picked-up-drop registry after a world reset (bench respawn / scene
        /// change). Without this, a fresh drop at the same scene/position would be mistaken
        /// for one collected in the previous world.
        /// </summary>
        public void ClearClaimedPickups()
        {
            lock (_transactionLock)
            {
                _claimedPickups.Clear();
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

        #endregion
    }
}
