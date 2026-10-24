using System.Globalization;

namespace HD2Mimnt.Core.Localization;

public static class LanguageCodes
{
    public const string Automatic = "auto";
    public const string SimplifiedChinese = "zh-CN";
    public const string TraditionalChinese = "zh-TW";
    public const string English = "en-US";
}

public sealed record LanguageInfo(string Code, string NativeName);

public sealed class LanguageChangedEventArgs(
    string previousLanguage, string selectedLanguage,
    CultureInfo previousCulture, CultureInfo currentCulture) : EventArgs
{
    public string PreviousLanguage { get; } = previousLanguage;
    public string SelectedLanguage { get; } = selectedLanguage;
    public CultureInfo PreviousCulture { get; } = previousCulture;
    public CultureInfo CurrentCulture { get; } = currentCulture;
}
