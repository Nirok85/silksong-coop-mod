using System;

namespace SilksongCoop.Core.Visuals
{
    /// <summary>
    /// Phases of the dash trail visual effect lifecycle.
    /// </summary>
    public enum DashTrailPhase : byte
    {
        Idle = 0,
        Active = 1,
        Cooldown = 2
    }

    /// <summary>
    /// Controls the lifecycle and rendering of dash trail visual effects for co-op players.
    /// </summary>
    public interface IDashTrailController
    {
        DashTrailPhase CurrentPhase { get; }
        float ActiveTimeRemaining { get; }
        float CooldownTimeRemaining { get; }
        bool CanTriggerDash { get; }

        void TriggerDash(bool isAirDash, bool isPlayer2);
        void Update(float deltaTime, bool isGrounded, bool isWallSliding);
        void InterruptDash();
        void Reset();
        void GetTrailColor(bool isPlayer2, out float r, out float g, out float b, out float a);
    }

    /// <summary>
    /// Manages dash trail visual effect timing and color for co-op players.
    /// </summary>
    public class DashTrailLifecycle : IDashTrailController
    {
        public const float DefaultDashTime = 0.25f;
        public const float DefaultDashCooldown = 0.40f;

        private DashTrailPhase _phase = DashTrailPhase.Idle;
        private float _activeTime;
        private float _cooldownTime;

        public DashTrailPhase CurrentPhase => _phase;
        public float ActiveTimeRemaining => _activeTime;
        public float CooldownTimeRemaining => _cooldownTime;
        public bool CanTriggerDash => _phase == DashTrailPhase.Idle;

        /// <summary>
        /// Triggers a dash visual effect for the specified player.
        /// </summary>
        /// <param name="isAirDash">True if this is an air dash, false for ground dash.</param>
        /// <param name="isPlayer2">True for player 2, false for player 1.</param>
        public void TriggerDash(bool isAirDash, bool isPlayer2)
        {
            if (!CanTriggerDash) return;
            _phase = DashTrailPhase.Active;
            _activeTime = DefaultDashTime;
            _cooldownTime = DefaultDashCooldown;
        }

        /// <summary>
        /// Updates the dash trail lifecycle state based on elapsed time and player state.
        /// </summary>
        /// <param name="deltaTime">Time elapsed since last update in seconds.</param>
        /// <param name="isGrounded">True if the player is on the ground.</param>
        /// <param name="isWallSliding">True if the player is wall sliding.</param>
        public void Update(float deltaTime, bool isGrounded, bool isWallSliding)
        {
            if (isGrounded || isWallSliding)
            {
                if (_phase == DashTrailPhase.Cooldown)
                {
                    _phase = DashTrailPhase.Idle;
                    _cooldownTime = 0f;
                }
            }

            if (_phase == DashTrailPhase.Active)
            {
                _activeTime -= deltaTime;
                if (_activeTime <= 0f)
                {
                    _phase = DashTrailPhase.Cooldown;
                    _activeTime = 0f;
                }
            }
            else if (_phase == DashTrailPhase.Cooldown)
            {
                _cooldownTime -= deltaTime;
                if (_cooldownTime <= 0f)
                {
                    _phase = DashTrailPhase.Idle;
                    _cooldownTime = 0f;
                }
            }
        }

        /// <summary>
        /// Interrupts an active dash, transitioning to cooldown phase.
        /// </summary>
        public void InterruptDash()
        {
            if (_phase == DashTrailPhase.Active)
            {
                _phase = DashTrailPhase.Cooldown;
                _activeTime = 0f;
            }
        }

        /// <summary>
        /// Resets the dash trail to idle state, clearing all timers.
        /// </summary>
        public void Reset()
        {
            _phase = DashTrailPhase.Idle;
            _activeTime = 0f;
            _cooldownTime = 0f;
        }

        /// <summary>
        /// Gets the trail color for the specified player.
        /// </summary>
        /// <param name="isPlayer2">True for player 2 (cyan), false for player 1 (crimson).</param>
        /// <param name="r">Output red component [0, 1].</param>
        /// <param name="g">Output green component [0, 1].</param>
        /// <param name="b">Output blue component [0, 1].</param>
        /// <param name="a">Output alpha component [0, 1].</param>
        public void GetTrailColor(bool isPlayer2, out float r, out float g, out float b, out float a)
        {
            if (isPlayer2)
            {
                // Player 2 Azure / Cyan Dash Trail
                r = 0.15f;
                g = 0.75f;
                b = 0.95f;
                a = 0.80f;
            }
            else
            {
                // Player 1 Hornet Crimson / Red Dash Trail
                r = 0.90f;
                g = 0.15f;
                b = 0.20f;
                a = 0.80f;
            }
        }
    }
}
