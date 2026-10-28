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
        /// Derives the session key used to authenticate post-handshake datagrams.
        /// Domain-separated from the challenge signature: the signature itself travels on
        /// the wire and must not be usable as a MAC key.
        /// </summary>
        public byte[] ComputeSessionKey(byte[] nonce, string roomCode)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(roomCode));
            byte[] label = Encoding.UTF8.GetBytes("SilksongCoop-Session-v4");
            byte[] message = new byte[label.Length + nonce.Length];
            Buffer.BlockCopy(label, 0, message, 0, label.Length);
            Buffer.BlockCopy(nonce, 0, message, label.Length, nonce.Length);
            return hmac.ComputeHash(message);
        }

        /// <summary>
        /// Validates a client's signature against the pending nonce.
        /// Returns true if signature is valid and nonce hasn't expired.
        /// </summary>
        public bool ValidateSignature(byte[] receivedSignature, string roomCode)
        {
            return ValidateSignature(receivedSignature, roomCode, out _);
        }

        /// <summary>
        /// Validates the challenge response and, on success, returns the derived session key
        /// (null when validation failed).
        /// </summary>
        public bool ValidateSignature(byte[] receivedSignature, string roomCode, out byte[]? sessionKey)
        {
            sessionKey = null;
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

            // Constant-time comparison to prevent timing attacks
            bool valid = receivedSignature.Length == expectedSignature.Length;
            if (valid)
            {
                int result = 0;
                for (int i = 0; i < expectedSignature.Length; i++)
                {
                    result |= receivedSignature[i] ^ expectedSignature[i];
                }
                valid = result == 0;
            }

            // Keep the nonce on a failed attempt: an unauthenticated (possibly spoofed)
            // response must not invalidate the challenge for the legitimate client that is
            // still about to answer. Brute force is infeasible because the key carries
            // 80 random bits and the nonce expires after 10 seconds.
            if (valid)
            {
                sessionKey = ComputeSessionKey(_pendingNonce, roomCode);
                _pendingNonce = null;
            }

            return valid;
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
