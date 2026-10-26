using SilksongCoop.Core.Clones;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;
using Xunit;

namespace SilksongCoop.Tests
{
    public class RemoteCloneStateTests
    {
        [Fact]
        public void SpawnRemotePlayer_CreatesValidClone_AndTracksState()
        {
            var manager = new HeadlessPlayerCloneManager();
            var peer = new NetworkPeerId(2);

            manager.SpawnRemotePlayer(peer, isPlayer2: true);

            Assert.True(manager.IsPlayerSpawned(peer));
            Assert.Equal(1, manager.ActiveCloneCount);
            Assert.True(manager.TryGetRemoteState(peer, out var state));
            Assert.Equal(peer, state.PeerId);
            Assert.True(state.IsPlayer2);
            Assert.True(state.FacingRight);
            Assert.False(state.IsDashActive);
        }

        [Fact]
        public void DespawnRemotePlayer_RemovesClone_AndHandlesNonExistentPeerCleanly()
        {
            var manager = new HeadlessPlayerCloneManager();
            var peer1 = new NetworkPeerId(10);
            var peer2 = new NetworkPeerId(20);

            // Edge Case E02: Despawning non-existent peer is a safe no-op
            manager.DespawnRemotePlayer(peer1);
            Assert.Equal(0, manager.ActiveCloneCount);

            manager.SpawnRemotePlayer(peer2, isPlayer2: false);
            Assert.Equal(1, manager.ActiveCloneCount);

            manager.DespawnRemotePlayer(peer2);
            Assert.Equal(0, manager.ActiveCloneCount);
            Assert.False(manager.IsPlayerSpawned(peer2));
        }

        [Fact]
        public void SpawnRemotePlayer_DuplicateSpawn_CleansUpPriorState()
        {
            var manager = new HeadlessPlayerCloneManager();
            var peer = new NetworkPeerId(5);

            manager.SpawnRemotePlayer(peer, isPlayer2: true);
            manager.UpdateRemoteTransform(
                peer,
                posX: 15f, posY: 8f,
                velX: 4f, velY: 0f,
                facingDirection: 0,
                stateFlags: TransformSyncPacket.FlagGrounded,
                animClipHash: AnimationClipHasher.ComputeHash("Run"),
                normalizedTime: 32768,
                timestampMs: 1000);

            // Edge Case E01: Re-spawning same peer ID cleans up and resets state
            manager.SpawnRemotePlayer(peer, isPlayer2: false);

            Assert.Equal(1, manager.ActiveCloneCount);
            Assert.True(manager.TryGetRemoteState(peer, out var state));
            Assert.False(state.IsPlayer2);
            Assert.Equal(0f, state.PositionX);
            Assert.Equal(0f, state.PositionY);
            Assert.True(state.FacingRight);
        }

        [Fact]
        public void UpdateRemoteTransform_InterpolatesPositionSmoothly()
        {
            var manager = new HeadlessPlayerCloneManager(targetDelayMs: 50.0);
            var peer = new NetworkPeerId(3);
            manager.SpawnRemotePlayer(peer, isPlayer2: true);

            // Feed snapshots at 1000ms and 1040ms
            manager.UpdateRemoteTransform(
                peer,
                posX: 0f, posY: 0f,
                velX: 10f, velY: 0f,
                facingDirection: 1,
                stateFlags: TransformSyncPacket.FlagGrounded,
                animClipHash: AnimationClipHasher.ComputeHash("Run"),
                normalizedTime: 0,
                timestampMs: 1000);

            manager.UpdateRemoteTransform(
                peer,
                posX: 4f, posY: 0f,
                velX: 10f, velY: 0f,
                facingDirection: 1,
                stateFlags: TransformSyncPacket.FlagGrounded,
                animClipHash: AnimationClipHasher.ComputeHash("Run"),
                normalizedTime: 32768,
                timestampMs: 1040);

            // Interpolate at localTime = 1070ms (renderTime = 1070 - 50 = 1020ms, exactly 50% between 1000 and 1040)
            manager.UpdateInterpolation(currentLocalTimeMs: 1070.0);

            Assert.True(manager.TryGetRemotePosition(peer, out float posX, out float posY));
            Assert.True(posX > 1.0f && posX < 3.5f, $"Expected smooth midpoint position, got {posX}");
            Assert.Equal(0f, posY);
        }

        [Fact]
        public void UpdateRemoteTransform_DashingState_TriggersDashLifecycle()
        {
            var manager = new HeadlessPlayerCloneManager();
            var peer = new NetworkPeerId(4);
            manager.SpawnRemotePlayer(peer, isPlayer2: true);

            manager.UpdateRemoteTransform(
                peer,
                posX: 5f, posY: 2f,
                velX: 20f, velY: 0f,
                facingDirection: 1,
                stateFlags: TransformSyncPacket.FlagGrounded | TransformSyncPacket.FlagDashing,
                animClipHash: AnimationClipHasher.ComputeHash("Dash"),
                normalizedTime: 0,
                timestampMs: 2000);

            Assert.True(manager.TryGetRemoteState(peer, out var state));
            Assert.True(state.IsDashing);
            Assert.True(state.IsDashActive);
        }

        [Fact]
        public void TriggerRemoteAction_AirDash_ActivatesDashLifecycle()
        {
            var manager = new HeadlessPlayerCloneManager();
            var peer = new NetworkPeerId(7);
            manager.SpawnRemotePlayer(peer, isPlayer2: true);

            manager.TriggerRemoteAction(peer, ActionType.AirDash, 0);

            Assert.True(manager.TryGetRemoteState(peer, out var state));
            Assert.True(state.IsDashActive);
        }

        [Fact]
        public void SetFacingDirection_UpdatesCloneFacing()
        {
            var manager = new HeadlessPlayerCloneManager();
            var peer = new NetworkPeerId(8);
            manager.SpawnRemotePlayer(peer, isPlayer2: false);

            Assert.True(manager.TryGetRemoteState(peer, out var s1));
            Assert.True(s1.FacingRight);

            manager.SetFacingDirection(peer, false);

            Assert.True(manager.TryGetRemoteState(peer, out var s2));
            Assert.False(s2.FacingRight);
        }

        [Fact]
        public void ResetAllClones_CleansUpAllSpawnedPlayers()
        {
            var manager = new HeadlessPlayerCloneManager();
            manager.SpawnRemotePlayer(new NetworkPeerId(1), isPlayer2: false);
            manager.SpawnRemotePlayer(new NetworkPeerId(2), isPlayer2: true);
            manager.SpawnRemotePlayer(new NetworkPeerId(3), isPlayer2: true);

            Assert.Equal(3, manager.ActiveCloneCount);

            manager.ResetAllClones();

            Assert.Equal(0, manager.ActiveCloneCount);
            Assert.False(manager.IsPlayerSpawned(new NetworkPeerId(1)));
        }
    }
}
