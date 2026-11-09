using System;
using System.Collections.Generic;
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
        private readonly Queue<CurrencySyncPacket> _pendingUdpCurrency = new();

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

            if (_transportService.ActiveTransport is LiteNetLibTransport udp)
                FlushUdpCurrency(udp);
            ReconcileWalletIfDue();
        }

        public void OnSessionStarted(bool isHost) { }

        public void OnSessionEnded()
        {
            _pendingUdpCurrency.Clear();
            _walletReconcilePlayerData = null;
            _walletReconcileAttempts = 0;
        }

        // CurrencyManager queues changes. Align PlayerData with the accepted Core balance
        // after the game drains that queue.
        private float _walletReconcileAt;
        private PlayerData? _walletReconcilePlayerData;
        private int _walletReconcileAttempts;

        private void OnOutgoingVitals(VitalsSyncPacket packet)
        {
            SendPacket(packet, DeliveryMethod.ReliableOrdered, NetworkChannels.Vitals);
        }

        private void OnOutgoingCurrency(CurrencySyncPacket packet)
        {
            if (_transportService.ActiveTransport is LiteNetLibTransport udp && udp.IsConnected)
            {
                _pendingUdpCurrency.Enqueue(packet);
                FlushUdpCurrency(udp);
                return;
            }

            SendPacket(packet, DeliveryMethod.ReliableOrdered, NetworkChannels.Currency);
        }

        private void FlushUdpCurrency(LiteNetLibTransport transport)
        {
            // The transport and game hooks run on Unity's thread. Keep order and wait for
            // space instead of handing a spend to the transport when it would drop it.
            while (transport.IsConnected && _pendingUdpCurrency.Count > 0 &&
                   transport.ReliableBacklogCount < NetworkLimits.ReliableBacklogCap)
            {
                if (!PacketSender.SendToAll(transport, _pendingUdpCurrency.Peek(),
                    DeliveryMethod.ReliableOrdered, NetworkChannels.Currency, 64, Diagnostics))
                    break;

                _pendingUdpCurrency.Dequeue();
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
            var pd = PlayerData.instance;
            if (pd == null) return;
            using (RemoteApplyScope.Begin())
            {
                var gameCurrency = type == Core.Protocol.Packets.CurrencyType.Rosaries ? CurrencyType.Money : CurrencyType.Shard;
                CurrencyManager.ChangeCurrency(delta, gameCurrency, true);
            }

            ScheduleWalletReconcile(pd);
        }

        private void ScheduleWalletReconcile(PlayerData playerData)
        {
            _walletReconcilePlayerData = playerData;
            _walletReconcileAttempts = 4;
            _walletReconcileAt = Time.unscaledTime + 0.15f;
        }

        private void ReconcileWalletIfDue()
        {
            var pd = _walletReconcilePlayerData;
            if (pd == null || Time.unscaledTime < _walletReconcileAt) return;

            // CurrencyManager only queues the change; while the game is paused the queue
            // does not drain and comparing PlayerData with Core would apply the remainder
            // a second time once it does. Retry on the first unpaused frame.
            if (Time.timeScale <= 0f) return;

            if (!ReferenceEquals(pd, PlayerData.instance))
            {
                _walletReconcilePlayerData = null;
                return;
            }

            bool applied = false;
            using (RemoteApplyScope.Begin())
            {
                int rosaryDiff = _vitalsSync.SharedRosaries - pd.geo;
                if (rosaryDiff != 0)
                {
                    CurrencyManager.ChangeCurrency(rosaryDiff, CurrencyType.Money, false);
                    applied = true;
                }

                int shardDiff = _vitalsSync.SharedShellShards - pd.ShellShards;
                if (shardDiff != 0)
                {
                    CurrencyManager.ChangeCurrency(shardDiff, CurrencyType.Shard, false);
                    applied = true;
                }
            }

            _walletReconcileAttempts--;
            if (applied && _walletReconcileAttempts > 0)
                _walletReconcileAt = Time.unscaledTime + 0.15f;
            else
                _walletReconcilePlayerData = null;
        }

        private void OnRemotePickupClaimed(Core.Protocol.Packets.CurrencyType currency, ulong key, float x, float y)
        {
            _enemySync?.OnRemotePickupClaimed(currency, key);
        }
    }
}
