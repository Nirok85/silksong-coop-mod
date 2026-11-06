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
        NetworkPeerId? ReviveTargetPeer { get; }
        (float X, float Y) ReviveTargetPosition { get; }

        event Action<float, float>? OnSoloDeathSuppressed;
        event Action? OnTeamWipeTriggered;
        event Action<float, float>? OnReviveAtPartnerRequested;
        event Action<DeathLifecyclePacket>? OnOutgoingDeathPacket;

        bool OnLocalPlayerDied(float deathX, float deathY);
        void OnRemotePartnerDied(float partnerDeathX, float partnerDeathY);
        void OnRemotePartnerRevived(float partnerX, float partnerY);

        // Per-peer death state for 3+ sessions. Single-partner callers can keep using
        // the aggregate overloads above.
        void RegisterRemotePeer(NetworkPeerId peer);
        void RemoveRemotePeer(NetworkPeerId peer);
        void ClearRemotePeers();
        bool IsRemotePeerAlive(NetworkPeerId peer);
        bool TryGetRemotePeerPosition(NetworkPeerId peer, out float x, out float y);
        void UpdatePartnerPosition(NetworkPeerId peer, float x, float y);
        void SetRemotePeerInSameScene(NetworkPeerId peer, bool sameScene);
        void OnRemotePartnerDied(NetworkPeerId peer, float partnerDeathX, float partnerDeathY);
        void OnRemotePartnerRevived(NetworkPeerId peer, float partnerX, float partnerY);

        void UpdateTick(float deltaTime);
        void CompleteRevival();
        void ConfirmReviveAtPartner(float x, float y);
        void FailReviveAtPartner();
        bool TryGetReviveTarget(out NetworkPeerId? peer, out float x, out float y);
        void HandleDeathPacket(NetworkPeerId sender, DeathLifecyclePacket packet);
        void ForceBenchRespawn();
        void BeginSoloBenchRespawn();
        void ResetAfterBenchRespawn();
    }
}
