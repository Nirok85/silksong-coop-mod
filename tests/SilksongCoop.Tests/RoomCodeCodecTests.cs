using System;
using System.Net;
using SilksongCoop.Core.Networking;
using Xunit;

namespace SilksongCoop.Tests
{
    public class RoomCodeCodecTests
    {
        [Theory]
        [InlineData("127.0.0.1", 7777)]
        [InlineData("192.168.1.100", 7777)]
        [InlineData("10.0.0.1", 27015)]
        [InlineData("172.16.254.1", 8080)]
        [InlineData("8.8.8.8", 53)]
        [InlineData("255.255.255.255", 65535)]
        public void EncodeAndDecode_Roundtrip_StandardIpsAndPorts(string ipString, int port)
        {
            var originalIp = IPAddress.Parse(ipString);
            string code = RoomCodeCodec.Encode(originalIp, port);

            Assert.NotNull(code);
            Assert.StartsWith("SK-", code);

            bool success = RoomCodeCodec.TryDecode(code, out IPAddress decodedIp, out int decodedPort);

            Assert.True(success, $"Failed to decode room code: {code}");
            Assert.Equal(originalIp, decodedIp);
            Assert.Equal(port, decodedPort);
        }

        [Fact]
        public void Decode_WithCrockfordSubstitutions_Succeeds()
        {
            var ip = IPAddress.Parse("192.168.1.50");
            int port = 7777;
            string code = RoomCodeCodec.Encode(ip, port);

            // 1. Lowercase
            string lower = code.ToLowerInvariant();
            Assert.True(RoomCodeCodec.TryDecode(lower, out IPAddress ipLower, out int portLower));
            Assert.Equal(ip, ipLower);
            Assert.Equal(port, portLower);

            // 2. Without prefix
            string withoutPrefix = code.Substring(3);
            Assert.True(RoomCodeCodec.TryDecode(withoutPrefix, out IPAddress ipNoPre, out int portNoPre));
            Assert.Equal(ip, ipNoPre);
            Assert.Equal(port, portNoPre);

            // 3. Without hyphens
            string noHyphens = code.Replace("-", "");
            Assert.True(RoomCodeCodec.TryDecode(noHyphens, out IPAddress ipNoHyphen, out int portNoHyphen));
            Assert.Equal(ip, ipNoHyphen);
            Assert.Equal(port, portNoHyphen);

            // 4. With extra spaces
            string withSpaces = "  " + code.Replace("-", " - ") + "  ";
            Assert.True(RoomCodeCodec.TryDecode(withSpaces, out IPAddress ipSpaces, out int portSpaces));
            Assert.Equal(ip, ipSpaces);
            Assert.Equal(port, portSpaces);
        }

        [Fact]
        public void Decode_WithTamperedCharacters_FailsCrcValidation()
        {
            var ip = IPAddress.Parse("192.168.1.50");
            int port = 7777;
            string validCode = RoomCodeCodec.Encode(ip, port);

            // Mutate characters at various positions in the payload
            int[] indicesToTamper = new int[] { 3, 5, 8, 10, validCode.Length - 1 };

            foreach (int index in indicesToTamper)
            {
                char[] chars = validCode.ToCharArray();
                char originalChar = chars[index];
                chars[index] = originalChar == 'A' ? 'Z' : 'A';
                string tamperedCode = new string(chars);

                bool success = RoomCodeCodec.TryDecode(tamperedCode, out IPAddress _, out int _);
                Assert.False(success, $"Tampered room code at index {index} ('{originalChar}' -> '{chars[index]}') should fail CRC validation.");
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("SK-INVALID-CHARS!")]
        [InlineData("SK-123")]
        [InlineData("SK-TOO-LONG-CODE-1234567890")]
        public void Decode_InvalidOrMalformedStrings_ReturnsFalse(string? invalidInput)
        {
            bool success = RoomCodeCodec.TryDecode(invalidInput, out IPAddress ip, out int port);
            Assert.False(success);
            Assert.Equal(IPAddress.None, ip);
            Assert.Equal(0, port);
        }

        [Fact]
        public void Encode_InvalidInputs_ThrowsArgumentExceptions()
        {
            Assert.Throws<ArgumentNullException>(() => RoomCodeCodec.Encode((IPAddress)null!, 7777));
            Assert.Throws<ArgumentOutOfRangeException>(() => RoomCodeCodec.Encode(IPAddress.Loopback, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => RoomCodeCodec.Encode(IPAddress.Loopback, 70000));
            Assert.Throws<ArgumentException>(() => RoomCodeCodec.Encode(IPAddress.IPv6Loopback, 7777));
        }
    }
}
