using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests.State
{
    /// <summary>
    /// Property-style coverage for the revision watermark rules. The deterministic random
    /// walk asserts the ledger never accepts an older revision for the same (object, origin)
    /// while still accepting newer revisions from every origin independently.
    /// </summary>
    public class RevisionLedgerPropertyTests
    {
        [Fact]
        public void RandomWalk_NeverAcceptsOlderRevisionFromSameOrigin()
        {
            var random = new Random(20261112);
            var ledger = new RevisionLedger();
            var lastRecorded = new Dictionary<(ulong Key, ulong Origin), uint>();

            ulong[] keys = { 1, 2, 3 };
            ulong[] origins = { 10, 20 };

            for (int step = 0; step < 500; step++)
            {
                ulong key = keys[random.Next(keys.Length)];
                ulong origin = origins[random.Next(origins.Length)];
                uint revision = (uint)random.Next(1, 5000);
                var slot = (key, origin);

                bool expectedStale = lastRecorded.TryGetValue(slot, out uint last) && revision <= last;
                Assert.Equal(expectedStale, ledger.IsStale(key, revision, origin));

                if (expectedStale) continue;

                ledger.Record(key, revision, origin);
                lastRecorded[slot] = revision;

                // The just-recorded revision is stale against itself; the next serial
                // number is accepted.
                Assert.True(ledger.IsStale(key, revision, origin));
                Assert.False(ledger.IsStale(key, revision + 1, origin));
            }

            Assert.Equal(keys.Length, ledger.Count);
        }

        [Fact]
        public void OriginsAreIndependent_ForTheSameObject()
        {
            var ledger = new RevisionLedger();

            ledger.Record(key: 7, revision: 100, origin: 10);

            // A delayed packet from another origin is not judged against origin 10's
            // watermark; per-origin tracking is the reason it stays applicable.
            Assert.False(ledger.IsStale(key: 7, revision: 1, origin: 20));
            Assert.True(ledger.IsStale(key: 7, revision: 100, origin: 10));
            Assert.True(ledger.IsStale(key: 7, revision: 99, origin: 10));
        }

        [Fact]
        public void SerialArithmetic_HandlesUintWraparound()
        {
            var ledger = new RevisionLedger();

            ledger.Record(key: 1, revision: uint.MaxValue - 2, origin: 1);
            ledger.Record(key: 1, revision: uint.MaxValue, origin: 1);

            // Revision 0 means "unversioned" and is never stale by contract.
            Assert.False(ledger.IsStale(key: 1, revision: 0, origin: 1));
            // Post-wrap revisions are newer than MaxValue in serial arithmetic.
            Assert.False(ledger.IsStale(key: 1, revision: 1, origin: 1));
            Assert.True(ledger.IsStale(key: 1, revision: uint.MaxValue - 1, origin: 1));
        }

        [Fact]
        public void ParallelRecording_KeepsWatermarksConsistent()
        {
            var ledger = new RevisionLedger();
            const int workers = 8;
            const int revisionsPerWorker = 200;

            Parallel.For(0, workers, worker =>
            {
                for (uint revision = 1; revision <= revisionsPerWorker; revision++)
                {
                    ulong key = (ulong)(worker % 3);
                    ledger.Record(key, revision, origin: (ulong)(worker + 1));
                    Assert.False(ledger.IsStale(key, revision + 1, origin: (ulong)(worker + 1)));
                }
            });

            Assert.Equal(3, ledger.Count);
            for (int worker = 0; worker < workers; worker++)
            {
                ulong key = (ulong)(worker % 3);
                ulong origin = (ulong)(worker + 1);
                Assert.True(ledger.IsStale(key, revisionsPerWorker, origin));
            }
        }
    }
}
