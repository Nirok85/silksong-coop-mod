using System;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using SilksongCoop.Mod.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Events;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using SilksongCoop.Mod.State;
using UnityEngine;

namespace SilksongCoop.Mod.Services
{
    /// <summary>
    /// Bridges VitalsSyncService with transport and game state, replacing inline callbacks.
    /// </summary>
    public class VitalsSyncManager : ISyncOrchestrator, IDisposable
    {
        private readonly ISessionService _sessionService;
        private readonly ITransportService _transportService;
        private readonly IVitalsSyncService _vitalsSync;
        private readonly DeathLifecycleCoordinator _deathCoordinator;
        private readonly EnemySyncController? _enemySync;
        private readonly Dictionary<NetworkPeerId, List<CurrencySyncPacket>> _currencyJournal = new();
        private readonly HashSet<NetworkPeerId> _exhaustedCurrencyJournals = new();
        /// <summary>Peers already warned about a filling journal; warns once per peer.</summary>
        private readonly HashSet<NetworkPeerId> _journalWarned = new();
        private readonly HashSet<NetworkPeerId> _currencyPeersReady = new();
        private readonly Dictionary<NetworkPeerId, HashSet<Guid>> _currencySent = new();
        private readonly Dictionary<NetworkPeerId, (WalletSeedPacket[] Parts, int Next)> _seedSends = new();

        private const int MaxUnconfirmedTransactions = 4096;

        private static readonly System.Reflection.FieldInfo CurrencyUpdateQueued =
            AccessTools.Field(typeof(CurrencyManager), "updateQueued");
        private static readonly Action<int, CurrencyType, bool> ProcessCurrency =
            AccessTools.MethodDelegate<Action<int, CurrencyType, bool>>(
                AccessTools.Method(typeof(CurrencyManager), "ProcessCurrency"));

        /// <summary>
        /// Optional diagnostics sink, wired up after the Menu UI is created.
        /// </summary>
        public INetworkDiagnostics? Diagnostics { get; set; }

        public VitalsSyncManager(
            ISessionService sessionService,
            ITransportService transportService,
            IVitalsSyncService vitalsSync,
            DeathLifecycleCoordinator deathCoordinator,
            EnemySyncController? enemySync)
        {
            _sessionService = sessionService;
            _transportService = transportService;
            _vitalsSync = vitalsSync;
            _deathCoordinator = deathCoordinator;
            _enemySync = enemySync;

            _vitalsSync.OnOutgoingVitalsBroadcast += OnOutgoingVitals;
            _vitalsSync.OnOutgoingCurrencyBroadcast += OnOutgoingCurrency;
            _vitalsSync.OnSharedMaxHealthChanged += OnSharedMaxHealthChanged;
            _vitalsSync.OnSharedNeedleUpgradeChanged += OnSharedNeedleUpgradeChanged;
            _vitalsSync.OnSharedMaxSilkChanged += OnSharedMaxSilkChanged;
            _vitalsSync.OnSharedCurrencyChanged += OnSharedCurrencyChanged;
            _vitalsSync.OnRemotePickupClaimed += OnRemotePickupClaimed;
        }

        public void Dispose()
        {
            _vitalsSync.OnOutgoingVitalsBroadcast -= OnOutgoingVitals;
            _vitalsSync.OnOutgoingCurrencyBroadcast -= OnOutgoingCurrency;
            _vitalsSync.OnSharedMaxHealthChanged -= OnSharedMaxHealthChanged;
            _vitalsSync.OnSharedNeedleUpgradeChanged -= OnSharedNeedleUpgradeChanged;
            _vitalsSync.OnSharedMaxSilkChanged -= OnSharedMaxSilkChanged;
            _vitalsSync.OnSharedCurrencyChanged -= OnSharedCurrencyChanged;
            _vitalsSync.OnRemotePickupClaimed -= OnRemotePickupClaimed;
        }

        public void Initialize() { }
        public void Update()
        {
            var session = _sessionService.SessionState;
            if (session?.IsConnected != true || !session.RemotePeerId.HasValue) return;

            foreach (var peer in _currencyPeersReady.ToArray()) FlushCurrencyJournal(peer);
            if (_walletReconcilePlayerData != null && !(bool)CurrencyUpdateQueued.GetValue(null))
                OnCurrencyQueueDrained();
        }

        public void OnSessionStarted(bool isHost) { }

        public void OnSessionEnded()
        {
            _currencyPeersReady.Clear();
            _currencySent.Clear();
            _seedSends.Clear();
            _walletReconcilePlayerData = null;
        }

        internal void ResetCurrencyJournal()
        {
            _currencyJournal.Clear();
            _exhaustedCurrencyJournals.Clear();
            _journalWarned.Clear();
        }

        internal void OnCurrencyPeerDisconnected(NetworkPeerId peer)
        {
            _currencyPeersReady.Remove(peer);
            _currencySent.Remove(peer);
            _seedSends.Remove(peer);
        }

        internal void ForgetCurrencyPeer(NetworkPeerId peer)
        {
            _currencyJournal.Remove(peer);
            _currencySent.Remove(peer);
            _seedSends.Remove(peer);
            _exhaustedCurrencyJournals.Remove(peer);
            _journalWarned.Remove(peer);
        }

        internal bool CanResumeCurrencyPeer(NetworkPeerId peer) => !_exhaustedCurrencyJournals.Contains(peer);

        internal void EnableCurrencyPeer(NetworkPeerId peer)
        {
            if (!_currencyPeersReady.Add(peer)) return;
            if (!_currencyJournal.TryGetValue(peer, out var journal))
                _currencyJournal.Add(peer, journal = new List<CurrencySyncPacket>());
            FlushCurrencyJournal(peer);
        }

        internal void ReceiveCurrencyReceipt(NetworkPeerId peer, Guid transactionId)
        {
            if (_currencyJournal.TryGetValue(peer, out var journal))
                journal.RemoveAll(packet => packet.TransactionId == transactionId);
            if (_currencySent.TryGetValue(peer, out var sent)) sent.Remove(transactionId);
        }

        internal long GetPendingCurrencyDelta(NetworkPeerId peer, Core.Protocol.Packets.CurrencyType currency)
        {
            long total = 0;
            if (_currencyJournal.TryGetValue(peer, out var journal))
                foreach (var packet in journal)
                    if (packet.Currency == currency) total += packet.DeltaAmount;
            return total;
        }

        /// <summary>True when the transport accepted the seed for delivery.</summary>
        internal bool SendWalletSeed(NetworkPeerId peer, WalletSeedPacket seed)
        {
            var transport = _transportService.ActiveTransport;
            if (transport == null) return false;
            if (!_seedSends.TryGetValue(peer, out var pending))
                pending = (seed.CreateChunks(), 0);
            while (pending.Next < pending.Parts.Length)
            {
                if (!PacketSender.SendTo(transport, peer, pending.Parts[pending.Next],
                        DeliveryMethod.ReliableOrdered, NetworkChannels.Currency, WalletSeedPacket.MaxWireSize, Diagnostics))
                {
                    _seedSends[peer] = pending;
                    return false;
                }
                pending.Next++;
            }
            _seedSends.Remove(peer);
            return true;
        }

        internal CurrencySyncPacket[] GetPendingCurrencyPackets(NetworkPeerId peer) =>
            _currencyJournal.TryGetValue(peer, out var journal) ? journal.ToArray() : Array.Empty<CurrencySyncPacket>();

        private void FlushCurrencyJournal(NetworkPeerId peer)
        {
            var transport = _transportService.ActiveTransport;
            if (transport == null || !_currencyPeersReady.Contains(peer) || !_currencyJournal.TryGetValue(peer, out var journal)) return;
            if (!_currencySent.TryGetValue(peer, out var sent)) _currencySent[peer] = sent = new HashSet<Guid>();
            int budget = 32;
            foreach (var packet in journal)
            {
                if (sent.Contains(packet.TransactionId)) continue;
                if (budget-- <= 0) break;
                if (!PacketSender.SendTo(transport, peer, packet, DeliveryMethod.ReliableOrdered, NetworkChannels.Currency, 64, Diagnostics)) break;
                sent.Add(packet.TransactionId);
            }
        }

        private PlayerData? _walletReconcilePlayerData;

        private void OnOutgoingVitals(VitalsSyncPacket packet)
        {
            SendPacket(packet, DeliveryMethod.ReliableOrdered, NetworkChannels.Vitals);
        }

        private void OnOutgoingCurrency(CurrencySyncPacket packet)
        {
            var session = _sessionService.SessionState;
            if (session == null) return;

            if (packet.DeltaAmount != 0 && SilksongCoopPlugin.Instance?.CanTrackCurrency != true) return;
            foreach (var peer in session.RemotePeerIds)
                if (!_currencyJournal.ContainsKey(peer))
                    _currencyJournal.Add(peer, new List<CurrencySyncPacket>());

            foreach (var pair in _currencyJournal)
            {
                if (_exhaustedCurrencyJournals.Contains(pair.Key)) continue;
                // Early visibility: receipts stream in continuously, so a journal that
                // keeps growing here means the partner stopped confirming. Warn once
                // well before the exhaustion limit turns into a disconnect.
                if (pair.Value.Count >= MaxUnconfirmedTransactions * 3 / 4 &&
                    _journalWarned.Add(pair.Key))
                {
                    SilksongCoopPlugin.LogWarn(
                        $"[Coop] Currency journal for {pair.Key} is at {pair.Value.Count}/{MaxUnconfirmedTransactions} unconfirmed transactions; the partner may have stopped confirming receipts.");
                }
                if (pair.Value.Count >= MaxUnconfirmedTransactions)
                {
                    _exhaustedCurrencyJournals.Add(pair.Key);
                    pair.Value.Clear();
                    pair.Value.TrimExcess();
                    SilksongCoopPlugin.LogWarn($"[Coop] Currency journal exhausted for {pair.Key}.");
                    if (session.RemotePeerIds.Contains(pair.Key))
                    {
                        if (_transportService.ActiveTransport is SteamP2PTransport steam)
                            steam.FailReliablePeer(pair.Key);
                        else
                        {
                            SilksongCoopPlugin.Instance?.DisconnectFromSession(DisconnectReason.ConnectionFailed);
                            return;
                        }
                    }
                    continue;
                }
                // The peer recovered from the earlier warning (receipts caught up): allow
                // a fresh warning if the journal ever starts growing again.
                if (pair.Value.Count < MaxUnconfirmedTransactions / 2) _journalWarned.Remove(pair.Key);

                pair.Value.Add(packet);
                if (_currencyPeersReady.Contains(pair.Key)) FlushCurrencyJournal(pair.Key);
            }
        }

        private void SendPacket<T>(T packet, DeliveryMethod method, byte channel) where T : struct, IPacket
        {
            var transport = _transportService.ActiveTransport;
            var session = _sessionService.SessionState;
            if (transport == null || session?.IsConnected != true) return;

            PacketSender.SendToAll(transport, packet, method, channel, 64, Diagnostics, "[VitalsSyncManager]");
        }

        private void OnSharedMaxHealthChanged(int newMax)
        {
            var pd = PlayerData.instance;
            if (pd == null) return;
            using (RemoteApplyScope.Begin())
            {
                pd.maxHealthBase = newMax;
                pd.maxHealth = newMax;
                if (pd.health > newMax) pd.health = newMax;
                EventRegister.SendEvent(EventRegisterEvents.HealthUpdate);
            }
        }

        private void OnSharedNeedleUpgradeChanged(int newLevel)
        {
            var pd = PlayerData.instance;
            if (pd == null) return;
            using (RemoteApplyScope.Begin())
            {
                if (newLevel > pd.nailUpgrades)
                    pd.nailUpgrades = newLevel;
            }
        }

        private void OnSharedMaxSilkChanged(int newSpools)
        {
            var pd = PlayerData.instance;
            if (pd == null) return;
            using (RemoteApplyScope.Begin())
            {
                int newMaxSilk = newSpools; // Protocol v12 transmits raw capacity.
                if (newMaxSilk > pd.silkMax)
                {
                    pd.silkMax = newMaxSilk;
                    HeroController.instance?.RefreshSilk();
                }
            }
        }

        private void OnSharedCurrencyChanged(Core.Protocol.Packets.CurrencyType type, int delta, int total)
        {
            ScheduleWalletReconcile();
        }

        internal void ScheduleWalletReconcile() => _walletReconcilePlayerData = PlayerData.instance;

        internal void OnCurrencyQueueDrained()
        {
            var pd = _walletReconcilePlayerData;
            _walletReconcilePlayerData = null;
            if (pd == null || !ReferenceEquals(pd, PlayerData.instance)) return;

            using (RemoteApplyScope.Begin())
            {
                int rosaryDiff = _vitalsSync.SharedRosaries - pd.geo;
                if (rosaryDiff != 0)
                    ProcessCurrency(rosaryDiff, CurrencyType.Money, true);

                int shardDiff = _vitalsSync.SharedShellShards - pd.ShellShards;
                if (shardDiff != 0)
                    ProcessCurrency(shardDiff, CurrencyType.Shard, true);
            }
        }

        private void OnRemotePickupClaimed(Core.Protocol.Packets.CurrencyType currency, ulong key, float x, float y)
        {
            _enemySync?.OnRemotePickupClaimed(currency, key);
        }
    }
}
