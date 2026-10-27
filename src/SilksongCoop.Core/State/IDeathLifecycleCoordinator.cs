using System;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;

namespace SilksongCoop.Core.State
{
    /// <summary>
    /// Defines the current death state of the local player in the co-op lifecycle.
    /// </summary>
    public enum DeathState
    {
        Alive,
        Spectating,
        RespawningAtPartner,
        TeamWipe
    }

    /// <summary>
    /// Coordinates the death, spectator, and revive lifecycle for co-op players.
    /// </summary>
    public interface IDeathLifecycleCoordinator
    {
        DeathState CurrentState { get; }
        bool IsPartnerAlive { get; set; }
        float RemainingSpectatorTime { get; }
        float ReviveCooldownDuration { get; set; }
        (float X, float Y) LastPartnerPosition { get; set; }
        uint CurrentEpoch { get; }

        event Action<float, float>? OnSoloDeathSuppressed;
        event Action? OnTeamWipeTriggered;
        event Action<float, float>? OnReviveAtPartnerRequested;
        event Action<DeathLifecyclePacket>? OnOutgoingDeathPacket;

        bool OnLocalPlayerDied(float deathX, float deathY);
        void OnRemotePartnerDied(float partnerDeathX, float partnerDeathY);
        void OnRemotePartnerRevived(float partnerX, float partnerY);
        void UpdateTick(float deltaTime);
        void CompleteRevival();
        void HandleDeathPacket(NetworkPeerId sender, DeathLifecyclePacket packet);
        void ForceBenchRespawn();
        void ResetAfterBenchRespawn();
    }
}
