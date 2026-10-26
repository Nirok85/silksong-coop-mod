using System;
using System.Net;
using System.Text;

namespace SilksongCoop.Core.Networking
{
    /// <summary>
    /// Crockford Base32 room code encoder and decoder for peer-to-peer connection strings.
    /// Packs IPv4 (4 bytes), Port (2 bytes), and CRC checksum (1 byte) into an easily shareable code.
    /// Tolerates case insensitivity, hyphens, spaces, and ambiguous characters (I, L -> 1; O -> 0).
    /// </summary>
    public static class RoomCodeCodec
    {
        private const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        private const string Prefix = "SK-";

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

        public static string Encode(string ipAddress, int port)
        {
            if (!IPAddress.TryParse(ipAddress, out var ip))
                throw new ArgumentException($"Invalid IP address: {ipAddress}", nameof(ipAddress));
            return Encode(ip, port);
        }

        public static bool TryDecode(string? code, out IPAddress ip, out int port)
        {
            ip = IPAddress.None;
            port = 0;

            if (string.IsNullOrWhiteSpace(code))
                return false;

            // Normalize input: strip prefix, hyphens, spaces, and apply Crockford mapping
            string clean = NormalizeCode(code);
            if (clean.Length == 12)
            {
                // 56-bit payload (7 bytes)
                if (!Base32Decode56(clean, out byte[] raw))
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

        private static string NormalizeCode(string input)
        {
            string trimmed = input.Trim();
            var sb = new StringBuilder(trimmed.Length);
            int start = 0;
            if (trimmed.StartsWith("SK-", StringComparison.OrdinalIgnoreCase))
                start = 3;
            else if (trimmed.StartsWith("SK", StringComparison.OrdinalIgnoreCase))
                start = 2;

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
    }
}
