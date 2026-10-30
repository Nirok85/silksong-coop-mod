using System;

namespace SilksongCoop.Core.Networking
{
    /// <summary>
    /// Adapts network tick rate based on connection quality.
    /// </summary>
    public class AdaptiveTickRate
    {
        private readonly float _minTickHz = 15f;
        private readonly float _maxTickHz = 60f;
        private readonly float _defaultTickHz = 30f;

        private float _currentTickHz;
        private float _lastUpdateTime;

        public float CurrentTickHz => _currentTickHz;

        /// <summary>
        /// Seconds between network ticks. Never returns zero or infinity, even if the
        /// configured tick rate was invalid.
        /// </summary>
        public float CurrentInterval => 1f / Math.Max(_minTickHz, _currentTickHz);

        public AdaptiveTickRate(float defaultHz = 30f)
        {
            // A zero, negative or NaN rate would make CurrentInterval infinite and silently
            // stop the transform stream, so fall back to the 30 Hz default.
            if (float.IsNaN(defaultHz) || float.IsInfinity(defaultHz) || defaultHz <= 0f)
                defaultHz = 30f;

            _defaultTickHz = Math.Min(Math.Max(defaultHz, _minTickHz), _maxTickHz);
            _currentTickHz = _defaultTickHz;
            _lastUpdateTime = float.NegativeInfinity; // apply the first sample immediately
        }

        /// <summary>
        /// Updates the adaptive tick rate using the current time (in seconds), RTT and loss.
        /// </summary>
        public void Update(float currentTime, float rttMs, float lossPercent)
        {
            if (currentTime - _lastUpdateTime < 0.5f) return; // Update every 500ms
            _lastUpdateTime = currentTime;

            float targetHz = _defaultTickHz;

            // Reduce tick rate based on RTT
            if (rttMs > 150f)
                targetHz = Math.Min(targetHz, 25f);
            if (rttMs > 250f)
                targetHz = Math.Min(targetHz, 20f);
            if (rttMs > 400f)
                targetHz = Math.Min(targetHz, 15f);

            // Reduce tick rate based on packet loss
            if (lossPercent > 2f)
                targetHz = Math.Min(targetHz, 25f);
            if (lossPercent > 5f)
                targetHz = Math.Min(targetHz, 20f);
            if (lossPercent > 10f)
                targetHz = Math.Min(targetHz, 15f);

            // Smooth transition
            if (targetHz < _currentTickHz)
                _currentTickHz = Math.Max(_minTickHz, _currentTickHz - 2f);
            else if (targetHz > _currentTickHz)
                _currentTickHz = Math.Min(_maxTickHz, _currentTickHz + 1f);

            if (_currentTickHz < _minTickHz) _currentTickHz = _minTickHz;
            if (_currentTickHz > _maxTickHz) _currentTickHz = _maxTickHz;
        }

        public void Reset()
        {
            _currentTickHz = _defaultTickHz;
            _lastUpdateTime = float.NegativeInfinity;
        }
    }
}
