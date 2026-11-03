using System.Text.RegularExpressions;

namespace SilksongCoop.Core.Diagnostics
{
    /// <summary>
    /// Removes secrets from text that is about to leave the machine (for example, a
    /// diagnostics bundle attached to a bug report). Kept in Core so the rules are
    /// unit-testable without Unity or BepInEx.
    /// </summary>
    public static class LogRedactor
    {
        // Room codes are the join secret on the UDP path. Codes are grouped in blocks of
        // four characters; a shorter match is left alone so ordinary text is not mangled.
        private static readonly Regex RoomCodePattern =
            new Regex(@"SK-[A-Z0-9]{4}(?:-[A-Z0-9]{4}){2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Defence in depth: no token belongs in a game log, but redact common formats so a
        // previously leaked value cannot be re-published through a diagnostics bundle.
        private static readonly Regex CredentialPattern =
            new Regex(
                @"(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|Bearer\s+[A-Za-z0-9\-\._~\+\/]{20,})",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Returns the input with room codes and credential-shaped strings replaced.
        /// </summary>
        public static string Redact(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            string result = RoomCodePattern.Replace(value, "SK-<redacted>");
            return CredentialPattern.Replace(result, "<redacted>");
        }
    }
}
