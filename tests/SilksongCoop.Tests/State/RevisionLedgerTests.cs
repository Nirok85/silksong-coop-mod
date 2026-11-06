using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests.State
{
    /// <summary>
    /// The pending-state pumps in the Unity controller re-check this ledger before applying
    /// a queued packet. Without that check an older revision that arrived while its object
    /// was not loaded could be applied after a newer revision and roll world state back.
    /// </summary>
    public class RevisionLedgerTests
    {
        [Fact]
        public void UnversionedPackets_AreNeverStale()
        {
            var ledger = new RevisionLedger();
            Assert.False(ledger.IsStale(1, 0, 10));

            ledger.Record(1, 0, 10);

            Assert.False(ledger.IsStale(1, 0, 10));
            Assert.False(ledger.IsStale(1, 0, 11));
        }

        [Fact]
        public void NewerRevisionFromTheSameOrigin_IsApplied()
        {
            var ledger = new RevisionLedger();
            ledger.Record(1, 5, origin: 42);

            Assert.False(ledger.IsStale(1, 6, origin: 42));
            Assert.True(ledger.IsStale(1, 5, origin: 42));
            Assert.True(ledger.IsStale(1, 4, origin: 42));
        }

        [Fact]
        public void RevisionFromAnotherOrigin_IsAlwaysComparable()
        {
            var ledger = new RevisionLedger();
            ledger.Record(1, 100, origin: 42);

            // Local counters are unrelated, so a peer with a lower revision is not stale.
            Assert.False(ledger.IsStale(1, 1, origin: 43));
        }

        [Fact]
        public void UnknownKey_IsNeverStale()
        {
            var ledger = new RevisionLedger();
            Assert.False(ledger.IsStale(99, 1, origin: 1));
        }

        [Fact]
        public void RecordAfterClear_StartsFresh()
        {
            var ledger = new RevisionLedger();
            ledger.Record(1, 10, origin: 1);
            Assert.Equal(1, ledger.Count);

            ledger.Clear();

            Assert.Equal(0, ledger.Count);
            Assert.False(ledger.IsStale(1, 1, origin: 1));
        }

        [Fact]
        public void PendingPumpOrder_NewerDirectApplyWinsOverQueuedOlderRevision()
        {
            // Models the reported sequence: revision 1 is cached while the object is not
            // loaded, revision 2 is applied directly, then the pump runs. The ledger now
            // tells the pump that revision 1 is stale before it touches SaveState.
            var ledger = new RevisionLedger();
            const ulong key = 123;
            const ulong origin = 77;

            Assert.False(ledger.IsStale(key, 1, origin)); // queued, not applied
            Assert.False(ledger.IsStale(key, 2, origin)); // applied directly
            ledger.Record(key, 2, origin);

            Assert.True(ledger.IsStale(key, 1, origin)); // queued pump must skip it
        }
    }
}
