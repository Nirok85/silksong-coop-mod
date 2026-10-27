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
    public class RemotePlayerController : MonoBehaviour
    {
        private const double TargetDelayMs = 50.0; // 50ms adaptive jitter delay window

        private NetworkPeerId _peerId;
        private bool _isPlayer2;
        private readonly SnapshotBuffer _buffer = new SnapshotBuffer(64);
        private readonly Dictionary<int, tk2dSpriteAnimationClip> _clipHashLookup = new Dictionary<int, tk2dSpriteAnimationClip>();

        private tk2dSprite? _sprite;
        private tk2dSpriteAnimator? _animator;
        private PaletteSwapController? _paletteSwap;
        private DashTrailController? _dashTrail;
        private tk2dSpriteAnimationClip? _fallbackClip;

        private float _currentVelX;
        private float _currentVelY;
        private bool _facingRight = true;
        private ushort _stateFlags;
        private int _currentClipHash;
        private float _currentNormalizedTime;
        private uint _lastTimestampMs;
        private bool _hasClockOffset;
        private double _clockOffsetMs;
        private bool _isInSameScene = true;

        public NetworkPeerId PeerId => _peerId;
        public bool IsPlayer2 => _isPlayer2;
        public SnapshotBuffer Buffer => _buffer;
        public bool FacingRight => _facingRight;
        public ushort StateFlags => _stateFlags;
        public uint LastTimestampMs => _lastTimestampMs;
        public bool IsInSameScene => _isInSameScene;

        private void Awake()
        {
            _sprite = GetComponent<tk2dSprite>();
            _animator = GetComponent<tk2dSpriteAnimator>();
            _paletteSwap = GetComponent<PaletteSwapController>();
            _dashTrail = GetComponent<DashTrailController>();
        }

        public void Initialize(NetworkPeerId peerId, bool isPlayer2)
        {
            _peerId = peerId;
            _isPlayer2 = isPlayer2;
            _lastTimestampMs = 0;
            _hasClockOffset = false;
            _clockOffsetMs = 0.0;
            _isInSameScene = true;
            _buffer.Clear();
            _paletteSwap?.SetPlayer2(isPlayer2);
            _dashTrail?.Initialize(isPlayer2);
            SetVisibility(true);
        }

        public void SetVisibility(bool visible)
        {
            var renderers = GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null) renderers[i].enabled = visible;
            }
        }

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

        public void UpdateTransform(
            float posX, float posY,
            float velX, float velY,
            byte facingDirection,
            ushort stateFlags,
            int animClipHash,
            ushort normalizedTime,
            uint timestampMs,
            bool isSameScene = true)
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
                return;
            }
            else
            {
                if (!_isInSameScene)
                {
                    _isInSameScene = true;
                    SetVisibility(true);
                    transform.position = new Vector3(posX, posY, transform.position.z);
                    _hasClockOffset = false;
                    _buffer.Clear();
                }
            }

            // Proper packet ordering: handle both normal progression and clock wrap detection
            // A packet is newer if:
            // 1. LastTimestampMs is 0 (first packet), OR
            // 2. Timestamp is ahead (normal case), OR
            // 3. Timestamp is behind by more than 10 seconds (clock wrap / session reset)
            // The 10-second threshold prevents rejecting legitimate session resets while
            // still filtering out stale packets from minor network jitter.
            long diff = (long)timestampMs - (long)_lastTimestampMs;
            bool isNewerPacket = !_hasClockOffset || diff > 0 || diff < -10000;
            if (isNewerPacket)
            {
                // The sender's timestamps come from its own session clock. Map them into
                // this client's local clock once so SnapshotBuffer can compare against
                // Time.timeAsDouble during interpolation. Without this the buffer never
                // brackets the render time and the clone freezes at its spawn point.
                double localNowMs = Time.timeAsDouble * 1000.0;
                if (!_hasClockOffset)
                {
                    _clockOffsetMs = localNowMs - timestampMs;
                    _hasClockOffset = true;
                    // Immediately snap position on first received packet to prevent staying at spawn/default coordinates
                    transform.position = new Vector3(posX, posY, transform.position.z);
                }
                double mappedTimestampMs = timestampMs + _clockOffsetMs;

                _buffer.AddSnapshot(new TransformSnapshot
                {
                    Tick = timestampMs,
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
                _lastTimestampMs = timestampMs;

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
        }

        public void TriggerAction(ActionType action, byte variant)
        {
            if (action == ActionType.AirDash || action == ActionType.HarpoonDash)
            {
                _dashTrail?.TriggerDash(action == ActionType.AirDash);
            }
            else if (action == ActionType.SlashNormal || action == ActionType.SlashUp || action == ActionType.SlashDown)
            {
                PerformSlashAttack(action);
            }
        }

        private void PerformSlashAttack(ActionType action)
        {
            try
            {
                Vector2 origin = transform.position;
                Vector2 size = new Vector2(2.5f, 1.8f);
                Vector2 offset = Vector2.zero;

                if (action == ActionType.SlashNormal)
                {
                    offset = new Vector2(_facingRight ? 1.2f : -1.2f, 0f);
                }
                else if (action == ActionType.SlashUp)
                {
                    offset = new Vector2(0f, 1.5f);
                    size = new Vector2(2.0f, 2.5f);
                }
                else if (action == ActionType.SlashDown)
                {
                    offset = new Vector2(0f, -1.2f);
                    size = new Vector2(2.0f, 2.0f);
                }

                Vector2 hitCenter = origin + offset;
                var colliders = Physics2D.OverlapBoxAll(hitCenter, size, 0f);
                if (colliders != null && colliders.Length > 0)
                {
                    int damage = 5;
                    var pd = PlayerData.instance;
                    if (pd != null)
                    {
                        damage += pd.nailUpgrades * 3;
                    }

                    for (int i = 0; i < colliders.Length; i++)
                    {
                        var col = colliders[i];
                        if (col == null || col.isTrigger) continue;
                        var hm = col.GetComponent<HealthManager>() ?? col.GetComponentInParent<HealthManager>();
                        if (hm != null && !hm.GetIsDead())
                        {
                            using (RemoteApplyScope.Begin())
                            {
                                var hit = new HitInstance
                                {
                                    Source = gameObject,
                                    DamageDealt = damage,
                                    AttackType = AttackTypes.Nail,
                                    MoveAngle = _facingRight ? 0f : 180f,
                                    MoveDirection = _facingRight,
                                    Multiplier = 1f,
                                    MagnitudeMultiplier = 1f,
                                    CircleDirection = false,
                                    SpecialType = SpecialTypes.None,
                                    IsHeroDamage = true
                                };
                                hm.Hit(hit);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[RemotePlayerController] PerformSlashAttack error: {ex.Message}");
            }
        }

        public void SyncAnimation(int clipHash, ushort normalizedTime)
        {
            _currentClipHash = clipHash;
            _currentNormalizedTime = AnimationClipHasher.DecodeNormalizedTime(normalizedTime);
            SyncClip(clipHash, normalizedTime);
        }

        public void SetFacingDirection(bool facingRight)
        {
            _facingRight = facingRight;
            transform.localScale = new Vector3(facingRight ? 1f : -1f, 1f, 1f);
        }

        public void TriggerDashEffect(bool isAirDash)
        {
            _dashTrail?.TriggerDash(isAirDash);
        }

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

            if (clip != null && _animator.CurrentClip != clip)
            {
                float normTime = AnimationClipHasher.DecodeNormalizedTime(normalizedTime);
                float duration = (clip.frames != null && clip.frames.Length > 0 && clip.fps > 0f)
                    ? (clip.frames.Length / clip.fps)
                    : 1.0f;
                float startTime = normTime * duration;
                _animator.PlayFrom(clip, startTime);
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
                double renderTime = currentLocalMs - TargetDelayMs;

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
                        // Extrapolate past newest snapshot via DeadReckoning
                        float extrapDt = (float)(renderTime - s1.TimestampMs) / 1000f;
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
                    }

                    if (_sprite != null && _sprite.Collection == null && heroAnimator.Sprite != null)
                    {
                        _sprite.SetSprite(heroAnimator.Sprite.Collection, heroAnimator.Sprite.spriteId);
                        _paletteSwap?.ApplyPaletteSwap();
                    }
                }
            }

            // Keep sorting layer synchronized with local hero
            var meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer != null)
            {
                var heroRenderer = hero.GetComponent<Renderer>() ?? hero.GetComponentInChildren<Renderer>();
                if (heroRenderer != null && (meshRenderer.sortingLayerID != heroRenderer.sortingLayerID || meshRenderer.sortingOrder != heroRenderer.sortingOrder))
                {
                    meshRenderer.sortingLayerID = heroRenderer.sortingLayerID;
                    meshRenderer.sortingLayerName = heroRenderer.sortingLayerName;
                    meshRenderer.sortingOrder = heroRenderer.sortingOrder;
                }
            }
        }
    }
}
