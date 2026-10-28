using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SilksongCoop.Core.Networking
{
    /// <summary>
    /// Token bucket rate limiter to prevent packet flooding and DoS attacks.
    /// Each peer gets a separate bucket that refills at a fixed rate.
    /// </summary>
    public class RateLimiter
    {
        // Monotonic clock: a system-clock adjustment must not freeze or over-fill buckets.
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private readonly ConcurrentDictionary<NetworkPeerId, TokenBucket> _buckets = new();
        private readonly int _maxTokens;
        private readonly int _refillRate;

        /// <summary>
        /// Creates a rate limiter with specified capacity and refill rate.
        /// </summary>
        /// <param name="maxTokens">Maximum tokens per peer (bucket capacity)</param>
        /// <param name="refillRatePerSecond">Tokens added per second</param>
        public RateLimiter(int maxTokens = 1000, int refillRatePerSecond = 100)
        {
            _maxTokens = maxTokens;
            _refillRate = refillRatePerSecond;
        }

        /// <summary>
        /// Checks if a peer is allowed to send a packet.
        /// Consumes one token if allowed.
        /// </summary>
        public bool TryConsume(NetworkPeerId peerId)
        {
            var bucket = _buckets.GetOrAdd(peerId, _ => new TokenBucket(_maxTokens, _refillRate));
            return bucket.TryConsume();
        }

        /// <summary>
        /// Removes a peer's bucket (call on disconnect to free memory).
        /// </summary>
        public void RemovePeer(NetworkPeerId peerId)
        {
            _buckets.TryRemove(peerId, out _);
        }

        /// <summary>
        /// Token bucket implementation with time-based refill.
        /// </summary>
        private class TokenBucket
        {
            private readonly int _maxTokens;
            private readonly int _refillRatePerSecond;
            private int _tokens;
            private long _lastRefillTimestamp;
            private readonly object _lock = new();

            public TokenBucket(int maxTokens, int refillRatePerSecond)
            {
                _maxTokens = maxTokens;
                _refillRatePerSecond = refillRatePerSecond;
                _tokens = maxTokens;
                _lastRefillTimestamp = Clock.ElapsedTicks;
            }

            public bool TryConsume()
            {
                lock (_lock)
                {
                    Refill();

                    if (_tokens > 0)
                    {
                        _tokens--;
                        return true;
                    }

                    return false;
                }
            }

            private void Refill()
            {
                long currentTimestamp = Clock.ElapsedTicks;
                long elapsedTicks = currentTimestamp - _lastRefillTimestamp;
                if (elapsedTicks <= 0)
                    return;

                double elapsedSeconds = elapsedTicks / (double)Stopwatch.Frequency;

                int tokensToAdd = (int)(elapsedSeconds * _refillRatePerSecond);

                if (tokensToAdd > 0)
                {
                    _tokens = Math.Min(_maxTokens, _tokens + tokensToAdd);
                    _lastRefillTimestamp = currentTimestamp;
                }
            }
        }
    }
}
