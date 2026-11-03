using SilksongCoop.Core.Diagnostics;
using Xunit;

namespace SilksongCoop.Tests.Diagnostics
{
    public class LogRedactorTests
    {
        [Fact]
        public void Redact_RoomCode_IsReplaced()
        {
            string line = "Hosting with code SK-A8B3-K9Q2-W1Z4-M4T7-N6P8-Q2R4-S5T6 please join";

            string result = LogRedactor.Redact(line);

            Assert.DoesNotContain("A8B3", result);
            Assert.Contains("SK-<redacted>", result);
            Assert.Contains("please join", result);
        }

        [Fact]
        public void Redact_GitHubToken_IsReplaced()
        {
            string line = "Authorization failed for ghp_abcdefghijklmnopqrstuvwxyz0123456789";

            string result = LogRedactor.Redact(line);

            Assert.DoesNotContain("ghp_", result);
            Assert.Contains("<redacted>", result);
            Assert.Contains("Authorization failed", result);
        }

        [Fact]
        public void Redact_BearerToken_IsReplaced()
        {
            string line = "header: Bearer eyJhbGciOiJIUzI1NiJ9.payload.signature";

            string result = LogRedactor.Redact(line);

            Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", result);
            Assert.Contains("<redacted>", result);
        }

        [Fact]
        public void Redact_OrdinaryLine_IsUnchanged()
        {
            const string line = "[Coop] Peer connected: Player#12345 latency 42 ms";

            Assert.Equal(line, LogRedactor.Redact(line));
        }

        [Fact]
        public void Redact_ShortRoomLikeText_IsNotMangled()
        {
            const string line = "SK-AB12 is not a full room code";

            Assert.Equal(line, LogRedactor.Redact(line));
        }

        [Fact]
        public void Redact_NullOrEmpty_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, LogRedactor.Redact(null));
            Assert.Equal(string.Empty, LogRedactor.Redact(string.Empty));
        }
    }
}
