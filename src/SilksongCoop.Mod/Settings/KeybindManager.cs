using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SilksongCoop.Mod.Settings
{
    /// <summary>
    /// Manages keybind configuration and persistence via BepInEx config.
    /// Supports both Unity Legacy Input and Unity New Input System (Unity 6 / InputSystem package).
    /// </summary>
    public class KeybindManager
    {
        #region Fields

        private readonly Dictionary<string, KeyCode> _keybinds = new Dictionary<string, KeyCode>();
        private readonly SilksongCoopPlugin _plugin;

        // Default keybinds
        public const string ToggleMenu = "ToggleMenu";
        public const string ToggleSpectatorCamera = "ToggleSpectatorCamera";
        public const string ToggleDiagnostics = "ToggleDiagnostics";

        private static Type? _cachedKeyboardType;
        private static bool _keyboardTypeResolved;
        private static PropertyInfo? _cachedCurrentProp;
        private static PropertyInfo? _cachedWasPressedProp;
        private static readonly Dictionary<KeyCode, PropertyInfo?> _cachedKeyControlProps = new();

        // Log-once guards: Unity's input APIs can throw or be unavailable, and the
        // checks run every frame for every key — log the diagnostic only once.
        private static bool _legacyInputLogged;
        private static bool _legacyInputUnavailable;
        private static bool _newInputLogged;
        private static bool _keyboardTypeLogged;
        private static bool _keyboardCurrentLogged;
        private static bool _newInputExceptionLogged;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new keybind manager with default bindings.
        /// </summary>
        /// <param name="plugin">The plugin instance for config persistence.</param>
        public KeybindManager(SilksongCoopPlugin plugin)
        {
            _plugin = plugin;
            InitializeDefaults();
            LoadFromConfig();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Gets the keybind for the specified action.
        /// </summary>
        /// <param name="action">The action name.</param>
        /// <returns>The bound key, or None if not found.</returns>
        public KeyCode GetKeybind(string action)
        {
            return _keybinds.TryGetValue(action, out var key) ? key : KeyCode.None;
        }

        /// <summary>
        /// Checks if the key for the specified action is currently pressed.
        /// </summary>
        /// <param name="action">The action name.</param>
        /// <returns>True if the key is pressed, false otherwise.</returns>
        public bool IsKeyDown(string action)
        {
            var key = GetKeybind(action);
            return IsKeyTriggered(key);
        }

        /// <summary>
        /// Checks if the specified key was triggered this frame (supports both Legacy and New Input).
        /// </summary>
        /// <param name="key">The key to check.</param>
        /// <returns>True if the key was triggered, false otherwise.</returns>
        public static bool IsKeyTriggered(KeyCode key)
        {
            if (key == KeyCode.None) return false;

            // 1. Try Unity Legacy Input. Once it throws (New-Input-only player
            // settings), stop calling it: an exception per key per frame is expensive.
            if (!_legacyInputUnavailable)
            {
                try
                {
                    if (Input.GetKeyDown(key))
                    {
                        return true;
                    }
                }
                catch (System.Exception ex)
                {
                    // Unity 6 throws InvalidOperationException if PlayerSettings only enables New Input System
                    _legacyInputUnavailable = true;
                    if (!_legacyInputLogged)
                    {
                        _legacyInputLogged = true;
                        Debug.Log($"[Coop] Legacy Input threw {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            // 2. Try Unity New Input System via reflection if present in Silksong
            var result = CheckNewInputSystemKey(key);
            if (result && !_newInputLogged)
            {
                _newInputLogged = true;
                Debug.Log($"[Coop] New Input System detected key press: {key}");
            }
            return result;
        }

        #endregion

        #region Private Methods

        private void InitializeDefaults()
        {
            _keybinds[ToggleMenu] = KeyCode.F10;
            _keybinds[ToggleSpectatorCamera] = KeyCode.F9;
            _keybinds[ToggleDiagnostics] = KeyCode.F11;
        }

        private void LoadFromConfig()
        {
            if (_plugin.Config == null) return;

            // Iterate over a snapshot list of keys to prevent 'Collection was modified' InvalidOperationException
            var keys = new List<string>(_keybinds.Keys);
            foreach (var action in keys)
            {
                var configEntry = _plugin.Config.Bind(
                    "Keybinds",
                    action,
                    _keybinds[action],
                    $"Keybind for {action}"
                );
                _keybinds[action] = configEntry.Value;
            }
        }

        private static bool CheckNewInputSystemKey(KeyCode key)
        {
            try
            {
                // BUG-P15: cache the Keyboard type and its 'current' property — Type.GetType
                // and GetProperty used to run on every IsKeyDown call (every frame, every key).
                if (!_keyboardTypeResolved)
                {
                    _cachedKeyboardType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem") ??
                                          Type.GetType("UnityEngine.InputSystem.Keyboard, UnityEngine.InputSystem") ??
                                          Type.GetType("UnityEngine.InputSystem.Keyboard");
                    _cachedCurrentProp = _cachedKeyboardType?.GetProperty("current", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    _keyboardTypeResolved = true;
                }

                var keyboardType = _cachedKeyboardType;
                if (keyboardType == null)
                {
                    if (!_keyboardTypeLogged)
                    {
                        _keyboardTypeLogged = true;
                        Debug.Log("[Coop] Unity.InputSystem.Keyboard type not found via reflection");
                    }
                    return false;
                }

                var keyboard = _cachedCurrentProp?.GetValue(null);
                if (keyboard == null)
                {
                    if (!_keyboardCurrentLogged)
                    {
                        _keyboardCurrentLogged = true;
                        Debug.Log("[Coop] Keyboard.current is null");
                    }
                    return false;
                }

                string? keyPropName = key switch
                {
                    KeyCode.F1 => "f1Key",
                    KeyCode.F2 => "f2Key",
                    KeyCode.F3 => "f3Key",
                    KeyCode.F4 => "f4Key",
                    KeyCode.F5 => "f5Key",
                    KeyCode.F6 => "f6Key",
                    KeyCode.F7 => "f7Key",
                    KeyCode.F8 => "f8Key",
                    KeyCode.F9 => "f9Key",
                    KeyCode.F10 => "f10Key",
                    KeyCode.F11 => "f11Key",
                    KeyCode.F12 => "f12Key",
                    KeyCode.Escape => "escapeKey",
                    KeyCode.Space => "spaceKey",
                    KeyCode.Return => "enterKey",
                    KeyCode.KeypadEnter => "enterKey",
                    KeyCode.Tab => "tabKey",
                    KeyCode.Backspace => "backspaceKey",
                    KeyCode.Delete => "deleteKey",
                    KeyCode.UpArrow => "upArrowKey",
                    KeyCode.DownArrow => "downArrowKey",
                    KeyCode.LeftArrow => "leftArrowKey",
                    KeyCode.RightArrow => "rightArrowKey",
                    KeyCode.LeftShift => "leftShiftKey",
                    KeyCode.RightShift => "rightShiftKey",
                    KeyCode.LeftControl => "leftCtrlKey",
                    KeyCode.RightControl => "rightCtrlKey",
                    KeyCode.LeftAlt => "leftAltKey",
                    KeyCode.RightAlt => "rightAltKey",
                    KeyCode.Minus => "minusKey",
                    KeyCode.Equals => "equalsKey",
                    KeyCode.LeftBracket => "leftBracketKey",
                    KeyCode.RightBracket => "rightBracketKey",
                    KeyCode.Semicolon => "semicolonKey",
                    KeyCode.Quote => "quoteKey",
                    KeyCode.Comma => "commaKey",
                    KeyCode.Period => "periodKey",
                    KeyCode.Slash => "slashKey",
                    KeyCode.Backslash => "backslashKey",
                    KeyCode.BackQuote => "backquoteKey",
                    _ => null
                };

                // Letters, top-row digits and the numeric keypad are the bindings users
                // most often choose in the config. The Input System names these
                // systematically (aKey..zKey, digit0Key..digit9Key, numpad0Key..).
                if (keyPropName == null)
                {
                    int code = (int)key;
                    if (code >= (int)KeyCode.A && code <= (int)KeyCode.Z)
                    {
                        keyPropName = (char)('a' + (code - (int)KeyCode.A)) + "Key";
                    }
                    else if (code >= (int)KeyCode.Alpha0 && code <= (int)KeyCode.Alpha9)
                    {
                        keyPropName = "digit" + (code - (int)KeyCode.Alpha0) + "Key";
                    }
                    else if (code >= (int)KeyCode.Keypad0 && code <= (int)KeyCode.Keypad9)
                    {
                        keyPropName = "numpad" + (code - (int)KeyCode.Keypad0) + "Key";
                    }
                }

                if (keyPropName != null)
                {
                    // Cache the reflected PropertyInfo per key: IsKeyDown runs for every
                    // bound key on every frame, and GetProperty is not free on Mono.
                    if (!_cachedKeyControlProps.TryGetValue(key, out var keyControlProp))
                    {
                        keyControlProp = keyboardType.GetProperty(keyPropName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        _cachedKeyControlProps[key] = keyControlProp;
                    }

                    var keyControl = keyControlProp?.GetValue(keyboard);
                    if (keyControl != null)
                    {
                        _cachedWasPressedProp ??= keyControl.GetType().GetProperty("wasPressedThisFrame");
                        if (_cachedWasPressedProp != null)
                        {
                            return (bool)(_cachedWasPressedProp.GetValue(keyControl) ?? false);
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                // Fallback safe ignore
                if (!_newInputExceptionLogged)
                {
                    _newInputExceptionLogged = true;
                    Debug.Log($"[Coop] New Input System reflection failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            return false;
        }

        #endregion
    }
}
