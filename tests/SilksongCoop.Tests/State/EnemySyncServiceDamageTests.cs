using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests.State
{
    /// <summary>
    /// Coverage for the additive remote-hit path used by 2+ player combat. The legacy
    /// monotonic <see cref="EnemySyncService.TryAcceptHit"/> contract is covered elsewhere
    /// and must stay unchanged.
    /// </summary>
    public class EnemySyncServiceDamageTests
    {
        [Fact]
        public void SimultaneousEqualHits_AreCombined()
        {
            var service = new EnemySyncService();
            service.NoteLocalEnemyState(1, 90, dead: false);

            // The peer also reported 90 after its own 10-damage hit. Trusting the
            // absolute value alone would leave the enemy at 90 instead of 80.
            Assert.True(service.TryAcceptRemoteHit(1, 90, dead: false, damage: 10, senderId: 2, sequence: 1, out int hp));

            Assert.Equal(80, hp);
        }

        [Fact]
        public void LowerRemoteHp_IsTrustedWithoutDoubleCounting()
        {
            var service = new EnemySyncService();
            service.NoteLocalEnemyState(1, 90, dead: false);

            // The peer already saw our hit before sending its own, so its absolute 80
            // includes both hits. Subtracting again would produce 70.
            Assert.True(service.TryAcceptRemoteHit(1, 80, dead: false, damage: 10, senderId: 2, sequence: 1, out int hp));

            Assert.Equal(80, hp);
        }

        [Fact]
        public void DuplicateSequence_IsRejected()
        {
            var service = new EnemySyncService();
            service.NoteLocalEnemyState(1, 90, dead: false);

            Assert.True(service.TryAcceptRemoteHit(1, 90, dead: false, damage: 10, senderId: 2, sequence: 7, out int first));
            Assert.Equal(80, first);
            Assert.False(service.TryAcceptRemoteHit(1, 90, dead: false, damage: 10, senderId: 2, sequence: 7, out _));
        }

        [Fact]
        public void TwoPeers_HittingTheSameEnemy_AreCombined()
        {
            var service = new EnemySyncService();
            service.NoteLocalEnemyState(1, 100, dead: false);

            Assert.True(service.TryAcceptRemoteHit(1, 90, dead: false, damage: 10, senderId: 2, sequence: 1, out int first));
            Assert.Equal(90, first);
            Assert.True(service.TryAcceptRemoteHit(1, 90, dead: false, damage: 10, senderId: 3, sequence: 1, out int second));
            Assert.Equal(80, second);
        }

        [Fact]
        public void DeadEnemy_StaysDead()
        {
            var service = new EnemySyncService();
            service.NoteLocalEnemyState(1, 10, dead: false);

            Assert.True(service.TryAcceptRemoteHit(1, 0, dead: true, damage: 10, senderId: 2, sequence: 1, out int deadHp));
            Assert.Equal(0, deadHp);
            Assert.False(service.TryAcceptRemoteHit(1, 0, dead: true, damage: 10, senderId: 2, sequence: 2, out _));
            Assert.False(service.TryAcceptRemoteHit(1, 5, dead: false, damage: 5, senderId: 3, sequence: 1, out _));
        }

        [Fact]
        public void DeathReport_WinsOverAPendingHit()
        {
            var service = new EnemySyncService();
            service.NoteLocalEnemyState(1, 100, dead: false);

            Assert.True(service.TryAcceptRemoteHit(1, 0, dead: true, damage: 10, senderId: 2, sequence: 1, out _));
            Assert.False(service.TryAcceptRemoteHit(1, 90, dead: false, damage: 10, senderId: 3, sequence: 1, out _));
        }

        [Fact]
        public void UnversionedPacket_IsNotDeduplicatedBySequence()
        {
            var service = new EnemySyncService();
            service.NoteLocalEnemyState(1, 100, dead: false);

            Assert.True(service.TryAcceptRemoteHit(1, 90, dead: false, damage: 10, senderId: 2, sequence: 0, out int first));
            Assert.True(service.TryAcceptRemoteHit(1, 90, dead: false, damage: 10, senderId: 2, sequence: 0, out int second));
            Assert.Equal(90, first);
            Assert.Equal(80, second);
        }

        [Fact]
        public void DifferentSimultaneousHits_ConvergeOnTheSameHp()
        {
            // Local player hit for 30, remote player hit for 10.
            var localThirtyFirst = new EnemySyncService();
            localThirtyFirst.NoteLocalEnemyState(1, 70, dead: false);
            Assert.True(localThirtyFirst.TryAcceptRemoteHit(1, 90, dead: false, damage: 10, senderId: 2, sequence: 1, out int first));
            Assert.Equal(60, first);

            var localTenFirst = new EnemySyncService();
            localTenFirst.NoteLocalEnemyState(1, 90, dead: false);
            Assert.True(localTenFirst.TryAcceptRemoteHit(1, 70, dead: false, damage: 30, senderId: 2, sequence: 1, out int second));
            Assert.Equal(60, second);
        }

        [Fact]
        public void CombinedLethalDamage_KillsTheEnemy()
        {
            var service = new EnemySyncService();
            // 15 HP enemy; the local 10-damage hit left 5.
            service.NoteLocalEnemyState(1, 5, dead: false);

            // The remote player's simultaneous 10-damage hit kills it. Clamping to 1 HP
            // would leave a zero-health enemy alive.
            Assert.True(service.TryAcceptRemoteHit(1, 5, dead: false, damage: 10, senderId: 2, sequence: 1, out int hp));
            Assert.Equal(0, hp);

            // Once the combined damage is lethal, later hits cannot resurrect it.
            Assert.False(service.TryAcceptRemoteHit(1, 5, dead: false, damage: 10, senderId: 3, sequence: 1, out _));
        }

        [Fact]
        public void HostSnapshot_MarksHostAuthority()
        {
            var service = new EnemySyncService();

            Assert.True(service.TryAcceptSnapshot(1, 80, dead: false, timestamp: 10));
            Assert.True(service.HasFreshHostAuthority(1));
            Assert.False(service.HasFreshHostAuthority(2));
        }

        [Fact]
        public void HostAuthority_DoesNotResurrectADeadEnemy()
        {
            var service = new EnemySyncService();
            service.NoteLocalEnemyState(1, 10, dead: false);
            Assert.True(service.TryAcceptRemoteHit(1, 0, dead: true, damage: 10, senderId: 2, sequence: 1, out _));

            Assert.False(service.TryAcceptSnapshot(1, 5, dead: false, timestamp: 10));
        }

        [Fact]
        public void HostAuthoritativeClientHit_ClampsPendingLethalTo1Hp()
        {
            var service = new EnemySyncService();
            Assert.True(service.TryAcceptSnapshot(1, 80, dead: false, timestamp: 10));
            Assert.True(service.HasFreshHostAuthority(1));

            // Without authority this would be 0/dead; with a live host it must stay alive
            // until the host confirms the death.
            Assert.True(service.TryAcceptRemoteHit(
                1, 80, dead: false, damage: 100, senderId: 2, sequence: 1,
                out int hp, allowLethal: false));

            Assert.Equal(1, hp);
            Assert.True(service.TryAcceptSnapshot(1, 0, dead: true, timestamp: 11));
        }

        [Fact]
        public void ZeroHpHit_CannotBypassHostConfirmation()
        {
            var service = new EnemySyncService();
            Assert.True(service.TryAcceptSnapshot(1, 10, dead: false, timestamp: 10));

            // EventType=Hit with hp=0 is not an explicit Death packet.
            Assert.True(service.TryAcceptRemoteHit(
                1, 0, dead: false, damage: 0, senderId: 2, sequence: 1,
                out int hp, allowLethal: false));
            Assert.Equal(1, hp);

            // An explicit Death is still accepted.
            Assert.True(service.TryAcceptRemoteHit(
                1, 0, dead: true, damage: 0, senderId: 2, sequence: 2,
                out int deadHp, allowLethal: false));
            Assert.Equal(0, deadHp);
        }

        [Fact]
        public void SequenceWraparound_DoesNotRejectNewPackets()
        {
            var service = new EnemySyncService();
            service.NoteLocalEnemyState(1, 1000, dead: false);

            Assert.True(service.TryAcceptRemoteHit(1, 990, dead: false, damage: 10, senderId: 2, sequence: uint.MaxValue - 1, out _));
            Assert.True(service.TryAcceptRemoteHit(1, 990, dead: false, damage: 10, senderId: 2, sequence: uint.MaxValue, out _));
            Assert.True(service.TryAcceptRemoteHit(1, 990, dead: false, damage: 10, senderId: 2, sequence: 1, out _));
        }
    }
}
