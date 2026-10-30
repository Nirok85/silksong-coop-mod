using System;
using System.Collections.Generic;
using SilksongCoop.Core.Clones;
using SilksongCoop.Core.Interpolation;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.Visuals;
using SilksongCoop.Mod.State;
using SilksongCoop.Mod.Visuals;
using UnityEngine;

namespace SilksongCoop.Mod.Clones
{
    /// <summary>
    /// Drives smooth interpolated 2D transform updates, velocity damping, localScale facing direction,
    /// and tk2d animation playback via deterministic FNV-1a clip name hashes for remote player clones.
    /// Completely isolated from HeroController singleton logic.
    /// </summary>
    /// <remarks>
    /// BUG-19 (thread affinity): <see cref="SilksongCoop.Core.Interpolation.SnapshotBuffer"/> is NOT
    /// thread-safe by design — <see cref="UpdateTransform"/> (invoked from transport data events)
    /// and <see cref="Update"/> both run on the Unity main thread via PollEvents(), so no
    /// synchronization is required today. If PollEvents is ever moved to a background thread,
    /// SnapshotBuffer needs locking first.
    /// </remarks>
    public class RemotePlayerController : MonoBehaviour
    {
        #region Fields and Properties

        private const double TargetDelayMs = 50.0; // 50ms adaptive jitter delay window
        private const double MaxAdaptiveDelayMs = 300.0; // P4: never drift further behind than this

        // P4: current interpolation delay — starts at the 50 ms floor and adapts to the
        // measured RTT via SnapshotBuffer.ComputeAdaptiveDelay (fixed 50 ms starved the
        // interpolation window at WAN pings above ~80 ms and forced constant extrapolation).
        private double _interpolationDelayMs = TargetDelayMs;

        private NetworkPeerId _peerId;
        private bool _isPlayer2;
        private readonly SnapshotBuffer _buffer = new SnapshotBuffer(64);
        private readonly Dictionary<int, tk2dSpriteAnimationClip> _clipHashLookup = new Dictionary<int, tk2dSpriteAnimationClip>();

        private tk2dSprite? _sprite;
        private tk2dSpriteAnimator? _animator;
        private PaletteSwapController? _paletteSwap;
        private DashTrailController? _dashTrail;
        private tk2dSpriteAnimationClip? _fallbackClip;
        private Renderer? _spriteRenderer;
        private Renderer? _heroRenderer;
        private GameObject? _heroRendererOwner;

        private float _currentVelX;
        private float _currentVelY;
        private bool _facingRight = true;
        private ushort _stateFlags;
        private int _currentClipHash;
        private float _currentNormalizedTime;
        private readonly InterpolationClock _clock = new InterpolationClock();
        private double _senderClockFrozenSinceUnscaled = -1.0;

        // Unknown until the first transform or an explicit SceneTransition says otherwise.
        // Starting optimistic made the first desync round compare scene names before any
        // packet had confirmed that both players were in the same room.
        private bool _isInSameScene;

        // Death visibility is independent of scene evidence: a partner that died in this
        // room is hidden, but that does not make their scene unknown.
        private bool _partnerAlive = true;

        public NetworkPeerId PeerId => _peerId;
        public bool IsPlayer2 => _isPlayer2;
        public SnapshotBuffer Buffer => _buffer;
        public bool FacingRight => _facingRight;
        public ushort StateFlags => _stateFlags;
        public uint LastTimestampMs => _clock.LastTimestampMs;
        public bool IsInSameScene => _isInSameScene;
        public bool IsPresentInScene => _isInSameScene && _partnerAlive;

        #endregion

        #region Initialization

        private void Awake()
        {
            _sprite = GetComponent<tk2dSprite>();
            _animator = GetComponent<tk2dSpriteAnimator>();
            _paletteSwap = GetComponent<PaletteSwapController>();
            _dashTrail = GetComponent<DashTrailController>();
        }

        /// <summary>
        /// Initializes the remote player controller with peer and player index information.
        /// </summary>
        /// <param name="peerId">The peer identifier.</param>
        /// <param name="isPlayer2">True if this is player 2, false for player 1.</param>
        public void Initialize(NetworkPeerId peerId, bool isPlayer2)
        {
            _peerId = peerId;
            _isPlayer2 = isPlayer2;
            _clock.Reset();
            _senderClockFrozenSinceUnscaled = -1.0;
            _isInSameScene = false;
            _partnerAlive = true;
            _buffer.Clear();
            float hueShift = SilksongCoopPlugin.Instance?.Player2HueShiftConfig?.Value
                ?? PaletteSwapMath.Player2HueShift;
            _paletteSwap?.SetPlayer2(isPlayer2, hueShift);
            if (_dashTrail != null)
            {
                _dashTrail.Initialize(isPlayer2);
                // BUG-N9: clones are ticked by this controller with real ground/wall flags.
                _dashTrail.SelfTick = false;
            }
            SetVisibility(false);
        }

        /// <summary>
        /// Sets the visibility of all renderers in the remote player hierarchy.
        /// </summary>
        /// <param name="visible">True to show, false to hide.</param>
        public void SetVisibility(bool visible)
        {
            var renderers = GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null) renderers[i].enabled = visible;
            }
        }

        /// <summary>
        /// BUG-20: reacts to an explicit SceneTransitionPacket from the remote peer.
        /// Departure hides the clone and drops buffered snapshots immediately; arrival
        /// re-shows it (the next transform packet re-positions and re-syncs the clock).
        /// </summary>
        /// <param name="present">True if the remote peer is present in this scene.</param>
        public void SetScenePresence(bool present)
        {
            if (present && !_isInSameScene)
            {
                _isInSameScene = true;
                SetVisibility(_partnerAlive);
            }
            else if (!present && _isInSameScene)
            {
                _isInSameScene = false;
                SetVisibility(false);
                _buffer.Clear();
            }
        }

        /// <summary>
        /// Toggles visibility for the partner's death/revive lifecycle without changing
        /// scene evidence. A revive in another room must not make the clone reappear there.
        /// </summary>
        /// <param name="alive">True when the partner is alive.</param>
        public void SetPartnerAlive(bool alive)
        {
            if (_partnerAlive == alive) return;

            _partnerAlive = alive;
            SetVisibility(alive && _isInSameScene);
            if (!alive)
            {
                // Dead players stop streaming transforms; buffered snapshots would keep
                // interpolating their last velocity on the next revive.
                _buffer.Clear();
            }
        }

        /// <summary>
        /// Binds the animation library for clip hash lookup and playback.
        /// </summary>
        /// <param name="library">The tk2d sprite animation library.</param>
        public void SetAnimationLibrary(tk2dSpriteAnimation library)
        {
            if (library == null || library.clips == null) return;

            _clipHashLookup.Clear();
            _fallbackClip = null;

            for (int i = 0; i < library.clips.Length; i++)
            {
                var clip = library.clips[i];
                if (clip != null && !string.IsNullOrEmpty(clip.name))
                {
                    int hash = AnimationClipHasher.ComputeHash(clip.name);
                    _clipHashLookup[hash] = clip;

                    if (clip.name == "Idle")
                    {
                        _fallbackClip = clip;
                    }
                }
            }

            if (_fallbackClip == null && library.clips.Length > 0)
            {
                _fallbackClip = library.clips[0];
            }
        }

        #endregion

        #region State Synchronization

        /// <summary>
        /// Updates the remote player's transform, velocity, animation, and state.
        /// </summary>
        /// <param name="posX">X position.</param>
        /// <param name="posY">Y position.</param>
        /// <param name="velX">X velocity.</param>
        /// <param name="velY">Y velocity.</param>
        /// <param name="facingDirection">Facing direction (0 = left, 1 = right).</param>
        /// <param name="stateFlags">State flags (grounded, dashing, etc.).</param>
        /// <param name="animClipHash">Animation clip hash.</param>
        /// <param name="normalizedTime">Normalized animation time.</param>
        /// <param name="timestampMs">Packet timestamp in milliseconds.</param>
        /// <param name="isSameScene">True if the remote player is in the same scene.</param>
        /// <param name="sequence">Transform sequence; orders updates when the sender clock is frozen.</param>
        public void UpdateTransform(
            float posX, float posY,
            float velX, float velY,
            byte facingDirection,
            ushort stateFlags,
            int animClipHash,
            ushort normalizedTime,
            uint timestampMs,
            bool isSameScene = true,
            uint sequence = 0)
        {
            // CRIT-01: If remote player is in a different room/scene, hide clone and ignore position
            if (!isSameScene)
            {
                if (_isInSameScene)
                {
                    _isInSameScene = false;
                    SetVisibility(false);
                    _buffer.Clear();
                }
                _clock.Reset();
                _senderClockFrozenSinceUnscaled = -1.0;
                return;
            }
            else
            {
                if (!_isInSameScene)
                {
                    _isInSameScene = true;
                    // Only show if alive
                    SetVisibility(_partnerAlive);
                    transform.position = new Vector3(posX, posY, transform.position.z);
                    _clock.Reset();
                    _buffer.Clear();
                }
            }

            // The sender stamps packets with its own game clock, which freezes while it is
            // paused. InterpolationClock maps that clock onto this client's Time.timeAsDouble,
            // re-anchors after a one-sided pause, and uses the sequence to accept updates that
            // share a timestamp instead of dropping them.
            double localNowMs = Time.timeAsDouble * 1000.0;
            bool wasCalibrated = _clock.HasOffset;
            if (!_clock.TryMap(
                    timestampMs,
                    sequence,
                    localNowMs,
                    Time.timeScale > 0f,
                    out double mappedTimestampMs,
                    out bool reanchored,
                    out bool clockFrozen))
            {
                return;
            }

            if (!wasCalibrated)
            {
                // Immediately snap position on the first received packet to prevent staying at spawn/default coordinates.
                transform.position = new Vector3(posX, posY, transform.position.z);
            }
            else if (reanchored)
            {
                // The old mapping would place every later snapshot far from the local render
                // time; dropping stale snapshots is cheaper than interpolating across the gap.
                _buffer.Clear();
            }

            // A live transform proves the partner is alive only if we haven't received a
            // newer death signal. Rely on death packets as the source of truth; do not
            // un-hide a clone that was explicitly marked dead.
            if (!_partnerAlive)
            {
                // Do not auto-resurrect from stale transforms
                return;
            }

            if (clockFrozen)
            {
                if (_senderClockFrozenSinceUnscaled < 0.0)
                    _senderClockFrozenSinceUnscaled = Time.unscaledTimeAsDouble;
            }
            else
            {
                _senderClockFrozenSinceUnscaled = -1.0;
            }            _buffer.AddSnapshot(new TransformSnapshot
            {
                // The sequence lets SnapshotBuffer replace the latest entry when the sender
                // clock is frozen and several packets share one timestamp.
                Tick = sequence != 0 ? sequence : timestampMs,
                TimestampMs = mappedTimestampMs,
                PositionX = posX,
                PositionY = posY,
                VelocityX = velX,
                VelocityY = velY,
                FacingRight = facingDirection != 0,
                Flags = stateFlags,
                AnimHash = animClipHash
            });

            _facingRight = facingDirection != 0;
            _stateFlags = stateFlags;
            _currentClipHash = animClipHash;
            _currentNormalizedTime = AnimationClipHasher.DecodeNormalizedTime(normalizedTime);

            // Update facing direction scale immediately (scale.x: 1.0 for right, -1.0 for left)
            transform.localScale = new Vector3(_facingRight ? 1f : -1f, 1f, 1f);

            // Trigger dash trail if FlagDashing is set
            if ((stateFlags & TransformSyncPacket.FlagDashing) != 0)
            {
                bool isAirDash = (stateFlags & TransformSyncPacket.FlagGrounded) == 0;
                _dashTrail?.TriggerDash(isAirDash);
            }

            // Sync animation clip
            SyncClip(animClipHash, normalizedTime);
        }

        /// <summary>
        /// Triggers a remote action (dash, attack, etc.) on the remote player.
        /// </summary>
        /// <param name="action">The action type to trigger.</param>
        /// <param name="variant">Action variant or parameter.</param>
        public void TriggerAction(ActionType action, byte variant)
        {
            TriggerAction(action, variant, float.NaN, float.NaN);
        }

        /// <summary>
        /// Triggers a remote action. Slash damage itself is replicated by
        /// <see cref="EnemySyncController"/> from the sender's authoritative HealthManager
        /// hit; the clone only plays dash-trail effects here.
        /// </summary>
        public void TriggerAction(ActionType action, byte variant, float originX, float originY)
        {
            if (!_isInSameScene) return;

            if (action == ActionType.AirDash || action == ActionType.HarpoonDash)
            {
                _dashTrail?.TriggerDash(action == ActionType.AirDash);
            }
        }

        /// <summary>
        /// Synchronizes animation state (clip and normalized time).
        /// </summary>
        /// <param name="clipHash">Animation clip hash.</param>
        /// <param name="normalizedTime">Normalized animation time.</param>
        public void SyncAnimation(int clipHash, ushort normalizedTime)
        {
            _currentClipHash = clipHash;
            _currentNormalizedTime = AnimationClipHasher.DecodeNormalizedTime(normalizedTime);
            SyncClip(clipHash, normalizedTime);
        }

        /// <summary>
        /// Sets the facing direction of the remote player.
        /// </summary>
        /// <param name="facingRight">True if facing right, false if facing left.</param>
        public void SetFacingDirection(bool facingRight)
        {
            _facingRight = facingRight;
            transform.localScale = new Vector3(facingRight ? 1f : -1f, 1f, 1f);
        }

        /// <summary>
        /// Triggers the dash visual effect on the remote player.
        /// </summary>
        /// <param name="isAirDash">True if this is an air dash, false for ground dash.</param>
        public void TriggerDashEffect(bool isAirDash)
        {
            _dashTrail?.TriggerDash(isAirDash);
        }

        #endregion

        #region Update and Helper Methods

        /// <summary>
        /// Retrieves the current state of the remote player clone.
        /// </summary>
        /// <returns>The remote clone state.</returns>
        public RemoteCloneState GetState()
        {
            return new RemoteCloneState
            {
                PeerId = _peerId,
                IsPlayer2 = _isPlayer2,
                PositionX = transform.position.x,
                PositionY = transform.position.y,
                VelocityX = _currentVelX,
                VelocityY = _currentVelY,
                FacingRight = _facingRight,
                StateFlags = _stateFlags,
                ActiveClipHash = _currentClipHash,
                ClipNormalizedTime = _currentNormalizedTime,
                IsDashActive = _dashTrail != null && _dashTrail.CurrentPhase == DashTrailPhase.Active,
                DashTimeRemaining = 0f
            };
        }

        private void SyncClip(int clipHash, ushort normalizedTime)
        {
            if (_animator == null) return;

            if (!_clipHashLookup.TryGetValue(clipHash, out var clip))
            {
                clip = _fallbackClip;
            }

            if (clip == null) return;

            float normTime = AnimationClipHasher.DecodeNormalizedTime(normalizedTime);
            float duration = (clip.frames != null && clip.frames.Length > 0 && clip.fps > 0f)
                ? (clip.frames.Length / clip.fps)
                : 1.0f;

            if (_animator.CurrentClip != clip)
            {
                float startTime = normTime * duration;
                _animator.PlayFrom(clip, startTime);
                return;
            }

            // The remote clip is already playing: correct phase drift instead of ignoring
            // NormalizedTime forever (long clips used to desynchronize completely). SetFrame
            // is used with triggerEvent:false so animation events are not re-fired.
            if (clip.frames == null || clip.frames.Length == 0) return;

            int frames = clip.frames.Length;
            int targetFrame = Mathf.Clamp((int)(normTime * frames), 0, frames - 1);
            int currentFrame = _animator.CurrentFrame;
            int diff = Math.Abs(targetFrame - currentFrame);

            // A large difference is either a wrap-around on a looping clip or a stale
            // packet; seeking then would visibly rewind the animation. Leave it alone.
            if (diff <= frames / 2)
            {
                int tolerance = Mathf.Max(1, (int)(frames * 0.15f));
                if (diff > tolerance)
                {
                    _animator.SetFrame(targetFrame, triggerEvent: false);
                }
            }
        }

        private void Update()
        {
            try
            {
                // Auto-bind animation library and sprite collection if local HeroController loaded late
                EnsureAnimationLibraryBound();

                // Evaluate render time for interpolation
                double currentLocalMs = Time.timeAsDouble * 1000.0;

                // P4: adapt the interpolation window to the measured round-trip time.
                // Delay = RTT/2 + 2*jitter + tickInterval, clamped to [50, 300] ms and
                // smoothed so a single RTT spike does not jerk the clone.
                float? rtt = SilksongCoopPlugin.Instance?.MenuUI?.Diagnostics?.CurrentPing;
                if (rtt is float pingMs && pingMs > 0f)
                {
                    double adaptive = SnapshotBuffer.ComputeAdaptiveDelay(pingMs, jitterMs: 15.0, tickIntervalMs: 50.0);
                    adaptive = Math.Clamp(adaptive, TargetDelayMs, MaxAdaptiveDelayMs);
                    _interpolationDelayMs += (adaptive - _interpolationDelayMs) * 0.05;
                }

                double renderTime = currentLocalMs - _interpolationDelayMs;

                bool isGrounded = (_stateFlags & TransformSyncPacket.FlagGrounded) != 0;
                bool isWallSliding = (_stateFlags & TransformSyncPacket.FlagWallSliding) != 0;
                _dashTrail?.UpdateLifecycle(Time.deltaTime, isGrounded, isWallSliding);

                if (_buffer.TryGetSnapshotsForTime(renderTime, out var s0, out var s1, out double t))
                {
                    if (t < 1.0)
                    {
                        float segmentDt = Math.Max(0.001f, (float)(s1.TimestampMs - s0.TimestampMs) / 1000f);
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

                        // Check for snap distance threshold (e.g. teleport / room transition)
                        if (DeadReckoning.ShouldSnap(transform.position.x, transform.position.y, interpX, interpY))
                        {
                            transform.position = new Vector3(s1.PositionX, s1.PositionY, transform.position.z);
                            _currentVelX = s1.VelocityX;
                            _currentVelY = s1.VelocityY;
                        }
                        else
                        {
                            transform.position = new Vector3(interpX, interpY, transform.position.z);
                            _currentVelX = velX;
                            _currentVelY = velY;
                        }
                    }
                    else
                    {
                        // Extrapolate past newest snapshot via DeadReckoning, unless the
                        // sender's game clock has been frozen for a while (paused peer).
                        // Extrapolating a stale velocity for seconds made the clone slide
                        // away from the paused player; hold the newest position instead.
                        bool senderClockFrozen = _senderClockFrozenSinceUnscaled >= 0.0 &&
                            Time.unscaledTimeAsDouble - _senderClockFrozenSinceUnscaled >= 0.3;
                        float extrapDt = senderClockFrozen
                            ? 0f
                            : (float)(renderTime - s1.TimestampMs) / 1000f;
                        if (extrapDt > 0f)
                        {
                            var (extrapX, extrapY) = DeadReckoning.ExtrapolatePosition(
                                s1.PositionX, s1.PositionY,
                                s1.VelocityX, s1.VelocityY,
                                isGrounded,
                                extrapDt);

                            transform.position = new Vector3(extrapX, extrapY, transform.position.z);
                        }
                        else
                        {
                            transform.position = new Vector3(s1.PositionX, s1.PositionY, transform.position.z);
                        }

                        _currentVelX = s1.VelocityX;
                        _currentVelY = s1.VelocityY;
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[RemotePlayerController] Update error for peer {_peerId}: {ex.Message}");
            }
        }

        private void EnsureAnimationLibraryBound()
        {
            var hero = HeroController.instance;
            if (hero == null) return;

            if (_animator != null && _animator.Library == null)
            {
                if (hero.AnimCtrl != null && hero.AnimCtrl.animator != null)
                {
                    var heroAnimator = hero.AnimCtrl.animator;
                    if (heroAnimator.Library != null)
                    {
                        _animator.Library = heroAnimator.Library;
                        SetAnimationLibrary(heroAnimator.Library);
                        SilksongCoopPlugin.LogInfo($"[RemotePlayerController] Animation library bound for peer {_peerId}");
                    }

                    if (_sprite != null && _sprite.Collection == null && heroAnimator.Sprite != null)
                    {
                        _sprite.SetSprite(heroAnimator.Sprite.Collection, heroAnimator.Sprite.spriteId);
                        SilksongCoopPlugin.LogInfo($"[RemotePlayerController] Sprite collection set for peer {_peerId}");
                        _paletteSwap?.ApplyPaletteSwap();
                    }
                }
            }

            // Keep sorting layer synchronized with local hero (via tk2dSprite's renderer).
            // Both renderers are cached: this used to run GetComponent every frame.
            if (_sprite != null)
            {
                if (_spriteRenderer == null) _spriteRenderer = _sprite.GetComponent<Renderer>();
                if (_heroRenderer == null || _heroRendererOwner != hero.gameObject)
                {
                    _heroRenderer = hero.GetComponent<Renderer>() ?? hero.GetComponentInChildren<Renderer>();
                    _heroRendererOwner = hero.gameObject;
                }

                var spriteRenderer = _spriteRenderer;
                var heroRenderer = _heroRenderer;
                if (spriteRenderer != null && heroRenderer != null && (spriteRenderer.sortingLayerID != heroRenderer.sortingLayerID || spriteRenderer.sortingOrder != heroRenderer.sortingOrder))
                {
                    spriteRenderer.sortingLayerID = heroRenderer.sortingLayerID;
                    spriteRenderer.sortingLayerName = heroRenderer.sortingLayerName;
                    spriteRenderer.sortingOrder = heroRenderer.sortingOrder;
                }
            }
        }

        #endregion
    }
}
