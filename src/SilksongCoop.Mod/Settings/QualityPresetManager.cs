using System;

namespace SilksongCoop.Mod.Settings
{
    /// <summary>
    /// Manages quality presets for network and visual settings.
    /// </summary>
    public class QualityPresetManager
    {
        public enum QualityLevel
        {
            Low,
            Medium,
            High,
            Custom
        }

        private readonly SilksongCoopPlugin _plugin;
        private QualityLevel _currentLevel = QualityLevel.Medium;

        public QualityLevel CurrentLevel => _currentLevel;

        public QualityPresetManager(SilksongCoopPlugin plugin)
        {
            _plugin = plugin;
            LoadFromConfig();
        }

        private void LoadFromConfig()
        {
            if (_plugin.Config == null) return;

            var configEntry = _plugin.Config.Bind(
                "Quality",
                "Preset",
                QualityLevel.Medium,
                "Quality preset (Low/Medium/High/Custom)"
            );
            _currentLevel = configEntry.Value;
        }

        public void ApplyPreset(QualityLevel level)
        {
            _currentLevel = level;

            // Persist to config
            if (_plugin.Config != null)
            {
                var configEntry = _plugin.Config.Bind(
                    "Quality",
                    "Preset",
                    level,
                    "Quality preset (Low/Medium/High/Custom)"
                );
                configEntry.Value = level;
            }

            // Apply preset settings
            switch (level)
            {
                case QualityLevel.Low:
                    ApplyLowPreset();
                    break;
                case QualityLevel.Medium:
                    ApplyMediumPreset();
                    break;
                case QualityLevel.High:
                    ApplyHighPreset();
                    break;
                case QualityLevel.Custom:
                    // Don't override custom settings
                    break;
            }
        }

        private void ApplyLowPreset()
        {
            // Network: 20 Hz tick rate, minimal bandwidth
            if (_plugin.TickRateHzConfig != null)
                _plugin.TickRateHzConfig.Value = 20;

            // Visuals: disable dash trail
            if (_plugin.EnablePlayer2DashTrailConfig != null)
                _plugin.EnablePlayer2DashTrailConfig.Value = false;
        }

        private void ApplyMediumPreset()
        {
            // Network: 30 Hz tick rate, balanced
            if (_plugin.TickRateHzConfig != null)
                _plugin.TickRateHzConfig.Value = 30;

            // Visuals: enable dash trail
            if (_plugin.EnablePlayer2DashTrailConfig != null)
                _plugin.EnablePlayer2DashTrailConfig.Value = true;
        }

        private void ApplyHighPreset()
        {
            // Network: 60 Hz tick rate, maximum smoothness
            if (_plugin.TickRateHzConfig != null)
                _plugin.TickRateHzConfig.Value = 60;

            // Visuals: enable all effects
            if (_plugin.EnablePlayer2DashTrailConfig != null)
                _plugin.EnablePlayer2DashTrailConfig.Value = true;
        }

        public void MarkAsCustom()
        {
            if (_currentLevel != QualityLevel.Custom)
            {
                _currentLevel = QualityLevel.Custom;
                
                if (_plugin.Config != null)
                {
                    var configEntry = _plugin.Config.Bind(
                        "Quality",
                        "Preset",
                        QualityLevel.Custom,
                        "Quality preset (Low/Medium/High/Custom)"
                    );
                    configEntry.Value = QualityLevel.Custom;
                }
            }
        }
    }
}
