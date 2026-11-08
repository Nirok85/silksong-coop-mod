using System;
using System.Collections.Generic;
using SilksongCoop.Core.Visuals;
using UnityEngine;

namespace SilksongCoop.Mod.Visuals
{
    /// <summary>
    /// Drives combat readability FX for Hornet dashes, including:
    /// - High-contrast cyan/azure ghost after-image emitters for Player 2 (and crimson for Player 1)
    /// - 0.25s emission duration, 0.40s cooldown lifecycle with ground/wall reset
    /// - Adaptive rim lighting for silhouette contrast in dark cavern areas
    /// </summary>
    public class DashTrailController : MonoBehaviour
    {
        #region Fields

        private const float GhostEmissionInterval = 0.04f;
        private const float GhostFadeDuration = 0.22f;
        private const int GhostPrewarmCount = 8;

        private tk2dSprite? _sourceSprite;
        private DashTrailLifecycle _lifecycle = new DashTrailLifecycle();
        private bool _isPlayer2;
        private float _emissionTimer;
        private bool _ghostPoolPrewarmed;
        private GameObject? _rimLightObject;
        private Light? _rimLight;
        private int _lastUpdateFrame = -1;

        /// <summary>
        /// When false the owning component drives <see cref="UpdateLifecycle"/> explicitly
        /// (remote clones get accurate grounded/wall-slide flags from their controller).
        /// Self-ticking would otherwise double-advance the lifecycle in the same frame.
        /// </summary>
        public bool SelfTick { get; set; } = true;

        #endregion

        #region Properties

        public DashTrailPhase CurrentPhase => _lifecycle.CurrentPhase;
        public bool CanTriggerDash => _lifecycle.CanTriggerDash;
        public bool IsPlayer2 => _isPlayer2;

        #endregion

        #region Public Methods

        /// <summary>
        /// Initializes the dash trail controller for the specified player.
        /// </summary>
        /// <param name="isPlayer2">True if this is player 2, false for player 1.</param>
        public void Initialize(bool isPlayer2)
        {
            _isPlayer2 = isPlayer2;
            PrewarmGhostPool();
            UpdateRimLightColor();
        }

        /// <summary>
        /// Triggers the dash visual effect.
        /// </summary>
        /// <param name="isAirDash">True if this is an air dash, false for ground dash.</param>
        public void TriggerDash(bool isAirDash)
        {
            if (!IsTrailEnabled()) return;
            if (!_lifecycle.CanTriggerDash) return;

            _lifecycle.TriggerDash(isAirDash, _isPlayer2);
            _emissionTimer = 0f;
            EmitGhostAfterImage();

            if (_rimLight != null && _rimLightObject != null)
            {
                _rimLightObject.SetActive(true);
                _rimLight.intensity = 2.5f;
            }
        }

        /// <summary>
        /// Interrupts an active dash effect.
        /// </summary>
        public void InterruptDash()
        {
            _lifecycle.InterruptDash();
        }

        /// <summary>
        /// Updates the dash trail lifecycle and emits ghost after-images.
        /// </summary>
        /// <param name="deltaTime">Time elapsed since last update.</param>
        /// <param name="isGrounded">True if the player is grounded.</param>
        /// <param name="isWallSliding">True if the player is wall sliding.</param>
        public void UpdateLifecycle(float deltaTime, bool isGrounded, bool isWallSliding)
        {
            // BUG-N9: the controller and the owning MonoBehaviour both ticked the lifecycle
            // in the same frame (RemotePlayerController.Update + DashTrailController.Update),
            // advancing the dash timers twice as fast. Never tick twice per frame.
            if (_lastUpdateFrame == Time.frameCount) return;

            _lastUpdateFrame = Time.frameCount;
            ReapCompletedGhosts();
            _lifecycle.Update(deltaTime, isGrounded, isWallSliding);

            if (_lifecycle.CurrentPhase == DashTrailPhase.Active)
            {
                _emissionTimer += deltaTime;
                if (_emissionTimer >= GhostEmissionInterval)
                {
                    _emissionTimer = 0f;
                    EmitGhostAfterImage();
                }
            }

            // Smoothly decay rim light intensity back to baseline
            if (_rimLight != null && _rimLight.intensity > 0.1f)
            {
                _rimLight.intensity = Mathf.Lerp(_rimLight.intensity, 0f, deltaTime * 5f);
                if (_rimLight.intensity < 0.05f)
                {
                    _rimLight.intensity = 0f;
                    _rimLightObject?.SetActive(false);
                }
            }
        }

        #endregion

        #region Private Methods

        private void Awake()
        {
            // BUG-B8: Hornet's tk2dSprite lives on the animator's child object, not on the
            // hero root this component is attached to — a plain GetComponent() returned
            // null and the dash ghost trail silently never emitted.
            _sourceSprite = GetComponent<tk2dSprite>() ?? GetComponentInChildren<tk2dSprite>();
            SetupRimLight();
        }

        private void Update()
        {
            if (!SelfTick) return;
            if (_lastUpdateFrame == Time.frameCount) return;

            // The local hero's own controller does not tick us, so read the real movement
            // state here. The old hard-coded false/false meant the dash cooldown was never
            // reset on landing or wall contact.
            bool grounded = false;
            bool wallSliding = false;
            var hero = HeroController.instance;
            if (hero != null && hero.cState != null)
            {
                grounded = hero.cState.onGround;
                wallSliding = hero.cState.wallSliding;
            }

            UpdateLifecycle(Time.deltaTime, grounded, wallSliding);
        }

        private bool IsTrailEnabled()
        {
            if (!_isPlayer2) return true;
            return SilksongCoopPlugin.Instance?.EnablePlayer2DashTrailConfig?.Value ?? true;
        }

        private void EmitGhostAfterImage()
        {
            DashGhostInstance? ghost = null;
            GameObject? ghostGo = null;
            try
            {
                if (_sourceSprite == null || _sourceSprite.Collection == null) return;

                // Try to reuse a pooled ghost instance

                if (_ghostPool.Count > 0)
                {
                    ghost = _ghostPool.Dequeue();
                    ghostGo = ghost.gameObject;
                    if (ghostGo == null) return;
                    ghostGo.SetActive(true);
                }
                else
                {
                    ghostGo = new GameObject($"DashGhost_{_isPlayer2}_{Time.frameCount}");
                    // Keep every ghost that can enter the pool alive across scene loads,
                    // matching the prewarmed instances.
                    UnityEngine.Object.DontDestroyOnLoad(ghostGo);
                    ghost = ghostGo.AddComponent<DashGhostInstance>();
                }
                _activeGhosts.Add(ghost);

                ghostGo.transform.position = transform.position;
                ghostGo.transform.rotation = transform.rotation;
                ghostGo.transform.localScale = transform.localScale;

                _lifecycle.GetTrailColor(_isPlayer2, out float r, out float g, out float b, out float a);
                var trailColor = new Color(r, g, b, a);

                var ghostSprite = ghostGo.GetComponent<tk2dSprite>();
                if (ghostSprite == null)
                {
                    ghostSprite = tk2dSprite.AddComponent(ghostGo, _sourceSprite.Collection, _sourceSprite.spriteId);
                }
                else
                {
                    ghostSprite.SetSprite(_sourceSprite.Collection, _sourceSprite.spriteId);
                }

                if (ghostSprite != null)
                {
                    ghostSprite.color = trailColor;
                    ghostSprite.FlipX = _sourceSprite.FlipX;
                    ghostSprite.FlipY = _sourceSprite.FlipY;

                    if (_isPlayer2)
                    {
                        float hueShift = SilksongCoopPlugin.Instance?.Player2HueShiftConfig?.Value
                            ?? PaletteSwapMath.Player2HueShift;
                        ghostSprite.EnableKeyword(PaletteSwapMath.KeywordHueShift);
                        ghostSprite.SetFloat(PaletteSwapController.GetHueShiftPropertyId(), hueShift);
                    }
                }

                ghost.Reset(ghostSprite, trailColor, GhostFadeDuration);
            }
            catch (Exception ex)
            {
                // Do not leave a half-initialized ghost visible or return a partially
                // initialized object to the pool: destroy it and let the next emission
                // create a clean instance.
                if (ghost != null) _activeGhosts.Remove(ghost);
                if (ghostGo != null) Destroy(ghostGo);

                SilksongCoopPlugin.LogWarn($"[DashTrailController] EmitGhostAfterImage error: {ex.Message}");
            }
        }

        private void RecycleGhost(DashGhostInstance? ghost)
        {
            if (ghost == null) return;

            _activeGhosts.Remove(ghost);
            var ghostGo = ghost.gameObject;
            if (ghostGo == null) return;

            ghostGo.SetActive(false);
            if (_ghostPool.Count < MaxPoolSize)
            {
                _ghostPool.Enqueue(ghost);
            }
            else
            {
                Destroy(ghostGo);
            }
        }

        private void PrewarmGhostPool()
        {
            if (_ghostPoolPrewarmed) return;
            _ghostPoolPrewarmed = true;

            int target = Math.Min(GhostPrewarmCount, MaxPoolSize);
            while (_ghostPool.Count < target)
            {
                var ghost = CreateInactiveGhost();
                if (ghost == null) break;
                _ghostPool.Enqueue(ghost);
            }
        }

        private static DashGhostInstance? CreateInactiveGhost()
        {
            var ghostGo = new GameObject("DashGhost_Pooled");
            // Pooled ghosts are root objects and must survive a scene transition; otherwise
            // the pool would hold destroyed references after the controller's scene unloads.
            UnityEngine.Object.DontDestroyOnLoad(ghostGo);
            try
            {
                var ghost = ghostGo.AddComponent<DashGhostInstance>();
                ghostGo.SetActive(false);
                return ghost;
            }
            catch (Exception ex)
            {
                UnityEngine.Object.Destroy(ghostGo);
                SilksongCoopPlugin.LogWarn(
                    $"[DashTrailController] Ghost prewarm failed: {ex.Message}");
                return null;
            }
        }

        private void ReapCompletedGhosts()
        {
            // Completed ghosts deactivate themselves in DashGhostInstance.Update; this
            // once-per-frame pass is the single place that returns or destroys them, so a
            // ghost can never be reaped twice (the old per-ghost coroutine did the same).
            for (int i = _activeGhosts.Count - 1; i >= 0; i--)
            {
                var ghost = _activeGhosts[i];
                if (ghost == null)
                {
                    _activeGhosts.RemoveAt(i);
                    continue;
                }

                if (!ghost.IsComplete) continue;

                // RecycleGhost removes from _activeGhosts and forces the object inactive;
                // this also covers a ghost whose setup threw before Reset() armed the fade.
                RecycleGhost(ghost);
            }
        }

        private void SetupRimLight()
        {
            _rimLightObject = new GameObject("AdaptiveRimLight");
            _rimLightObject.transform.SetParent(transform, false);
            _rimLightObject.transform.localPosition = new Vector3(0f, 0.5f, -0.5f);

            _rimLight = _rimLightObject.AddComponent<Light>();
            _rimLight.type = LightType.Point;
            _rimLight.range = 3.5f;
            _rimLight.intensity = 0f; // OFF by default — only enabled during dash
            _rimLightObject.SetActive(false); // Disable entire light object to avoid any rendering cost
            UpdateRimLightColor();
        }

        private void UpdateRimLightColor()
        {
            if (_rimLight != null)
            {
                _rimLight.color = _isPlayer2
                    ? new Color(0.0f, 0.85f, 1.0f, 1.0f) // Soft cyan for Player 2
                    : new Color(1.0f, 0.25f, 0.25f, 1.0f); // Soft crimson for Player 1
            }
        }

        private void OnDestroy()
        {
            if (_rimLightObject != null)
            {
                Destroy(_rimLightObject);
            }

            // Clean up pooled ghosts
            while (_ghostPool.Count > 0)
            {
                var ghost = _ghostPool.Dequeue();
                if (ghost != null && ghost.gameObject != null)
                {
                    Destroy(ghost.gameObject);
                }
            }

            // Ghosts that were still fading when the clone was destroyed (they are root
            // objects, so Unity does not remove them with the clone).
            for (int i = 0; i < _activeGhosts.Count; i++)
            {
                var ghost = _activeGhosts[i];
                if (ghost != null && ghost.gameObject != null)
                {
                    Destroy(ghost.gameObject);
                }
            }
            _activeGhosts.Clear();
        }

        #endregion

        #region Nested Types

        private class DashGhostInstance : MonoBehaviour
        {
            private tk2dSprite? _sprite;
            private Color _initialColor;
            private float _duration;
            private float _elapsed;
            private bool _isActive;

            /// <summary>
            /// True once the fade has finished; the owning controller then returns the
            /// instance to its pool (or destroys it) from its once-per-frame update.
            /// </summary>
            public bool IsComplete => !_isActive;

            /// <summary>
            /// Initializes the ghost instance with sprite and fade settings.
            /// </summary>
            /// <param name="sprite">The sprite to use for the ghost.</param>
            /// <param name="initialColor">Initial color of the ghost.</param>
            /// <param name="duration">Fade duration in seconds.</param>
            public void Initialize(tk2dSprite? sprite, Color initialColor, float duration)
            {
                _sprite = sprite;
                _initialColor = initialColor;
                _duration = duration;
                _elapsed = 0f;
                _isActive = true;
            }

            /// <summary>
            /// Resets the ghost instance with new sprite and fade settings.
            /// </summary>
            /// <param name="sprite">The sprite to use for the ghost.</param>
            /// <param name="initialColor">Initial color of the ghost.</param>
            /// <param name="duration">Fade duration in seconds.</param>
            public void Reset(tk2dSprite? sprite, Color initialColor, float duration)
            {
                _sprite = sprite;
                _initialColor = initialColor;
                _duration = duration;
                _elapsed = 0f;
                _isActive = true;
            }

            private void Update()
            {
                if (!_isActive) return;

                _elapsed += Time.deltaTime;
                float progress = _elapsed / _duration;
                if (progress >= 1f)
                {
                    _isActive = false;
                    gameObject.SetActive(false);
                    return;
                }

                if (_sprite != null)
                {
                    float alpha = Mathf.Lerp(_initialColor.a, 0f, progress);
                    _sprite.color = new Color(_initialColor.r, _initialColor.g, _initialColor.b, alpha);
                }
            }
        }

        #endregion

        // Object pool for ghost instances to reduce GC pressure
        private readonly Queue<DashGhostInstance> _ghostPool = new Queue<DashGhostInstance>();
        private readonly List<DashGhostInstance> _activeGhosts = new List<DashGhostInstance>();
        private const int MaxPoolSize = 20;
    }
}
