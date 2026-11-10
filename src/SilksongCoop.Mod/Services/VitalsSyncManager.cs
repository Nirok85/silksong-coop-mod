using System;
using HarmonyLib;
using System.Collections.Generic;
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
        private readonly HashSet<NetworkPeerId> _currencyPeersReady = new();

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

            if (_walletReconcilePlayerData != null && !(bool)CurrencyUpdateQueued.GetValue(null))
                OnCurrencyQueueDrained();
        }

        public void OnSessionStarted(bool isHost) { }

        public void OnSessionEnded()
        {
            _currencyPeersReady.Clear();
            _walletReconcilePlayerData = null;
        }

        internal void ResetCurrencyJournal() => _currencyJournal.Clear();

        internal void OnCurrencyPeerDisconnected(NetworkPeerId peer) => _currencyPeersReady.Remove(peer);

        internal void ForgetCurrencyPeer(NetworkPeerId peer) => _currencyJournal.Remove(peer);

        internal void EnableCurrencyPeer(NetworkPeerId peer)
        {
            if (!_currencyPeersReady.Add(peer)) return;
            if (!_currencyJournal.TryGetValue(peer, out var journal))
                _currencyJournal.Add(peer, journal = new List<CurrencySyncPacket>());
            foreach (var packet in journal)
                SendCurrencyTo(peer, packet);
        }

        internal void ReceiveCurrencyReceipt(NetworkPeerId peer, Guid transactionId)
        {
            if (_currencyJournal.TryGetValue(peer, out var journal))
                journal.RemoveAll(packet => packet.TransactionId == transactionId);
        }

        internal long GetPendingCurrencyDelta(NetworkPeerId peer, Core.Protocol.Packets.CurrencyType currency)
        {
            long total = 0;
            if (_currencyJournal.TryGetValue(peer, out var journal))
                foreach (var packet in journal)
                    if (packet.Currency == currency) total += packet.DeltaAmount;
            return total;
        }

        internal void SendWalletSeed(NetworkPeerId peer, (int Rosaries, int Shards) balances)
        {
            var transport = _transportService.ActiveTransport;
            if (transport == null) return;
            PacketSender.SendTo(transport, peer, new WalletSeedPacket
            {
                Header = new PacketHeader(PacketType.WalletSeed, NetworkChannels.Currency, 0, NetworkClock.NowMs),
                Rosaries = balances.Rosaries,
                Shards = balances.Shards
            }, DeliveryMethod.ReliableOrdered, NetworkChannels.Currency, PacketHeader.HeaderSize + 8, Diagnostics);
        }

        private void SendCurrencyTo(NetworkPeerId peer, CurrencySyncPacket packet)
        {
            var transport = _transportService.ActiveTransport;
            if (transport != null)
                PacketSender.SendTo(transport, peer, packet, DeliveryMethod.ReliableOrdered, NetworkChannels.Currency, 64, Diagnostics);
        }

        private PlayerData? _walletReconcilePlayerData;

        private void OnOutgoingVitals(VitalsSyncPacket packet)
        {
            SendPacket(packet, DeliveryMethod.ReliableOrdered, NetworkChannels.Vitals);
        }

        private void OnOutgoingCurrency(CurrencySyncPacket packet)
        {
            if (packet.DeltaAmount == 0)
            {
                SendPacket(packet, DeliveryMethod.ReliableOrdered, NetworkChannels.Currency);
                return;
            }

            var session = _sessionService.SessionState;
            if (SilksongCoopPlugin.Instance?.CanTrackCurrency != true || session == null) return;
            foreach (var peer in session.RemotePeerIds)
                if (!_currencyJournal.ContainsKey(peer))
                    _currencyJournal.Add(peer, new List<CurrencySyncPacket>());

            foreach (var pair in _currencyJournal)
            {
                if (pair.Value.Count >= MaxUnconfirmedTransactions)
                {
                    SilksongCoopPlugin.LogWarn("[Coop] Too many unconfirmed currency transactions.");
                    SilksongCoopPlugin.Instance.DisconnectFromSession(DisconnectReason.ConnectionFailed);
                    return;
                }
                pair.Value.Add(packet);
                if (_currencyPeersReady.Contains(pair.Key)) SendCurrencyTo(pair.Key, packet);
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
                int newMaxSilk = newSpools * 9;
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
