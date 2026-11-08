using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests.State
{
    /// <summary>
    /// Regressions for the 3.3.2 audit: a client cannot declare a kill on the host, an
    /// authoritative host hit that already contains the local hit is not subtracted twice,
    /// and a snapshot generated before a local hit cannot raise HP back.
    /// </summary>
    public class EnemySyncRemediationTests
    {
        [Fact]
        public void LocalHost_ClientDeathClaim_DoesNotKillWhenHostHpSurvives()
        {
            var service = new EnemySyncService();
            service.NoteCurrentEnemyState(1, 100, dead: false);

            // The client's local simulation diverged to 5 HP and reports a lethal 10-hit.
            // The host still has 100 HP, so the claim must become ordinary damage.
            Assert.True(service.TryAcceptRemoteHit(
                1, 0, dead: true, damage: 10, senderId: 2, sequence: 1,
                RemoteHitPolicy.LocalHost, out int hp));

            Assert.Equal(90, hp);
        }

        [Fact]
        public void LocalHost_ClientDeathClaim_WithZeroDamage_DoesNotKill()
        {
            var service = new EnemySyncService();
            service.NoteCurrentEnemyState(1, 80, dead: false);

            Assert.True(service.TryAcceptRemoteHit(
                1, 0, dead: true, damage: 0, senderId: 2, sequence: 1,
                RemoteHitPolicy.LocalHost, out int hp));

            Assert.Equal(80, hp);
        }

        [Fact]
        public void LocalHost_CombinedLethalDamage_ConfirmsTheKill()
        {
            var service = new EnemySyncService();
            service.NoteCurrentEnemyState(1, 5, dead: false);

            Assert.True(service.TryAcceptRemoteHit(
                1, 0, dead: true, damage: 10, senderId: 2, sequence: 1,
                RemoteHitPolicy.LocalHost, out int hp));

            Assert.Equal(0, hp);
        }

        [Fact]
        public void LocalHost_WithoutBaseline_DoesNotAcceptRemoteDeath()
        {
            var service = new EnemySyncService();

            Assert.True(service.TryAcceptRemoteHit(
                1, 0, dead: true, damage: 50, senderId: 2, sequence: 1,
                RemoteHitPolicy.LocalHost, out int hp));

            Assert.Equal(1, hp);
        }

        [Fact]
        public void AuthoritativeHostHit_AlreadyLowerThanLocalHit_IsAbsolute()
        {
            var service = new EnemySyncService();
            Assert.True(service.TryAcceptSnapshot(1, 100, dead: false, timestamp: 10));
            service.NoteLocalEnemyState(1, 90, dead: false);

            // The host is at 85 because it applied the local hit and then its own damage.
            // Subtracting the host's 10-damage event from 90 would incorrectly produce 80.
            Assert.True(service.TryAcceptRemoteHit(
                1, 85, dead: false, damage: 10, senderId: 2, sequence: 1,
                RemoteHitPolicy.Authoritative, out int hp));

            Assert.Equal(85, hp);
        }

        [Fact]
        public void SnapshotOlderThanLocalHit_DoesNotRaiseHp()
        {
            var service = new EnemySyncService();
            Assert.True(service.TryAcceptSnapshot(1, 100, dead: false, timestamp: 10));
            service.NoteLocalEnemyState(1, 90, dead: false);

            // A snapshot captured before the local hit still says 100. It must not undo the
            // hit; authority and ordering advance but the local HP stays at 90.
            Assert.False(service.TryAcceptSnapshot(1, 100, dead: false, timestamp: 11));

            // A subsequent snapshot with a lower value is still applied normally.
            Assert.True(service.TryAcceptSnapshot(1, 85, dead: false, timestamp: 12));
        }

        [Fact]
        public void SnapshotOlderThanLocalHit_StillConfirmsDeath()
        {
            var service = new EnemySyncService();
            Assert.True(service.TryAcceptSnapshot(1, 100, dead: false, timestamp: 10));
            service.NoteLocalEnemyState(1, 90, dead: false);

            Assert.True(service.TryAcceptSnapshot(1, 0, dead: true, timestamp: 11));
        }
    }
}
