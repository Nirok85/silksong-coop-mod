using System;
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

            _currencyResyncTimer += Time.unscaledDeltaTime;
            if (_currencyResyncTimer >= 5f)
            {
                _currencyResyncTimer = 0f;
                _vitalsSync.BroadcastCurrentCurrency();
            }

            ReconcileWalletIfDue();
        }

        public void OnSessionStarted(bool isHost) { }

        public void OnSessionEnded()
        {
            _currencyResyncTimer = 0f;
            _walletReconcilePending = false;
            _walletReconcileAttempts = 0;
        }

        private float _currencyResyncTimer;

        // A delta-only application can leave PlayerData behind the shared wallet when the
        // sender's packet also repaired a missed gain. Re-apply the shared total after the
        // game's queued currency change has settled, instead of trusting the packet delta
        // as the final balance.
        private float _walletReconcileAt;
        private bool _walletReconcilePending;
        private int _walletReconcileAttempts;

        private void OnOutgoingVitals(VitalsSyncPacket packet)
        {
            SendPacket(packet, DeliveryMethod.ReliableOrdered, NetworkChannels.Vitals);
        }

        private void OnOutgoingCurrency(CurrencySyncPacket packet)
        {
            SendPacket(packet, DeliveryMethod.ReliableOrdered, NetworkChannels.Currency);
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
                var silkgurrencyType = (type == Core.Protocol.Packets.CurrencyType.Rosaries) ? CurrencyType.Money : CurrencyType.Shard;
                if (delta != 0)
                {
                    CurrencyManager.ChangeCurrency(delta, silkgurrencyType, true);
                }
                else
                {
                    int current = (type == Core.Protocol.Packets.CurrencyType.Rosaries) ? pd.geo : pd.ShellShards;
                    int diff = total - current;
                    if (diff > 0)
                        CurrencyManager.ChangeCurrency(diff, silkgurrencyType, true);
                }
            }

            // The shared core wallet may be higher than PlayerData + delta when the
            // sender's packet reconciled a missed gain. Apply the remainder once the
            // queued change above has been processed by the game.
            ScheduleWalletReconcile();
        }

        private void ScheduleWalletReconcile()
        {
            _walletReconcilePending = true;
            _walletReconcileAttempts = 4;
            _walletReconcileAt = Time.unscaledTime + 0.15f;
        }

        private void ReconcileWalletIfDue()
        {
            if (!_walletReconcilePending || Time.unscaledTime < _walletReconcileAt) return;

            // CurrencyManager only queues the change; while the game is paused the queue
            // does not drain and comparing PlayerData with Core would apply the remainder
            // a second time once it does. Retry on the first unpaused frame.
            if (Time.timeScale <= 0f) return;

            var pd = PlayerData.instance;
            if (pd == null)
            {
                _walletReconcilePending = false;
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
                _walletReconcilePending = false;
        }

        private void OnRemotePickupClaimed(Core.Protocol.Packets.CurrencyType currency, ulong key, float x, float y)
        {
            // The event originates from HandleCurrencyPacket, which only runs for the single
            // session peer today. Pass it explicitly so the Unity layer can require that the
            // collector is actually in the local scene before using coordinates.
            var sender = _sessionService.SessionState?.RemotePeerId;
            if (!sender.HasValue) return;

            _enemySync?.OnRemotePickupClaimed(currency, key, x, y, sender.Value);
        }
    }
}
