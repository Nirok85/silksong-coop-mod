using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace SilksongCoop.Core.Networking
{
    /// <summary>
    /// Handles connection authentication using challenge-response with HMAC validation.
    /// Prevents unauthorized connections and replay attacks.
    /// </summary>
    public class ConnectionAuthenticator
    {
        private byte[]? _pendingNonce;
        private long _nonceTimestamp;
        private const int NonceSizeBytes = 16;
        private const long NonceExpiryMs = 10000; // 10 seconds to complete handshake

        // Monotonic clock — a system-clock adjustment must not extend or expire a nonce.
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static long NowMs() => _clock.ElapsedMilliseconds;

        /// <summary>
        /// Generates a cryptographically random nonce for challenge-response.
        /// </summary>
        public byte[] GenerateNonce()
        {
            var nonce = new byte[NonceSizeBytes];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(nonce);
            _pendingNonce = nonce;
            _nonceTimestamp = NowMs();
            return nonce;
        }

        /// <summary>
        /// Computes HMAC-SHA256 signature using the room code as shared secret.
        /// </summary>
        public byte[] ComputeSignature(byte[] nonce, string roomCode)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(roomCode));
            return hmac.ComputeHash(nonce);
        }

        /// <summary>
        /// Validates a client's signature against the pending nonce.
        /// Returns true if signature is valid and nonce hasn't expired.
        /// </summary>
        public bool ValidateSignature(byte[] receivedSignature, string roomCode)
        {
            if (_pendingNonce == null)
                return false;

            // Check nonce expiry
            long now = NowMs();
            if (now - _nonceTimestamp > NonceExpiryMs)
            {
                _pendingNonce = null;
                return false;
            }

            // Compute expected signature
            byte[] expectedSignature = ComputeSignature(_pendingNonce, roomCode);

            // Clear pending nonce after validation attempt
            _pendingNonce = null;

            // Constant-time comparison to prevent timing attacks
            if (receivedSignature.Length != expectedSignature.Length)
                return false;

            int result = 0;
            for (int i = 0; i < expectedSignature.Length; i++)
            {
                result |= receivedSignature[i] ^ expectedSignature[i];
            }

            return result == 0;
        }

        /// <summary>
        /// Clears any pending authentication state.
        /// </summary>
        public void Reset()
        {
            _pendingNonce = null;
            _nonceTimestamp = 0;
        }

        /// <summary>
        /// BUG-A3: exposes the still-pending nonce (if any) so the transport can re-send
        /// the SAME challenge on a duplicate handshake instead of generating a fresh one,
        /// which would invalidate a response already in flight.
        /// </summary>
        public bool TryGetPendingNonce(out byte[]? nonce)
        {
            if (_pendingNonce != null &&
                NowMs() - _nonceTimestamp <= NonceExpiryMs)
            {
                nonce = _pendingNonce;
                return true;
            }

            nonce = null;
            return false;
        }
    }
}
