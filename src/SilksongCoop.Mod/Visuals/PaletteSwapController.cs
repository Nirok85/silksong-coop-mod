using System;
using SilksongCoop.Core.Visuals;
using UnityEngine;

namespace SilksongCoop.Mod.Visuals
{
    /// <summary>
    /// Controls runtime GPU palette swapping on tk2dSprite and MeshRenderer using Hollow Knight: Silksong's
    /// native CAN_HUESHIFT shader keyword and _HueShift float property.
    /// Preserves white mask, obsidian horns, and steel needle while shifting Hornet's cloak
    /// to radiant azure blue for Player 2.
    /// </summary>
    public class PaletteSwapController : MonoBehaviour
    {
        private tk2dSprite[]? _sprites;
        private Renderer[]? _renderers;
        private MaterialPropertyBlock? _propertyBlock;
        private bool _isPlayer2;
        private float _hueShift = PaletteSwapMath.Player2HueShift;
        private int _huePropId;
        private float _lastAppliedHueShift = -1f;
        private bool _needsUpdate = true;

        public bool IsPlayer2 => _isPlayer2;
        public float HueShift => _hueShift;

        private void Awake()
        {
            _propertyBlock = new MaterialPropertyBlock();
            _huePropId = GetHueShiftPropertyId();
            CacheComponents();
        }

        private void CacheComponents()
        {
            _sprites = GetComponentsInChildren<tk2dSprite>(true);
            _renderers = GetComponentsInChildren<Renderer>(true);
        }

        private void Start()
        {
            CacheComponents();
            if (_isPlayer2)
            {
                ApplyPaletteSwap();
                StartCoroutine(DelayedApplyPaletteSwap());
            }
        }

        private System.Collections.IEnumerator DelayedApplyPaletteSwap()
        {
            yield return null;
            yield return null;
            CacheComponents();
            _needsUpdate = true;
            ApplyPaletteSwap();
        }

        public void SetPlayer2(bool isPlayer2, float customHueShift = PaletteSwapMath.Player2HueShift)
        {
            try
            {
                _isPlayer2 = isPlayer2;
                _hueShift = customHueShift;
                _needsUpdate = true;
                ApplyPaletteSwap();
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[PaletteSwapController] SetPlayer2 error: {ex.Message}");
            }
        }

        public void ApplyPaletteSwap()
        {
            try
            {
                if (gameObject == null || !gameObject.activeInHierarchy || !enabled)
                {
                    return;
                }

                if (_huePropId == 0)
                {
                    _huePropId = GetHueShiftPropertyId();
                }

                if (_sprites == null || _sprites.Length == 0 || _renderers == null || _renderers.Length == 0)
                {
                    CacheComponents();
                }

                float targetHue = _isPlayer2 ? _hueShift : PaletteSwapMath.Player1HueShift;

                // 1. Apply to all tk2dSprites
                if (_sprites != null)
                {
                    for (int i = 0; i < _sprites.Length; i++)
                    {
                        var sprite = _sprites[i];
                        if (sprite == null) continue;

                        try
                        {
                            if (_isPlayer2)
                            {
                                sprite.EnableKeyword(PaletteSwapMath.KeywordHueShift);
                                sprite.DisableKeyword(PaletteSwapMath.KeywordRecolour);
                                sprite.SetFloat(_huePropId, targetHue);
                                sprite.color = Color.white;
                            }
                            else
                            {
                                sprite.DisableKeyword(PaletteSwapMath.KeywordHueShift);
                                sprite.DisableKeyword(PaletteSwapMath.KeywordRecolour);
                                sprite.SetFloat(_huePropId, PaletteSwapMath.Player1HueShift);
                            }
                        }
                        catch (Exception spriteEx)
                        {
                            SilksongCoopPlugin.LogWarn($"[PaletteSwapController] Sprite operation failed: {spriteEx.Message}");
                        }
                    }
                }

                // 2. Apply to all Renderers (both direct material keywords and MaterialPropertyBlock)
                if (_renderers != null)
                {
                    if (_propertyBlock == null)
                    {
                        _propertyBlock = new MaterialPropertyBlock();
                    }

                    for (int i = 0; i < _renderers.Length; i++)
                    {
                        var ren = _renderers[i];
                        if (ren == null) continue;

                        try
                        {
                            // A. Material Property Block
                            ren.GetPropertyBlock(_propertyBlock);
                            _propertyBlock.SetFloat(_huePropId, targetHue);
                            _propertyBlock.SetFloat(PaletteSwapMath.ShaderPropertyHueShift, targetHue);
                            ren.SetPropertyBlock(_propertyBlock);

                            // B. Direct materials on the renderer (required for GPU keyword activation)
                            var materials = ren.materials;
                            if (materials != null)
                            {
                                for (int m = 0; m < materials.Length; m++)
                                {
                                    var mat = materials[m];
                                    if (mat == null) continue;

                                    if (_isPlayer2)
                                    {
                                        mat.EnableKeyword(PaletteSwapMath.KeywordHueShift);
                                        mat.DisableKeyword(PaletteSwapMath.KeywordRecolour);
                                        mat.SetFloat(_huePropId, targetHue);
                                        mat.SetFloat(PaletteSwapMath.ShaderPropertyHueShift, targetHue);
                                    }
                                    else
                                    {
                                        mat.DisableKeyword(PaletteSwapMath.KeywordHueShift);
                                        mat.SetFloat(_huePropId, PaletteSwapMath.Player1HueShift);
                                        mat.SetFloat(PaletteSwapMath.ShaderPropertyHueShift, PaletteSwapMath.Player1HueShift);
                                    }
                                }
                            }
                        }
                        catch (Exception rendererEx)
                        {
                            SilksongCoopPlugin.LogWarn($"[PaletteSwapController] Renderer operation failed: {rendererEx.Message}");
                        }
                    }
                }

                _lastAppliedHueShift = targetHue;
                _needsUpdate = false;
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[PaletteSwapController] ApplyPaletteSwap error: {ex.Message}");
            }
        }

        private void LateUpdate()
        {
            try
            {
                // Enforce palette swap in LateUpdate after all animations and state changes
                if (_isPlayer2)
                {
                    ApplyPaletteSwap();
                }
                else if (_needsUpdate)
                {
                    ApplyPaletteSwap();
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[PaletteSwapController] LateUpdate error: {ex.Message}");
            }
        }

        internal static int GetHueShiftPropertyId()
        {
            try
            {
                int id = PoisonTintBase.HueShiftPropId;
                if (id != 0) return id;
            }
            catch
            {
                // Fallback to Shader.PropertyToID if PoisonTintBase is not loaded
            }

            return Shader.PropertyToID(PaletteSwapMath.ShaderPropertyHueShift);
        }
    }
}
