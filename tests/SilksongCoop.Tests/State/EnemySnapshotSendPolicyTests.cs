using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests.State
{
    public class EnemySnapshotSendPolicyTests
    {
        [Fact]
        public void Reconcile_AlwaysSends()
        {
            Assert.True(EnemySnapshotSendPolicy.ShouldSend(
                reconcile: true, known: true, lastHp: 10, hp: 10, initHp: 10,
                dead: false, wasDead: false));
            Assert.True(EnemySnapshotSendPolicy.ShouldSend(
                reconcile: true, known: true, lastHp: 0, hp: 0, initHp: 10,
                dead: true, wasDead: true));
        }

        [Fact]
        public void FirstSeen_IsSentEvenAtFullHealth()
        {
            Assert.True(EnemySnapshotSendPolicy.ShouldSend(
                reconcile: false, known: false, lastHp: 0, hp: 100, initHp: 100,
                dead: false, wasDead: false));
        }

        [Fact]
        public void KnownUnchangedEnemy_IsNotResent()
        {
            Assert.False(EnemySnapshotSendPolicy.ShouldSend(
                reconcile: false, known: true, lastHp: 100, hp: 100, initHp: 100,
                dead: false, wasDead: false));
        }

        [Fact]
        public void DamagedEnemy_IsSent()
        {
            Assert.True(EnemySnapshotSendPolicy.ShouldSend(
                reconcile: false, known: true, lastHp: 100, hp: 90, initHp: 100,
                dead: false, wasDead: false));
        }

        [Fact]
        public void ChangedHp_IsSentEvenAboveInitialValue()
        {
            // A heal/phase change is a real state change, not a duplicate.
            Assert.True(EnemySnapshotSendPolicy.ShouldSend(
                reconcile: false, known: true, lastHp: 90, hp: 120, initHp: 100,
                dead: false, wasDead: false));
        }

        [Fact]
        public void NewlyDeadEnemy_IsSent()
        {
            Assert.True(EnemySnapshotSendPolicy.ShouldSend(
                reconcile: false, known: true, lastHp: 1, hp: 0, initHp: 100,
                dead: true, wasDead: false));
        }

        [Fact]
        public void AlreadyDeadEnemy_IsNotResent()
        {
            Assert.False(EnemySnapshotSendPolicy.ShouldSend(
                reconcile: false, known: true, lastHp: 0, hp: 0, initHp: 100,
                dead: true, wasDead: true));
        }

        [Fact]
        public void UnknownInitHp_FirstSeenStillSends()
        {
            // When reflection cannot resolve initHp, the caller passes a value below the
            // current HP; first-seen must still reach a late joiner.
            Assert.True(EnemySnapshotSendPolicy.ShouldSend(
                reconcile: false, known: false, lastHp: 0, hp: 50, initHp: 0,
                dead: false, wasDead: false));
        }
    }
}
