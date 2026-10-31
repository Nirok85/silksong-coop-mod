using System;
using SilksongCoop.Core.Events;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using SilksongCoop.Mod.State;
using UnityEngine;

namespace SilksongCoop.Mod.Services
{
    /// <summary>
    /// Bridges VitalsSyncService with transport and game state, replacing inline callbacks.
    /// </summary>
    public class VitalsSyncManager : ISyncOrchestrator
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
        }

        public void OnSessionStarted(bool isHost) { }
        public void OnSessionEnded() { _currencyResyncTimer = 0f; }

        private float _currencyResyncTimer;

        private void OnOutgoingVitals(VitalsSyncPacket packet)
        {
            SendPacket(PacketType.VitalsSync, packet, DeliveryMethod.ReliableOrdered, NetworkChannels.Vitals);
        }

        private void OnOutgoingCurrency(CurrencySyncPacket packet)
        {
            SendPacket(PacketType.CurrencySync, packet, DeliveryMethod.ReliableOrdered, NetworkChannels.Currency);
        }

        private void SendPacket<T>(PacketType type, T packet, DeliveryMethod method, byte channel) where T : struct
        {
            var transport = _transportService.ActiveTransport;
            var session = _sessionService.SessionState;
            if (transport == null || session?.IsConnected != true || !session.RemotePeerId.HasValue) return;

            try
            {
                byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64);
                try
                {
                    var writer = new PacketWriter(buffer);
                    if (packet is VitalsSyncPacket v) v.Serialize(ref writer);
                    else if (packet is CurrencySyncPacket c) c.Serialize(ref writer);
                    transport.Send(session.RemotePeerId.Value, buffer, 0, writer.Position, method, channel);
                    Diagnostics?.RecordSent(writer.Position);
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }
            }
            catch (Exception ex)
            {
                SilksongCoop.Core.CoopLog.Warn($"[VitalsSyncManager] Failed sending {type}: {ex.Message}");
            }
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
        }

        private void OnRemotePickupClaimed(Core.Protocol.Packets.CurrencyType currency, ulong key, float x, float y)
        {
            _enemySync?.OnRemotePickupClaimed(currency, key, x, y);
        }
    }
}
