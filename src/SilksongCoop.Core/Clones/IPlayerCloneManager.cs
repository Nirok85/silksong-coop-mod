using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;

namespace SilksongCoop.Core.Clones
{
    /// <summary>
    /// Manages spawning, transform replication, animation synchronization,
    /// and visual representation of remote players.
    /// Engine-agnostic interface implemented by RemotePlayerCloneManager in the Unity mod.
    /// </summary>
    public interface IPlayerCloneManager
    {
        /// <summary>
        /// Spawns a clean remote Hornet clone for the specified peer without HeroController singleton guard.
        /// </summary>
        void SpawnRemotePlayer(NetworkPeerId peerId, bool isPlayer2);

        /// <summary>
        /// Despawns and cleans up the remote clone and associated buffers.
        /// </summary>
        void DespawnRemotePlayer(NetworkPeerId peerId);

        /// <summary>
        /// Updates the remote clone's trajectory using the received network packet.
        /// Pushes snapshot into circular buffer for Hermite spline evaluation.
        /// </summary>
        void UpdateRemoteTransform(
            NetworkPeerId peerId,
            float posX, float posY,
            float velX, float velY,
            byte facingDirection,
            ushort stateFlags,
            int animClipHash,
            ushort normalizedTime,
            uint timestampMs);

        /// <summary>
        /// Triggers a discrete one-shot combat action (slash, harpoon dash, air dash, bind).
        /// </summary>
        void TriggerRemoteAction(NetworkPeerId peerId, ActionType action, byte variant);

        /// <summary>
        /// Synchronizes the active tk2d animation clip and normalized playback offset.
        /// </summary>
        void SyncAnimation(NetworkPeerId peerId, int clipHash, ushort normalizedTime);

        /// <summary>
        /// Updates the facing direction (scale.x sign or FlipX).
        /// </summary>
        void SetFacingDirection(NetworkPeerId peerId, bool facingRight);

        /// <summary>
        /// Activates the dash trail particle and visual FX lifecycle for the specified peer.
        /// </summary>
        void TriggerDashEffect(NetworkPeerId peerId, bool isAirDash);

        /// <summary>
        /// Queries the current interpolated 2D position of the remote player.
        /// </summary>
        bool TryGetRemotePosition(NetworkPeerId peerId, out float posX, out float posY);

        /// <summary>
        /// Returns true when the remote peer is present in the same scene as the local player.
        /// </summary>
        bool IsRemoteInSameScene(NetworkPeerId peerId);

        /// <summary>
        /// Queries the complete interpolated state of the remote player clone.
        /// </summary>
        bool TryGetRemoteState(NetworkPeerId peerId, out RemoteCloneState state);

        /// <summary>
        /// Checks whether a remote clone is currently active for the given peer.
        /// </summary>
        bool IsPlayerSpawned(NetworkPeerId peerId);

        /// <summary>
        /// Advances the simulation clock by deltaTime seconds, interpolating all active remote clones.
        /// </summary>
        void UpdateInterpolation(double currentLocalTimeMs);

        /// <summary>
        /// Destroys all spawned clones and resets state on session disconnect.
        /// </summary>
        void ResetAllClones();
    }
}
