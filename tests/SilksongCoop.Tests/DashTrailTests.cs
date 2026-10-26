using SilksongCoop.Core.Visuals;
using Xunit;

namespace SilksongCoop.Tests
{
    public class DashTrailTests
    {
        [Fact]
        public void DashTrail_InitialState_IsIdleAndReady()
        {
            var controller = new DashTrailLifecycle();
            Assert.Equal(DashTrailPhase.Idle, controller.CurrentPhase);
            Assert.True(controller.CanTriggerDash);
            Assert.Equal(0f, controller.ActiveTimeRemaining);
            Assert.Equal(0f, controller.CooldownTimeRemaining);
        }

        [Fact]
        public void DashTrail_TriggerDash_EntersActivePhaseWithFullDuration()
        {
            var controller = new DashTrailLifecycle();
            controller.TriggerDash(isAirDash: false, isPlayer2: true);

            Assert.Equal(DashTrailPhase.Active, controller.CurrentPhase);
            Assert.False(controller.CanTriggerDash);
            Assert.Equal(DashTrailLifecycle.DefaultDashTime, controller.ActiveTimeRemaining, 3);
        }

        [Fact]
        public void DashTrail_ActivePhaseExpires_TransitionsToCooldown()
        {
            var controller = new DashTrailLifecycle();
            controller.TriggerDash(isAirDash: false, isPlayer2: false);

            // Advance time past dash active duration (0.25s)
            controller.Update(0.26f, isGrounded: false, isWallSliding: false);

            Assert.Equal(DashTrailPhase.Cooldown, controller.CurrentPhase);
            Assert.False(controller.CanTriggerDash);
            Assert.True(controller.CooldownTimeRemaining > 0f);
        }

        [Fact]
        public void DashTrail_CooldownCompletes_ReturnsToIdle()
        {
            var controller = new DashTrailLifecycle();
            controller.TriggerDash(isAirDash: false, isPlayer2: false);

            // Advance through active (0.25s) + cooldown (0.40s)
            controller.Update(0.25f, isGrounded: false, isWallSliding: false);
            controller.Update(0.41f, isGrounded: false, isWallSliding: false);

            Assert.Equal(DashTrailPhase.Idle, controller.CurrentPhase);
            Assert.True(controller.CanTriggerDash);
        }

        [Fact]
        public void DashTrail_GroundTouchDuringCooldown_ImmediatelyResetsToIdle()
        {
            var controller = new DashTrailLifecycle();
            controller.TriggerDash(isAirDash: true, isPlayer2: true);

            // Active finishes in air
            controller.Update(0.25f, isGrounded: false, isWallSliding: false);
            Assert.Equal(DashTrailPhase.Cooldown, controller.CurrentPhase);

            // Hero touches ground before cooldown timer naturally finishes
            controller.Update(0.05f, isGrounded: true, isWallSliding: false);

            Assert.Equal(DashTrailPhase.Idle, controller.CurrentPhase);
            Assert.True(controller.CanTriggerDash);
        }

        [Fact]
        public void DashTrail_WallSlideDuringCooldown_ImmediatelyResetsToIdle()
        {
            var controller = new DashTrailLifecycle();
            controller.TriggerDash(isAirDash: true, isPlayer2: true);

            controller.Update(0.25f, isGrounded: false, isWallSliding: false);
            Assert.Equal(DashTrailPhase.Cooldown, controller.CurrentPhase);

            // Hero clings to wall
            controller.Update(0.02f, isGrounded: false, isWallSliding: true);

            Assert.Equal(DashTrailPhase.Idle, controller.CurrentPhase);
            Assert.True(controller.CanTriggerDash);
        }

        [Fact]
        public void DashTrail_InterruptDash_EntersCooldownImmediately()
        {
            var controller = new DashTrailLifecycle();
            controller.TriggerDash(isAirDash: false, isPlayer2: false);

            Assert.Equal(DashTrailPhase.Active, controller.CurrentPhase);
            controller.InterruptDash();

            Assert.Equal(DashTrailPhase.Cooldown, controller.CurrentPhase);
            Assert.Equal(0f, controller.ActiveTimeRemaining);
        }

        [Fact]
        public void DashTrail_Player2Color_ProvidesHighContrastAzureCyan()
        {
            var controller = new DashTrailLifecycle();
            controller.GetTrailColor(isPlayer2: true, out float r2, out float g2, out float b2, out float a2);

            // Player 2: Blue-dominant Cyan (r < 0.3, b > 0.8)
            Assert.True(b2 > r2);
            Assert.True(b2 > 0.8f);
            Assert.True(g2 > 0.6f);
        }

        [Fact]
        public void DashTrail_Player1Color_ProvidesHornetCrimson()
        {
            var controller = new DashTrailLifecycle();
            controller.GetTrailColor(isPlayer2: false, out float r1, out float g1, out float b1, out float a1);

            // Player 1: Red-dominant Crimson (r > 0.8, b < 0.3)
            Assert.True(r1 > b1);
            Assert.True(r1 > 0.8f);
        }

        [Fact]
        public void DashTrail_RapidTriggerAttempts_SuppressedDuringActiveAndCooldown()
        {
            var controller = new DashTrailLifecycle();
            controller.TriggerDash(isAirDash: false, isPlayer2: true);

            // Secondary trigger while active should be ignored
            controller.TriggerDash(isAirDash: false, isPlayer2: true);
            Assert.Equal(DashTrailPhase.Active, controller.CurrentPhase);

            controller.Update(0.25f, isGrounded: false, isWallSliding: false);
            Assert.Equal(DashTrailPhase.Cooldown, controller.CurrentPhase);

            // Secondary trigger while on cooldown should be ignored
            controller.TriggerDash(isAirDash: false, isPlayer2: true);
            Assert.Equal(DashTrailPhase.Cooldown, controller.CurrentPhase);
        }
    }
}
