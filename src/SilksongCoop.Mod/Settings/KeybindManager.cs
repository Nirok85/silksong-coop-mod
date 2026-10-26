using System;
using System.Collections.Generic;
using UnityEngine;

namespace SilksongCoop.Mod.Settings
{
    /// <summary>
    /// Manages keybind configuration and persistence via BepInEx config.
    /// Supports both Unity Legacy Input and Unity New Input System (Unity 6 / InputSystem package).
    /// </summary>
    public class KeybindManager
    {
        private readonly Dictionary<string, KeyCode> _keybinds = new Dictionary<string, KeyCode>();
        private readonly SilksongCoopPlugin _plugin;

        // Default keybinds
        public const string ToggleMenu = "ToggleMenu";
        public const string ToggleSpectatorCamera = "ToggleSpectatorCamera";
        public const string ToggleDiagnostics = "ToggleDiagnostics";

        public KeybindManager(SilksongCoopPlugin plugin)
        {
            _plugin = plugin;
            InitializeDefaults();
            LoadFromConfig();
        }

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

        public KeyCode GetKeybind(string action)
        {
            return _keybinds.TryGetValue(action, out var key) ? key : KeyCode.None;
        }

        public void SetKeybind(string action, KeyCode key)
        {
            _keybinds[action] = key;
            
            // Persist to config
            if (_plugin.Config != null)
            {
                var configEntry = _plugin.Config.Bind(
                    "Keybinds",
                    action,
                    key,
                    $"Keybind for {action}"
                );
                configEntry.Value = key;
            }
        }

        public bool IsKeyDown(string action)
        {
            var key = GetKeybind(action);
            return IsKeyTriggered(key);
        }

        public static bool IsKeyTriggered(KeyCode key)
        {
            if (key == KeyCode.None) return false;

            // 1. Try Unity Legacy Input
            try
            {
                if (Input.GetKeyDown(key))
                {
                    return true;
                }
            }
            catch
            {
                // Unity 6 throws InvalidOperationException if PlayerSettings only enables New Input System
            }

            // 2. Try Unity New Input System via reflection if present in Silksong
            return CheckNewInputSystemKey(key);
        }

        private static bool CheckNewInputSystemKey(KeyCode key)
        {
            try
            {
                var keyboardType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
                if (keyboardType == null) return false;

                var currentProp = keyboardType.GetProperty("current", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var keyboard = currentProp?.GetValue(null);
                if (keyboard == null) return false;

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
                    _ => null
                };

                if (keyPropName != null)
                {
                    var keyControlProp = keyboardType.GetProperty(keyPropName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    var keyControl = keyControlProp?.GetValue(keyboard);
                    if (keyControl != null)
                    {
                        var wasPressedProp = keyControl.GetType().GetProperty("wasPressedThisFrame");
                        if (wasPressedProp != null)
                        {
                            return (bool)(wasPressedProp.GetValue(keyControl) ?? false);
                        }
                    }
                }
            }
            catch
            {
                // Fallback safe ignore
            }

            return false;
        }

        public Dictionary<string, KeyCode> GetAllKeybinds()
        {
            return new Dictionary<string, KeyCode>(_keybinds);
        }

        public void ResetToDefaults()
        {
            InitializeDefaults();
            
            // Persist defaults to config
            if (_plugin.Config != null)
            {
                var keys = new List<string>(_keybinds.Keys);
                foreach (var action in keys)
                {
                    var configEntry = _plugin.Config.Bind(
                        "Keybinds",
                        action,
                        _keybinds[action],
                        $"Keybind for {action}"
                    );
                    configEntry.Value = _keybinds[action];
                }
            }
        }
    }
}
