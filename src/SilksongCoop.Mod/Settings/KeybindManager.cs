using System;
using System.Collections.Generic;
using UnityEngine;

namespace SilksongCoop.Mod.Settings
{
    /// <summary>
    /// Manages keybind configuration and persistence via BepInEx config.
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
            // Load from BepInEx config if available
            if (_plugin.Config == null) return;

            foreach (var kvp in _keybinds)
            {
                var configEntry = _plugin.Config.Bind(
                    "Keybinds",
                    kvp.Key,
                    kvp.Value,
                    $"Keybind for {kvp.Key}"
                );
                _keybinds[kvp.Key] = configEntry.Value;
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
            return key != KeyCode.None && Input.GetKeyDown(key);
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
                foreach (var kvp in _keybinds)
                {
                    var configEntry = _plugin.Config.Bind(
                        "Keybinds",
                        kvp.Key,
                        kvp.Value,
                        $"Keybind for {kvp.Key}"
                    );
                    configEntry.Value = kvp.Value;
                }
            }
        }
    }
}
