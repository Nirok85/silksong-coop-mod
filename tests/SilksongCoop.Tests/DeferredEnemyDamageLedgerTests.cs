using System;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests
{
    public sealed class DeferredEnemyDamageLedgerTests
    {
        [Fact]
        public void DuplicateOrOlderEventNeverAddsDamage()
        {
            var ledger = new DeferredEnemyDamageLedger();
            Assert.True(ledger.TryRecord(1, 2, 0, 90, 10, 0, out int total));
            Assert.Equal(10, total);
            Assert.False(ledger.TryRecord(1, 2, 0, 90, 10, 1, out _));
            Assert.True(ledger.TryRecord(1, 2, 1, 80, 10, 2, out total));
            Assert.Equal(20, total);
            Assert.False(ledger.TryRecord(1, 2, 0, 90, 10, 3, out _));
            Assert.Equal(20, ledger.GetDamage(1, 3));
        }

        [Fact]
        public void SenderSerialWrapsThroughZero()
        {
            var ledger = new DeferredEnemyDamageLedger();
            Assert.True(ledger.TryRecord(1, 2, uint.MaxValue, 90, 10, 0, out _));
            Assert.True(ledger.TryRecord(1, 2, 0, 80, 10, 1, out int total));
            Assert.Equal(20, total);
            Assert.False(ledger.TryRecord(1, 2, uint.MaxValue, 90, 10, 2, out _));
        }

        [Fact]
        public void SenderWatermarkSpansEnemiesButNotDifferentSenders()
        {
            var ledger = new DeferredEnemyDamageLedger();
            Assert.True(ledger.TryRecord(1, 2, 10, 90, 10, 0, out _));
            Assert.False(ledger.TryRecord(3, 2, 9, 90, 10, 1, out _));
            Assert.True(ledger.TryRecord(3, 2, 11, 90, 10, 2, out _));
            Assert.True(ledger.TryRecord(1, 4, 0, 70, 20, 3, out int total));
            Assert.Equal(30, total);
        }

        [Fact]
        public void HpIncreaseStartsNewEvidenceAcrossAllSenders()
        {
            var ledger = new DeferredEnemyDamageLedger();
            Assert.True(ledger.TryRecord(1, 2, 1, 50, 10, 0, out _));
            Assert.True(ledger.TryRecord(1, 3, 1, 40, 20, 1, out int total));
            Assert.Equal(30, total);
            Assert.True(ledger.TryRecord(1, 2, 2, 90, 10, 2, out total));
            Assert.Equal(10, total);
        }

        [Fact]
        public void ExpirationAcrossClockWrapCannotReopenReplay()
        {
            var ledger = new DeferredEnemyDamageLedger();
            uint start = uint.MaxValue - 30000;
            Assert.True(ledger.TryRecord(1, 2, 0, 90, 10, start, out _));
            uint expired = unchecked(start + DeferredEnemyDamageLedger.ProofLifetimeMs);
            Assert.Equal(10, ledger.GetDamage(1, unchecked(expired - 1)));
            Assert.Equal(0, ledger.GetDamage(1, expired));
            Assert.False(ledger.TryRecord(1, 2, 0, 90, 10, expired, out _));
            Assert.True(ledger.TryRecord(1, 2, 1, 80, 10, expired, out int total));
            Assert.Equal(10, total);
        }

        [Fact]
        public void LoadedUnloadedTransitionsRetainSenderWatermark()
        {
            var ledger = new DeferredEnemyDamageLedger();
            Assert.True(ledger.TryRecord(1, 2, 1, 90, 10, 0, out _));
            Assert.False(ledger.TryObserve(2, 1));
            Assert.True(ledger.TryObserve(2, 2));
            ledger.Forget(1);
            Assert.Equal(0, ledger.GetDamage(1, 1));
            Assert.False(ledger.TryRecord(1, 2, 2, 80, 10, 2, out _));
            Assert.True(ledger.TryRecord(1, 2, 3, 70, 10, 3, out int total));
            Assert.Equal(10, total);
        }

        [Fact]
        public void EncounterClearPreservesReplayButSessionResetAllowsRestart()
        {
            var ledger = new DeferredEnemyDamageLedger();
            Assert.True(ledger.TryRecord(1, 2, 5, 90, 10, 0, out _));
            ledger.ClearEncounters();
            Assert.Equal(0, ledger.EnemyCount);
            Assert.False(ledger.TryRecord(1, 2, 5, 90, 10, 1, out _));
            ledger.Reset();
            Assert.Equal(0, ledger.SenderCount);
            Assert.True(ledger.TryRecord(1, 2, 0, 90, 10, 2, out _));
        }

        [Fact]
        public void EnemyCapacityFailsClosedAndExpiredEntriesReleaseCapacity()
        {
            var ledger = new DeferredEnemyDamageLedger();
            for (uint i = 1; i <= DeferredEnemyDamageLedger.MaxEnemies; i++)
                Assert.True(ledger.TryRecord(i, 2, i, 90, 10, 0, out _));
            Assert.Equal(DeferredEnemyDamageLedger.MaxEnemies, ledger.EnemyCount);
            uint next = DeferredEnemyDamageLedger.MaxEnemies + 1;
            Assert.False(ledger.TryRecord(next, 2, next, 90, 10, 1, out _));
            Assert.Equal(DeferredEnemyDamageLedger.MaxEnemies, ledger.EnemyCount);
            Assert.True(ledger.TryRecord(next, 2, next, 90, 10,
                DeferredEnemyDamageLedger.ProofLifetimeMs, out int total));
            Assert.Equal(10, total);
            Assert.Equal(1, ledger.EnemyCount);
        }

        [Fact]
        public void SenderCapacityFailsClosedWithoutEviction()
        {
            var ledger = new DeferredEnemyDamageLedger();
            for (ulong peer = 1; peer <= DeferredEnemyDamageLedger.MaxSenders; peer++)
                Assert.True(ledger.TryRecord(1, peer, 0, 90, 1, 0, out _));
            Assert.False(ledger.TryRecord(1, 1000, 0, 90, 100, 0, out _));
            Assert.False(ledger.TryObserve(1000, 0));
            Assert.Equal(DeferredEnemyDamageLedger.MaxSenders, ledger.SenderCount);
            Assert.Equal(DeferredEnemyDamageLedger.MaxSenders, ledger.GetDamage(1, 0));
            Assert.False(ledger.TryRecord(1, 1, 0, 90, 100, 0, out _));
        }

        [Fact]
        public void DamageSaturatesInsteadOfWrappingAndInvalidInputIsRejected()
        {
            var ledger = new DeferredEnemyDamageLedger();
            Assert.False(ledger.TryRecord(0, 2, 0, 90, 1, 0, out _));
            Assert.False(ledger.TryRecord(1, 0, 0, 90, 1, 0, out _));
            Assert.False(ledger.TryRecord(1, 2, 0, -1, 1, 0, out _));
            Assert.False(ledger.TryRecord(1, 2, 0, 90, -1, 0, out _));
            Assert.True(ledger.TryRecord(1, 2, 0, 90, int.MaxValue, 0, out _));
            Assert.True(ledger.TryRecord(1, 2, 1, 80, 10, 1, out int total));
            Assert.Equal(int.MaxValue, total);
        }
    }
}
