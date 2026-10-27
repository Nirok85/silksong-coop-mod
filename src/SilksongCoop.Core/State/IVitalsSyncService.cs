using System;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;

namespace SilksongCoop.Core.State
{
    /// <summary>
    /// Synchronizes player vitals (health, silk, upgrades, currency) between co-op peers.
    /// </summary>
    public interface IVitalsSyncService
    {
        // Local Player State
        int LocalHealth { get; }
        int LocalSilk { get; }
        int SharedMaxHealth { get; }
        int SharedMaxSilkSpools { get; }
        int SharedNeedleUpgradeLevel { get; }
        int SharedRosaries { get; }
        int SharedShellShards { get; }

        // Local State Change Handlers
        void OnLocalDamageTaken(int damageAmount, int newHealth);
        void OnLocalSilkUsed(int silkUsed, int newSilk);
        void OnLocalMaxHealthUpgrade(int newMaxHealth);
        void OnLocalMaxSilkUpgrade(int newMaxSilk);
        void OnLocalNeedleUpgrade(int newNeedleLevel);
        void OnLocalCurrencyChanged(CurrencyType currency, int deltaAmount, int newTotal, Guid transactionId);

        // Network Handlers
        void HandleVitalsPacket(NetworkPeerId sender, VitalsSyncPacket packet);
        void HandleCurrencyPacket(NetworkPeerId sender, CurrencySyncPacket packet);
        void BroadcastCurrentVitals();
        void BroadcastCurrentCurrency();

        // Events for UI and Game Reflection
        event Action<int, int>? OnRemoteVitalsObserved; // (remoteHealth, remoteSilk)
        event Action<int>? OnSharedMaxHealthChanged;
        event Action<int>? OnSharedMaxSilkChanged;
        event Action<int>? OnSharedNeedleUpgradeChanged;
        event Action<CurrencyType, int, int>? OnSharedCurrencyChanged; // (type, delta, newTotal)
        event Action<VitalsSyncPacket>? OnOutgoingVitalsBroadcast;
        event Action<CurrencySyncPacket>? OnOutgoingCurrencyBroadcast;
    }
}
