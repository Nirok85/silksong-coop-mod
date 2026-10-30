using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SilksongCoop.Core.Networking
{
    /// <summary>
    /// Crockford Base32 room code encoder and decoder for peer-to-peer connection strings.
    /// Packs IPv4 (4 bytes), Port (2 bytes), and CRC checksum (1 byte) into an easily shareable code,
    /// optionally followed by a random 80-bit shared secret. Tolerates case insensitivity, hyphens,
    /// spaces, and ambiguous characters (I, L -> 1; O -> 0).
    /// </summary>
    /// <remarks>
    /// The legacy 12-character code is a deterministic encoding of IP:port, so it is not a
    /// secret: anyone who knows the address can rebuild it. <see cref="EncodeWithSecret"/> appends
    /// 16 random Crockford characters (80 bits) that act as the actual pre-shared key for the
    /// challenge-response handshake, while the first 12 characters still carry the address.
    /// </remarks>
    public static class RoomCodeCodec
    {
        #region Constants

        private const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        private const string Prefix = "SK-";

        /// <summary>Number of random bytes appended by <see cref="EncodeWithSecret"/>.</summary>
        public const int SecretBytes = 10;

        private const int AddressCodeChars = 12;
        private const int SecretCodeChars = 16;
        private const int LegacySecretChars = 8;
        private const int LegacySecretBodyLength = AddressCodeChars + LegacySecretChars;
        private const int SecretCodeBodyLength = AddressCodeChars + SecretCodeChars;

        #endregion

        #region Public Methods

        /// <summary>
        /// Encodes an IP address and port into a room code string.
        /// </summary>
        /// <param name="ip">The IPv4 address to encode.</param>
        /// <param name="port">The port number (0-65535).</param>
        /// <returns>A room code string in the format "SK-XXXX-XXXX-XXXX".</returns>
        public static string Encode(IPAddress ip, int port)
        {
            if (ip == null) throw new ArgumentNullException(nameof(ip));
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port), "Port must be 0..65535");

            byte[] ipBytes = ip.GetAddressBytes();
            if (ipBytes.Length != 4)
                throw new ArgumentException("Only IPv4 addresses are supported in room codes", nameof(ip));

            // 7-byte buffer: 4 bytes IP, 2 bytes port, 1 byte CRC
            byte[] raw = new byte[7];
            Array.Copy(ipBytes, 0, raw, 0, 4);
            raw[4] = (byte)((port >> 8) & 0xFF);
            raw[5] = (byte)(port & 0xFF);
            raw[6] = ComputeCrc8(raw.AsSpan(0, 6));

            // Encode 56 bits into 12 Crockford Base32 characters (60 bits capacity)
            string encoded = Base32Encode56(raw);

            // Format as SK-XXXX-XXXX-XXXX
            var sb = new StringBuilder(Prefix, 16);
            sb.Append(encoded, 0, 4);
            sb.Append('-');
            sb.Append(encoded, 4, 4);
            sb.Append('-');
            sb.Append(encoded, 8, 4);
            return sb.ToString();
        }

        /// <summary>
        /// Encodes an IP address string and port into a room code string.
        /// </summary>
        /// <param name="ipAddress">The IPv4 address as a string.</param>
        /// <param name="port">The port number (0-65535).</param>
        /// <returns>A room code string in the format "SK-XXXX-XXXX-XXXX".</returns>
        public static string Encode(string ipAddress, int port)
        {
            if (!IPAddress.TryParse(ipAddress, out var ip))
                throw new ArgumentException($"Invalid IP address: {ipAddress}", nameof(ipAddress));
            return Encode(ip, port);
        }

        /// <summary>
        /// Encodes an IP address and port with a fresh cryptographically random 80-bit secret.
        /// The result is <c>SK-XXXX-XXXX-XXXX-YYYY-YYYY-YYYY-YYYY</c>: the first part addresses the host,
        /// the second is the pre-shared key for the HMAC handshake. Because the secret is not
        /// derivable from the address, knowing IP:port is no longer enough to join.
        /// </summary>
        /// <param name="ip">The IPv4 address to encode.</param>
        /// <param name="port">The port number (0-65535).</param>
        /// <returns>A room code with a random secret component.</returns>
        public static string EncodeWithSecret(IPAddress ip, int port)
        {
            var secret = new byte[SecretBytes];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(secret);
            return Encode(ip, port, secret);
        }

        /// <summary>
        /// Encodes an IP address and port with an explicit secret.
        /// </summary>
        /// <param name="ip">The IPv4 address to encode.</param>
        /// <param name="port">The port number (0-65535).</param>
        /// <param name="secret">Exactly <see cref="SecretBytes"/> random bytes.</param>
        /// <returns>A room code with a secret component.</returns>
        public static string Encode(IPAddress ip, int port, ReadOnlySpan<byte> secret)
        {
            if (secret.Length == 0)
                return Encode(ip, port);
            if (secret.Length != SecretBytes)
                throw new ArgumentException($"Secret must be {SecretBytes} bytes", nameof(secret));

            return AppendSecret(Encode(ip, port), secret);
        }

        /// <summary>
        /// Encodes an IP address and port with a fresh cryptographically random secret.
        /// </summary>
        /// <param name="ipAddress">The IPv4 address as a string.</param>
        /// <param name="port">The port number (0-65535).</param>
        /// <returns>A room code with a random secret component.</returns>
        public static string EncodeWithSecret(string ipAddress, int port)
        {
            if (!IPAddress.TryParse(ipAddress, out var ip))
                throw new ArgumentException($"Invalid IP address: {ipAddress}", nameof(ipAddress));
            return EncodeWithSecret(ip, port);
        }

        private static string AppendSecret(string addressCode, ReadOnlySpan<byte> secret)
        {
            // Encode SecretBytes*8 bits into SecretCodeChars Crockford characters
            // (10 bytes = 80 bits = 16 * 5). The MSB of the first byte is the first bit.
            var chars = new char[SecretCodeChars];
            for (int charIndex = SecretCodeChars - 1; charIndex >= 0; charIndex--)
            {
                int value = 0;
                for (int bit = 0; bit < 5; bit++)
                {
                    int bitIndex = charIndex * 5 + bit;
                    int byteIndex = bitIndex >> 3;
                    int bitInByte = 7 - (bitIndex & 7);
                    value = (value << 1) | ((secret[byteIndex] >> bitInByte) & 1);
                }
                chars[charIndex] = CrockfordAlphabet[value];
            }

            return addressCode + "-" + FormatGroups(new string(chars), 4);
        }

        private static string FormatGroups(string value, int groupSize)
        {
            var sb = new StringBuilder(value.Length + value.Length / groupSize);
            for (int i = 0; i < value.Length; i += groupSize)
            {
                if (i > 0) sb.Append('-');
                sb.Append(value, i, Math.Min(groupSize, value.Length - i));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Converts a room code into its canonical uppercase, hyphenated form without changing
        /// which address/secret it carries. Both peers must key the HMAC handshake on the exact
        /// same string, so a pasted code must be normalized before use.
        /// </summary>
        /// <param name="code">The room code to normalize.</param>
        /// <param name="canonical">The canonical room code when successful.</param>
        /// <returns>True when the input is a valid 12-, 20- or 28-character code.</returns>
        public static bool TryCanonicalize(string? code, out string canonical)
        {
            canonical = string.Empty;
            if (!TryDecode(code, out IPAddress ip, out int port))
                return false;

            string clean = NormalizeCode(code!);
            string addressPart = Encode(ip, port);
            if (clean.Length == SecretCodeBodyLength || clean.Length == LegacySecretBodyLength)
            {
                canonical = addressPart + "-" + FormatGroups(clean.Substring(AddressCodeChars), 4);
            }
            else
            {
                canonical = addressPart;
            }
            return true;
        }

        /// <summary>
        /// Attempts to decode a room code string into an IP address and port.
        /// </summary>
        /// <param name="code">The room code to decode.</param>
        /// <param name="ip">When this method returns, contains the decoded IP address if successful.</param>
        /// <param name="port">When this method returns, contains the decoded port if successful.</param>
        /// <returns>True if the code was successfully decoded; otherwise, false.</returns>
        public static bool TryDecode(string? code, out IPAddress ip, out int port)
        {
            ip = IPAddress.None;
            port = 0;

            if (string.IsNullOrWhiteSpace(code))
                return false;

            // Normalize input: strip prefix, hyphens, spaces, and apply Crockford mapping
            string clean = NormalizeCode(code);
            if (clean.Length == AddressCodeChars)
            {
                return TryDecodeAddressPayload(clean, out ip, out port);
            }
            else if (clean.Length == SecretCodeBodyLength || clean.Length == LegacySecretBodyLength)
            {
                // Address + random secret (new 80-bit or legacy 40-bit format). The secret
                // is validated as Crockford Base32 but its value is irrelevant here; it only
                // matters as the HMAC key of the full canonical string.
                for (int i = AddressCodeChars; i < clean.Length; i++)
                {
                    if (CrockfordAlphabet.IndexOf(clean[i]) < 0)
                        return false;
                }

                return TryDecodeAddressPayload(clean.Substring(0, AddressCodeChars), out ip, out port);
            }
            else if (clean.Length == 8)
            {
                // 40-bit compact payload (5 bytes: 4 bytes IP, 1 byte port delta / CRC)
                if (!Base32Decode40(clean, out byte[] raw))
                    return false;

                // Improved encoding: 6 bits for port offset (0-63), 2 bits for CRC
                byte expectedCrc = (byte)((raw[0] ^ raw[1] ^ raw[2] ^ raw[3]) & 0x03);
                byte actualCrc = (byte)(raw[4] & 0x03);
                if (actualCrc != expectedCrc)
                    return false;

                byte portOffset = (byte)((raw[4] >> 2) & 0x3F);
                ip = new IPAddress(new byte[] { raw[0], raw[1], raw[2], raw[3] });
                port = 7777 + portOffset;
                return true;
            }

            return false;
        }

        private static bool TryDecodeAddressPayload(string payload, out IPAddress ip, out int port)
        {
            ip = IPAddress.None;
            port = 0;

            // 56-bit payload (7 bytes)
            if (!Base32Decode56(payload, out byte[] raw))
                return false;

            byte expectedCrc = ComputeCrc8(raw.AsSpan(0, 6));
            if (raw[6] != expectedCrc)
                return false; // CRC mismatch

            byte[] ipBytes = new byte[4];
            Array.Copy(raw, 0, ipBytes, 0, 4);
            ip = new IPAddress(ipBytes);
            port = (raw[4] << 8) | raw[5];
            return true;
        }

        /// <summary>
        /// Attempts to decode a room code string into IP address and port strings.
        /// </summary>
        /// <param name="code">The room code to decode.</param>
        /// <param name="ipAddress">When this method returns, contains the decoded IP address string if successful.</param>
        /// <param name="port">When this method returns, contains the decoded port if successful.</param>
        /// <returns>True if the code was successfully decoded; otherwise, false.</returns>
        public static bool TryDecodeToString(string? code, out string ipAddress, out int port)
        {
            if (TryDecode(code, out IPAddress ip, out port))
            {
                ipAddress = ip.ToString();
                return true;
            }
            ipAddress = string.Empty;
            return false;
        }

        /// <summary>
        /// Checks if a room code uses the deprecated 8-character compact format.
        /// 8-character codes have only 2-bit CRC and are vulnerable to tampering.
        /// Prefer 12-character codes with full CRC-8 protection.
        /// </summary>
        public static bool IsDeprecatedCode(string? code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return false;

            string clean = NormalizeCode(code);

            // BUG-N6: only strings made solely of Crockford Base32 characters can be room
            // codes. Without this check an IPv4 address of exactly 8 characters (e.g.
            // "10.0.0.1") was classified as a deprecated code, so connecting to it threw
            // "8-character room codes are deprecated" instead of dialling the address.
            if (clean.Length != 8)
                return false;

            for (int i = 0; i < clean.Length; i++)
            {
                if (CrockfordAlphabet.IndexOf(clean[i]) < 0)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Computes an 8-bit CRC checksum for the specified data.
        /// </summary>
        /// <param name="data">The data to compute the checksum for.</param>
        /// <returns>The 8-bit CRC checksum value.</returns>
        public static byte ComputeCrc8(ReadOnlySpan<byte> data)
        {
            byte crc = 0x00;
            for (int i = 0; i < data.Length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x80) != 0)
                        crc = (byte)((crc << 1) ^ 0x07);
                    else
                        crc = (byte)(crc << 1);
                }
            }
            return crc;
        }

        #endregion

        #region Private Methods

        private static string NormalizeCode(string input)
        {
            string trimmed = input.Trim();
            var sb = new StringBuilder(trimmed.Length);
            int start = 0;

            if (trimmed.StartsWith("SK-", StringComparison.OrdinalIgnoreCase))
            {
                start = 3;
            }
            else if (trimmed.StartsWith("SK", StringComparison.OrdinalIgnoreCase))
            {
                // BUG-N12: a bare 12-character code can legitimately begin with "SK"
                // (e.g. codes for 204.192.0.0/10). Only treat a prefix without the hyphen
                // as a prefix when exactly 12 code characters follow it.
                int bodyLength = 0;
                for (int i = 2; i < trimmed.Length; i++)
                {
                    char c = trimmed[i];
                    if (c == '-' || char.IsWhiteSpace(c))
                        continue;
                    bodyLength++;
                }

                if (bodyLength == AddressCodeChars || bodyLength == SecretCodeBodyLength ||
                    bodyLength == LegacySecretBodyLength)
                    start = 2;
            }

            for (int i = start; i < trimmed.Length; i++)
            {
                char c = char.ToUpperInvariant(trimmed[i]);
                if (c == '-' || char.IsWhiteSpace(c))
                    continue;

                // Crockford substitutions
                if (c == 'I' || c == 'L') c = '1';
                else if (c == 'O') c = '0';

                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Base32Encode56(byte[] data)
        {
            // data has 7 bytes (56 bits).
            // Convert to 12 5-bit values.
            ulong val = 0;
            for (int i = 0; i < 7; i++)
            {
                val = (val << 8) | data[i];
            }
            // val has 56 bits. Shift left by 4 to align to 60 bits (12 * 5).
            val <<= 4;

            char[] chars = new char[12];
            for (int i = 11; i >= 0; i--)
            {
                int index = (int)(val & 0x1F);
                chars[i] = CrockfordAlphabet[index];
                val >>= 5;
            }
            return new string(chars);
        }

        private static bool Base32Decode56(string str, out byte[] data)
        {
            data = new byte[7];
            if (str.Length != 12) return false;

            ulong val = 0;
            for (int i = 0; i < 12; i++)
            {
                int charVal = CrockfordAlphabet.IndexOf(str[i]);
                if (charVal < 0) return false;
                val = (val << 5) | (uint)charVal;
            }

            // Verify that the 4 trailing pad bits are 0
            if ((val & 0x0F) != 0)
                return false;

            // Shift right by 4 to discard the 4 zero pad bits
            val >>= 4;

            for (int i = 6; i >= 0; i--)
            {
                data[i] = (byte)(val & 0xFF);
                val >>= 8;
            }
            return true;
        }

        private static bool Base32Decode40(string str, out byte[] data)
        {
            data = new byte[5];
            if (str.Length != 8) return false;

            ulong val = 0;
            for (int i = 0; i < 8; i++)
            {
                int charVal = CrockfordAlphabet.IndexOf(str[i]);
                if (charVal < 0) return false;
                val = (val << 5) | (uint)charVal;
            }

            for (int i = 4; i >= 0; i--)
            {
                data[i] = (byte)(val & 0xFF);
                val >>= 8;
            }
            return true;
        }

        #endregion
    }
}
