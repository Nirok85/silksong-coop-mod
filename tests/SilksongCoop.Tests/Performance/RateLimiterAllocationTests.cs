using System;
using SilksongCoop.Core.Networking;
using Xunit;

namespace SilksongCoop.Tests.Performance
{
    /// <summary>
    /// Allocation regression test for the rate limiter hot path. A peer's bucket is created
    /// once; afterwards TryConsume must not allocate (in particular, GetOrAdd must not build
    /// a fresh value-factory delegate on every packet). The warm-up covers JIT compilation,
    /// bucket insertion and the first GetOrAdd call.
    /// </summary>
    public class RateLimiterAllocationTests
    {
        private const int WarmupIterations = 1000;
        private const int MeasuredIterations = 1000;

        [Fact]
        public void TryConsume_KnownPeerWithTokens_AllocatesNothingAfterWarmup()
        {
            var limiter = new RateLimiter(maxTokens: 4096, refillRatePerSecond: 256);
            var peer = new NetworkPeerId(42, "Peer42");

            _ = GC.GetAllocatedBytesForCurrentThread();

            long consumed = 0;
            for (int i = 0; i < WarmupIterations; i++)
            {
                if (limiter.TryConsume(peer)) consumed++;
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < MeasuredIterations; i++)
            {
                if (limiter.TryConsume(peer)) consumed++;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
            // 2000 calls against a 4096-token bucket all succeed; the count also proves the
            // calls were not elided by the JIT.
            Assert.Equal(WarmupIterations + MeasuredIterations, consumed);
            Assert.Equal(1, limiter.BucketCount);
        }
    }
}
