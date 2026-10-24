using System.Globalization;
using System.Text.RegularExpressions;
using HD2Mimnt.Application.Localization;

namespace HD2Mimnt.Tests.Services;

public sealed class TranslationCatalogTests
{
    [Fact]
    public void BuiltInTranslations_ShouldCoverSameKeysAndPreserveFormatArguments()
    {
        var catalog = new TranslationCatalog();
        var english = catalog.GetStrings(CultureInfo.GetCultureInfo("en-US"));
        Assert.True(english.Count > 800);
        foreach (var language in catalog.SupportedLanguages)
        {
            var culture = CultureInfo.GetCultureInfo(language.Code);
            var translations = catalog.GetStrings(culture);
            Assert.Equal(english.Keys.Order(), translations.Keys.Order());
            Assert.Equal(translations.Count, translations.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            foreach (var (key, text) in translations)
            {
                Assert.False(string.IsNullOrWhiteSpace(text), key);
                string[] Parameters(string value) => Regex.Matches(value, @"\{(\d+)(?:[^}]*)\}")
                    .Select(match => match.Groups[1].Value).Order().ToArray();
                Assert.Equal(Parameters(english[key]), Parameters(text));
                _ = string.Format(culture, text, Enumerable.Repeat<object>(12, 20).ToArray());
            }
        }
    }
}
