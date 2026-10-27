using System;
using System.Collections.Generic;
using SilksongCoop.Core.Interpolation;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.Visuals;

namespace SilksongCoop.Core.Clones
{
    /// <summary>
    /// Headless, engine-agnostic implementation of IPlayerCloneManager.
    /// Manages remote clone state, snapshot buffers, Hermite spline interpolation,
    /// dead reckoning, and dash lifecycle for automated testing and headless simulation.
    /// </summary>
    public class HeadlessPlayerCloneManager : IPlayerCloneManager
    {
        private class CloneEntry
        {
            public NetworkPeerId PeerId;
            public bool IsPlayer2;
            public SnapshotBuffer Buffer = new SnapshotBuffer(64);
            public DashTrailLifecycle DashLifecycle = new DashTrailLifecycle();
            public float CurrentPosX;
            public float CurrentPosY;
            public float CurrentVelX;
            public float CurrentVelY;
            public bool FacingRight = true;
            public ushort StateFlags;
            public int ActiveClipHash;
            public float NormalizedTime;
            public double LastLocalTimeMs;
            public uint LastTimestampMs;
        }

        private readonly Dictionary<NetworkPeerId, CloneEntry> _clones = new Dictionary<NetworkPeerId, CloneEntry>();
        private readonly double _targetDelayMs;

        /// <summary>
        /// Initializes a new instance of the headless clone manager.
        /// </summary>
        /// <param name="targetDelayMs">Target render delay in milliseconds for interpolation.</param>
        public HeadlessPlayerCloneManager(double targetDelayMs = 50.0)
        {
            _targetDelayMs = targetDelayMs;
        }

        public int ActiveCloneCount => _clones.Count;

        /// <summary>
        /// Spawns a remote player clone or resets an existing one.
        /// </summary>
        /// <param name="peerId">Network peer identifier for the remote player.</param>
        /// <param name="isPlayer2">True if this is the second player, false otherwise.</param>
        public void SpawnRemotePlayer(NetworkPeerId peerId, bool isPlayer2)
        {
            if (_clones.TryGetValue(peerId, out var existing))
            {
                // Edge Case E01: Re-spawning peer cleanly resets existing clone and buffer
                existing.Buffer.Clear();
                existing.DashLifecycle.Reset();
                existing.IsPlayer2 = isPlayer2;
                existing.CurrentPosX = 0f;
                existing.CurrentPosY = 0f;
                existing.CurrentVelX = 0f;
                existing.CurrentVelY = 0f;
                existing.FacingRight = true;
                existing.StateFlags = 0;
                existing.ActiveClipHash = AnimationClipHasher.ComputeHash("Idle");
                existing.NormalizedTime = 0f;
                return;
            }

            var entry = new CloneEntry
            {
                PeerId = peerId,
                IsPlayer2 = isPlayer2,
                ActiveClipHash = AnimationClipHasher.ComputeHash("Idle"),
                FacingRight = true
            };
            _clones[peerId] = entry;
        }

        /// <summary>
        /// Despawns a remote player clone. Safe no-op if peer doesn't exist.
        /// </summary>
        /// <param name="peerId">Network peer identifier to despawn.</param>
        public void DespawnRemotePlayer(NetworkPeerId peerId)
        {
            // Edge Case E02: Despawning non-existent peer is a safe no-op
            _clones.Remove(peerId);
        }

        /// <summary>
        /// Updates the remote player's transform state from a network packet.
        /// </summary>
        /// <param name="peerId">Target remote player.</param>
        /// <param name="posX">X position in world space.</param>
        /// <param name="posY">Y position in world space.</param>
        /// <param name="velX">X velocity in units per second.</param>
        /// <param name="velY">Y velocity in units per second.</param>
        /// <param name="facingDirection">Facing direction (0 = left, non-zero = right).</param>
        /// <param name="stateFlags">Bit flags for state (grounded, dashing, etc.).</param>
        /// <param name="animClipHash">Hash of the current animation clip.</param>
        /// <param name="normalizedTime">Normalized animation time [0, 1].</param>
        /// <param name="timestampMs">Packet timestamp in milliseconds.</param>
        public void UpdateRemoteTransform(
            NetworkPeerId peerId,
            float posX, float posY,
            float velX, float velY,
            byte facingDirection,
            ushort stateFlags,
            int animClipHash,
            ushort normalizedTime,
            uint timestampMs)
        {
            if (!_clones.TryGetValue(peerId, out var clone))
            {
                return;
            }

            // Proper packet ordering: handle both normal progression and clock wrap detection
            // A packet is newer if:
            // 1. LastTimestampMs is 0 (first packet), OR
            // 2. Timestamp is ahead (normal case), OR
            // 3. Timestamp is behind by more than 10 seconds (clock wrap / session reset)
            // The 10-second threshold prevents rejecting legitimate session resets while
            // still filtering out stale packets from minor network jitter.
            long diff = (long)timestampMs - (long)clone.LastTimestampMs;
            bool isNewerPacket = clone.LastTimestampMs == 0 || diff > 0 || diff < -10000;
            
            if (isNewerPacket)
            {
                clone.Buffer.AddSnapshot(new TransformSnapshot
                {
                    Tick = timestampMs,
                    TimestampMs = timestampMs,
                    PositionX = posX,
                    PositionY = posY,
                    VelocityX = velX,
                    VelocityY = velY,
                    FacingRight = facingDirection != 0,
                    Flags = stateFlags,
                    AnimHash = animClipHash
                });

                clone.FacingRight = facingDirection != 0;
                clone.StateFlags = stateFlags;
                clone.ActiveClipHash = animClipHash != 0 ? animClipHash : AnimationClipHasher.ComputeHash("Idle");
                clone.NormalizedTime = AnimationClipHasher.DecodeNormalizedTime(normalizedTime);
                clone.LastTimestampMs = timestampMs;

                // Trigger dash lifecycle if FlagDashing is active
                if ((stateFlags & TransformSyncPacket.FlagDashing) != 0 && clone.DashLifecycle.CanTriggerDash)
                {
                    clone.DashLifecycle.TriggerDash(isAirDash: (stateFlags & TransformSyncPacket.FlagGrounded) == 0, clone.IsPlayer2);
                }
            }
        }

        /// <summary>
        /// Triggers a remote action on the specified player clone.
        /// </summary>
        /// <param name="peerId">Target remote player.</param>
        /// <param name="action">Type of action to trigger.</param>
        /// <param name="variant">Action variant or parameter.</param>
        public void TriggerRemoteAction(NetworkPeerId peerId, ActionType action, byte variant)
        {
            if (!_clones.TryGetValue(peerId, out var clone)) return;

            if (action == ActionType.AirDash || action == ActionType.HarpoonDash)
            {
                clone.DashLifecycle.TriggerDash(isAirDash: action == ActionType.AirDash, clone.IsPlayer2);
            }
        }

        /// <summary>
        /// Synchronizes animation state for a remote player.
        /// </summary>
        /// <param name="peerId">Target remote player.</param>
        /// <param name="clipHash">Animation clip hash.</param>
        /// <param name="normalizedTime">Normalized animation time [0, 1].</param>
        public void SyncAnimation(NetworkPeerId peerId, int clipHash, ushort normalizedTime)
        {
            if (!_clones.TryGetValue(peerId, out var clone)) return;
            clone.ActiveClipHash = clipHash != 0 ? clipHash : AnimationClipHasher.ComputeHash("Idle");
            clone.NormalizedTime = AnimationClipHasher.DecodeNormalizedTime(normalizedTime);
        }

        /// <summary>
        /// Sets the facing direction for a remote player.
        /// </summary>
        /// <param name="peerId">Target remote player.</param>
        /// <param name="facingRight">True if facing right, false if facing left.</param>
        public void SetFacingDirection(NetworkPeerId peerId, bool facingRight)
        {
            if (!_clones.TryGetValue(peerId, out var clone)) return;
            clone.FacingRight = facingRight;
        }

        /// <summary>
        /// Triggers the dash visual effect for a remote player.
        /// </summary>
        /// <param name="peerId">Target remote player.</param>
        /// <param name="isAirDash">True if this is an air dash, false if ground dash.</param>
        public void TriggerDashEffect(NetworkPeerId peerId, bool isAirDash)
        {
            if (!_clones.TryGetValue(peerId, out var clone)) return;
            clone.DashLifecycle.TriggerDash(isAirDash, clone.IsPlayer2);
        }

        /// <summary>
        /// Attempts to retrieve the current position of a remote player.
        /// </summary>
        /// <param name="peerId">Target remote player.</param>
        /// <param name="posX">Output X position.</param>
        /// <param name="posY">Output Y position.</param>
        /// <returns>True if the player exists and position was retrieved, false otherwise.</returns>
        public bool TryGetRemotePosition(NetworkPeerId peerId, out float posX, out float posY)
        {
            if (_clones.TryGetValue(peerId, out var clone))
            {
                posX = clone.CurrentPosX;
                posY = clone.CurrentPosY;
                return true;
            }
            posX = 0f;
            posY = 0f;
            return false;
        }

        /// <summary>
        /// Attempts to retrieve the full state of a remote player.
        /// </summary>
        /// <param name="peerId">Target remote player.</param>
        /// <param name="state">Output remote clone state.</param>
        /// <returns>True if the player exists and state was retrieved, false otherwise.</returns>
        public bool TryGetRemoteState(NetworkPeerId peerId, out RemoteCloneState state)
        {
            if (_clones.TryGetValue(peerId, out var clone))
            {
                state = new RemoteCloneState
                {
                    PeerId = clone.PeerId,
                    IsPlayer2 = clone.IsPlayer2,
                    PositionX = clone.CurrentPosX,
                    PositionY = clone.CurrentPosY,
                    VelocityX = clone.CurrentVelX,
                    VelocityY = clone.CurrentVelY,
                    FacingRight = clone.FacingRight,
                    StateFlags = clone.StateFlags,
                    ActiveClipHash = clone.ActiveClipHash,
                    ClipNormalizedTime = clone.NormalizedTime,
                    IsDashActive = clone.DashLifecycle.CurrentPhase == DashTrailPhase.Active,
                    DashTimeRemaining = clone.DashLifecycle.ActiveTimeRemaining
                };
                return true;
            }
            state = default;
            return false;
        }

        /// <summary>
        /// Checks if a remote player is currently spawned.
        /// </summary>
        /// <param name="peerId">Target remote player.</param>
        /// <returns>True if the player is spawned, false otherwise.</returns>
        public bool IsPlayerSpawned(NetworkPeerId peerId)
        {
            return _clones.ContainsKey(peerId);
        }

        /// <summary>
        /// Updates interpolation for all remote clones based on current time.
        /// </summary>
        /// <param name="currentLocalTimeMs">Current local time in milliseconds.</param>
        public void UpdateInterpolation(double currentLocalTimeMs)
        {
            double renderTime = currentLocalTimeMs - _targetDelayMs;

            foreach (var kvp in _clones)
            {
                var clone = kvp.Value;
                float dt = clone.LastLocalTimeMs > 0 ? Math.Max(0f, (float)(currentLocalTimeMs - clone.LastLocalTimeMs) / 1000f) : 0.016f;
                clone.LastLocalTimeMs = currentLocalTimeMs;

                // Update dash lifecycle state
                bool isGrounded = (clone.StateFlags & TransformSyncPacket.FlagGrounded) != 0;
                bool isWallSliding = (clone.StateFlags & TransformSyncPacket.FlagWallSliding) != 0;
                clone.DashLifecycle.Update(dt, isGrounded, isWallSliding);

                if (clone.Buffer.TryGetSnapshotsForTime(renderTime, out var s0, out var s1, out double t))
                {
                    if (t < 1.0)
                    {
                        // Interpolate between snapshots s0 and s1
                        float segmentDt = (float)(s1.TimestampMs - s0.TimestampMs) / 1000f;
                        var (interpX, interpY) = HermiteSpline.InterpolatePosition2D(
                            s0.PositionX, s0.PositionY,
                            s0.VelocityX, s0.VelocityY,
                            s1.PositionX, s1.PositionY,
                            s1.VelocityX, s1.VelocityY,
                            segmentDt, (float)t);

                        var (velX, velY) = HermiteSpline.InterpolateVelocity2D(
                            s0.PositionX, s0.PositionY,
                            s0.VelocityX, s0.VelocityY,
                            s1.PositionX, s1.PositionY,
                            s1.VelocityX, s1.VelocityY,
                            segmentDt, (float)t);

                        // Edge Case E04: Teleport / extreme distance snap check
                        if (DeadReckoning.ShouldSnap(clone.CurrentPosX, clone.CurrentPosY, interpX, interpY))
                        {
                            clone.CurrentPosX = s1.PositionX;
                            clone.CurrentPosY = s1.PositionY;
                            clone.CurrentVelX = s1.VelocityX;
                            clone.CurrentVelY = s1.VelocityY;
                        }
                        else
                        {
                            clone.CurrentPosX = interpX;
                            clone.CurrentPosY = interpY;
                            clone.CurrentVelX = velX;
                            clone.CurrentVelY = velY;
                        }
                    }
                    else
                    {
                        // Edge Case E03: Packet starvation - extrapolate via DeadReckoning
                        float extrapDt = (float)(renderTime - s1.TimestampMs) / 1000f;
                        if (extrapDt > 0f)
                        {
                            var (extrapX, extrapY) = DeadReckoning.ExtrapolatePosition(
                                s1.PositionX, s1.PositionY,
                                s1.VelocityX, s1.VelocityY,
                                isGrounded,
                                extrapDt);

                            clone.CurrentPosX = extrapX;
                            clone.CurrentPosY = extrapY;
                        }
                        else
                        {
                            clone.CurrentPosX = s1.PositionX;
                            clone.CurrentPosY = s1.PositionY;
                        }

                        clone.CurrentVelX = s1.VelocityX;
                        clone.CurrentVelY = s1.VelocityY;
                    }
                }
            }
        }

        /// <summary>
        /// Resets and despawns all remote player clones.
        /// </summary>
        public void ResetAllClones()
        {
            _clones.Clear();
        }
    }
}
