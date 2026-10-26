using System;
using SilksongCoop.Core.Visuals;
using UnityEngine;

namespace SilksongCoop.Mod.Visuals
{
    /// <summary>
    /// Controls runtime GPU palette swapping on tk2dSprite using Hollow Knight: Silksong's
    /// native CAN_HUESHIFT shader keyword and _HueShift float property.
    /// Preserves white mask, obsidian horns, and steel needle while shifting Hornet's cloak
    /// to radiant azure blue for Player 2.
    /// </summary>
    [RequireComponent(typeof(tk2dSprite))]
    public class PaletteSwapController : MonoBehaviour
    {
        private tk2dSprite? _sprite;
        private Renderer? _renderer;
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
            _sprite = GetComponent<tk2dSprite>();
            _renderer = GetComponent<Renderer>();
            _propertyBlock = new MaterialPropertyBlock();
            _huePropId = GetHueShiftPropertyId();
        }

        private void Start()
        {
            if (_isPlayer2)
            {
                ApplyPaletteSwap();
            }
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
                if (_huePropId == 0)
                {
                    _huePropId = GetHueShiftPropertyId();
                }

                if (_sprite != null)
                {
                    if (_isPlayer2)
                    {
                        _sprite.EnableKeyword(PaletteSwapMath.KeywordHueShift);
                        _sprite.DisableKeyword(PaletteSwapMath.KeywordRecolour);
                        _sprite.SetFloat(_huePropId, _hueShift);
                    }
                    else
                    {
                        _sprite.DisableKeyword(PaletteSwapMath.KeywordHueShift);
                        _sprite.DisableKeyword(PaletteSwapMath.KeywordRecolour);
                        _sprite.SetFloat(_huePropId, PaletteSwapMath.Player1HueShift);
                    }
                }

                if (_renderer != null)
                {
                    if (_propertyBlock == null)
                    {
                        _propertyBlock = new MaterialPropertyBlock();
                    }

                    _renderer.GetPropertyBlock(_propertyBlock);
                    _propertyBlock.SetFloat(_huePropId, _isPlayer2 ? _hueShift : PaletteSwapMath.Player1HueShift);
                    _renderer.SetPropertyBlock(_propertyBlock);
                    _lastAppliedHueShift = _isPlayer2 ? _hueShift : PaletteSwapMath.Player1HueShift;
                }
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
                // Optimization: Only re-apply when necessary to avoid redundant GPU updates
                // This prevents per-frame overhead while still handling animation rebuilds
                if (!_needsUpdate && _isPlayer2 && _renderer != null && _propertyBlock != null)
                {
                    // Check if animation system reset the property block
                    _renderer.GetPropertyBlock(_propertyBlock);
                    float currentHue = _propertyBlock.GetFloat(_huePropId);
                    if (Mathf.Abs(currentHue - _hueShift) > 0.001f)
                    {
                        _propertyBlock.SetFloat(_huePropId, _hueShift);
                        _renderer.SetPropertyBlock(_propertyBlock);
                        _lastAppliedHueShift = _hueShift;
                    }
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
