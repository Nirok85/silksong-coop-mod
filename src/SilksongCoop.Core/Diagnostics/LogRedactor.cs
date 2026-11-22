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

        // Steam64 identifiers are 17 digits and identify a player; never publish them.
        private static readonly Regex SteamIdPattern =
            new Regex(@"\b\d{17}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // IPv4 addresses can identify a peer's network; redact them.
        private static readonly Regex IpPattern =
            new Regex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Local profile paths contain the OS user name; redact the user segment.
        private static readonly Regex ProfilePathPattern =
            new Regex(@"[A-Za-z]:\\Users\\[^\\]+", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>
        /// Returns the input with room codes, credential-shaped strings, Steam IDs, IP
        /// addresses and local profile paths replaced.
        /// </summary>
        public static string Redact(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            string result = RoomCodePattern.Replace(value, "SK-<redacted>");
            result = CredentialPattern.Replace(result, "<redacted>");
            result = SteamIdPattern.Replace(result, "<steamid>");
            result = IpPattern.Replace(result, "<ip>");
            return ProfilePathPattern.Replace(result, @"<drive>:\Users\<user>");
        }
    }
}
