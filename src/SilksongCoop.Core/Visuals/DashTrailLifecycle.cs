using System;

namespace SilksongCoop.Core.Visuals
{
    public enum DashTrailPhase : byte
    {
        Idle = 0,
        Active = 1,
        Cooldown = 2
    }

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

        public void TriggerDash(bool isAirDash, bool isPlayer2)
        {
            if (!CanTriggerDash) return;
            _phase = DashTrailPhase.Active;
            _activeTime = DefaultDashTime;
            _cooldownTime = DefaultDashCooldown;
        }

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

        public void InterruptDash()
        {
            if (_phase == DashTrailPhase.Active)
            {
                _phase = DashTrailPhase.Cooldown;
                _activeTime = 0f;
            }
        }

        public void Reset()
        {
            _phase = DashTrailPhase.Idle;
            _activeTime = 0f;
            _cooldownTime = 0f;
        }

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
