using System.ComponentModel;
using System.Globalization;
using HD2Mimnt.Core.Localization;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>应用级语言状态。通知可能来自后台线程，UI 接入时须派发至 UI 线程。</summary>
public interface ILocalizationService : INotifyPropertyChanged
{
    IReadOnlyList<LanguageInfo> SupportedLanguages { get; }
    string SelectedLanguage { get; }
    bool IsAutomatic { get; }
    string CurrentLanguage { get; }
    CultureInfo CurrentCulture { get; }
    string this[string key] { get; }
    event EventHandler<LanguageChangedEventArgs>? LanguageChanged;

    string GetString(string key);
    string Format(string key, params object?[] arguments);
    Task SetLanguageAsync(string languageCode);
    void RefreshSystemLanguage();
}
