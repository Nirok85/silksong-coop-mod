using System;
using System.Collections;
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
        private const float GhostEmissionInterval = 0.04f;
        private const float GhostFadeDuration = 0.22f;

        private tk2dSprite? _sourceSprite;
        private DashTrailLifecycle _lifecycle = new DashTrailLifecycle();
        private bool _isPlayer2;
        private float _emissionTimer;
        private GameObject? _rimLightObject;
        private Light? _rimLight;
        private int _lastUpdateFrame = -1;

        public DashTrailPhase CurrentPhase => _lifecycle.CurrentPhase;
        public bool CanTriggerDash => _lifecycle.CanTriggerDash;
        public bool IsPlayer2 => _isPlayer2;

        private void Awake()
        {
            _sourceSprite = GetComponent<tk2dSprite>();
            SetupRimLight();
        }

        public void Initialize(bool isPlayer2)
        {
            _isPlayer2 = isPlayer2;
            UpdateRimLightColor();
        }

        public void TriggerDash(bool isAirDash)
        {
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

        public void InterruptDash()
        {
            _lifecycle.InterruptDash();
        }

        public void UpdateLifecycle(float deltaTime, bool isGrounded, bool isWallSliding)
        {
            _lastUpdateFrame = Time.frameCount;
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

        private void Update()
        {
            if (_lastUpdateFrame == Time.frameCount) return;
            // By default, if not externally ticked, tick with Time.deltaTime
            UpdateLifecycle(Time.deltaTime, isGrounded: false, isWallSliding: false);
        }

        private void EmitGhostAfterImage()
        {
            try
            {
                if (_sourceSprite == null || _sourceSprite.Collection == null) return;

                // Try to reuse a pooled ghost instance
                DashGhostInstance ghost;
                GameObject ghostGo;

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
                    ghost = ghostGo.AddComponent<DashGhostInstance>();
                }

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
                        ghostSprite.EnableKeyword(PaletteSwapMath.KeywordHueShift);
                        ghostSprite.SetFloat(PaletteSwapController.GetHueShiftPropertyId(), PaletteSwapMath.Player2HueShift);
                    }
                }

                ghost.Reset(ghostSprite, trailColor, GhostFadeDuration);

                // Start coroutine to return ghost to pool after fade completes
                if (this != null && gameObject != null)
                {
                    StartCoroutine(ReturnGhostToPool(ghost, GhostFadeDuration + 0.1f));
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[DashTrailController] EmitGhostAfterImage error: {ex.Message}");
            }
        }

        private IEnumerator ReturnGhostToPool(DashGhostInstance ghost, float delay)
        {
            // Note: yield cannot be used in try-catch blocks, so we use null checks instead
            yield return new WaitForSeconds(delay);
            
            if (ghost == null || ghost.gameObject == null)
            {
                yield break;
            }
            
            if (_ghostPool != null && _ghostPool.Count < MaxPoolSize)
            {
                ghost.gameObject.SetActive(false);
                _ghostPool.Enqueue(ghost);
            }
            else
            {
                Destroy(ghost.gameObject);
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
        }

        private class DashGhostInstance : MonoBehaviour
        {
            private tk2dSprite? _sprite;
            private Color _initialColor;
            private float _duration;
            private float _elapsed;
            private bool _isActive;

            public void Initialize(tk2dSprite? sprite, Color initialColor, float duration)
            {
                _sprite = sprite;
                _initialColor = initialColor;
                _duration = duration;
                _elapsed = 0f;
                _isActive = true;
            }

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

        // Object pool for ghost instances to reduce GC pressure
        private readonly Queue<DashGhostInstance> _ghostPool = new Queue<DashGhostInstance>();
        private const int MaxPoolSize = 20;
    }
}
