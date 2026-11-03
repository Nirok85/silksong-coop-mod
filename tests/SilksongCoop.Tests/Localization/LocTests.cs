using SilksongCoop.Core.Localization;
using Xunit;

namespace SilksongCoop.Tests.Localization
{
    public class LocTests
    {
        [Fact]
        public void Translate_English_ReturnsSource()
        {
            Assert.Equal("HOST", Loc.Translate(CoopLanguage.English, "HOST"));
        }

        [Fact]
        public void Translate_Russian_ReturnsTranslation()
        {
            Assert.Equal("ХОСТ", Loc.Translate(CoopLanguage.Russian, "HOST"));
        }

        [Fact]
        public void Translate_UnknownKey_FallsBackToEnglish()
        {
            const string key = "This key does not exist";

            Assert.Equal(key, Loc.Translate(CoopLanguage.Russian, key));
        }

        [Fact]
        public void Translate_FormatsPlaceholdersInBothLanguages()
        {
            Assert.Equal(
                "<b>Role:</b>  Host",
                Loc.Translate(CoopLanguage.English, "<b>Role:</b>  {0}", "Host"));
            Assert.Equal(
                "<b>Роль:</b>  Хост",
                Loc.Translate(CoopLanguage.Russian, "<b>Role:</b>  {0}", "Хост"));
        }

        [Fact]
        public void Translate_UsesInvariantCultureForNumbers()
        {
            // The UI passes formatted values (ms, Hz); a comma decimal separator from
            // the OS locale would make the diagnostics line harder to read.
            Assert.Equal(
                "Interp 50.0 ms",
                Loc.Translate(CoopLanguage.English, "Interp {0:F1} ms", 50.0));
        }

        [Fact]
        public void Translate_ExtraArguments_AreIgnored()
        {
            // A translation without placeholders must tolerate a caller that passes
            // formatting arguments; string.Format ignores extras instead of throwing.
            Assert.Equal(
                "ХОСТ",
                Loc.Translate(CoopLanguage.Russian, "HOST", 1));
        }

        [Fact]
        public void Translate_NullOrEmpty_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, Loc.Translate(CoopLanguage.English, null));
            Assert.Equal(string.Empty, Loc.Translate(CoopLanguage.Russian, string.Empty));
        }

        [Fact]
        public void Resolve_Auto_UsesSystemLanguage()
        {
            Assert.Equal(CoopLanguage.Russian, Loc.Resolve(CoopLanguage.Auto, systemIsRussian: true));
            Assert.Equal(CoopLanguage.English, Loc.Resolve(CoopLanguage.Auto, systemIsRussian: false));
        }

        [Fact]
        public void Resolve_ExplicitChoice_OverridesSystemLanguage()
        {
            Assert.Equal(CoopLanguage.English, Loc.Resolve(CoopLanguage.English, systemIsRussian: true));
            Assert.Equal(CoopLanguage.Russian, Loc.Resolve(CoopLanguage.Russian, systemIsRussian: false));
        }

        [Fact]
        public void SetLanguage_ChangesCurrentTranslation()
        {
            Loc.SetLanguage(CoopLanguage.Russian, systemIsRussian: false);
            Assert.Equal("СТАТУС", Loc.T("STATUS"));

            Loc.SetLanguage(CoopLanguage.English, systemIsRussian: true);
            Assert.Equal("STATUS", Loc.T("STATUS"));
        }
    }
}
