using System.Net;
using System.Reflection;
using SilksongCoop.Core.Networking;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Regression tests for transport hardening that cannot be exercised by sending
    /// billions of packets through a real socket.
    /// </summary>
    public class NetworkHardeningTests
    {
        private const BindingFlags StaticPrivate =
            BindingFlags.NonPublic | BindingFlags.Static;

        [Fact]
        public void SequenceComparison_IsWrapAware()
        {
            var newer = typeof(LiteNetLibTransport).GetMethod("IsSequenceNewer", StaticPrivate);
            var older = typeof(LiteNetLibTransport).GetMethod("IsSequenceOlder", StaticPrivate);
            Assert.NotNull(newer);
            Assert.NotNull(older);

            // Ordinary ordering.
            Assert.True((bool)newer!.Invoke(null, new object[] { 1u, 0u })!);
            Assert.False((bool)newer.Invoke(null, new object[] { 0u, 1u })!);
            Assert.True((bool)older!.Invoke(null, new object[] { 0u, 1u })!);
            Assert.False((bool)older.Invoke(null, new object[] { 1u, 0u })!);

            // 0xFFFFFFFF -> 0 is the next sequence, not a huge backward jump.
            Assert.True((bool)newer.Invoke(null, new object[] { 0u, uint.MaxValue })!);
            Assert.False((bool)newer.Invoke(null, new object[] { uint.MaxValue, 0u })!);
            Assert.True((bool)older.Invoke(null, new object[] { uint.MaxValue, 0u })!);
            Assert.False((bool)older.Invoke(null, new object[] { 0u, uint.MaxValue })!);
        }

        [Fact]
        public void PreAuthRateLimit_IsScopedToTheSourceEndpoint()
        {
            using var transport = new LiteNetLibTransport(1, "Host");
            var method = typeof(LiteNetLibTransport).GetMethod(
                "IsPreAuthAllowed",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var flooder = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 7777);
            var legitimate = new IPEndPoint(IPAddress.Parse("10.0.0.2"), 7777);

            for (int i = 0; i < 32; i++)
                Assert.True((bool)method!.Invoke(transport, new object[] { flooder })!);

            // The flooding endpoint is out of tokens...
            Assert.False((bool)method.Invoke(transport, new object[] { flooder })!);

            // ...but a different endpoint still has its own budget.
            Assert.True((bool)method.Invoke(transport, new object[] { legitimate })!);
        }

        [Fact]
        public void RateLimiter_Clear_DropsAllBuckets()
        {
            var limiter = new RateLimiter(maxTokens: 1, refillRatePerSecond: 0);
            var peer = new NetworkPeerId(42);

            Assert.True(limiter.TryConsume(peer));
            Assert.False(limiter.TryConsume(peer));
            Assert.Equal(1, limiter.BucketCount);

            limiter.Clear();

            Assert.Equal(0, limiter.BucketCount);
            Assert.True(limiter.TryConsume(peer));
        }
    }
}
