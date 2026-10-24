using System.Globalization;
using System.Resources;
using HD2Mimnt.Core.Localization;

namespace HD2Mimnt.Application.Localization;

/// <summary>内置资源目录。英语为完整回退资源；新增语言时添加 resx 和语言项。</summary>
public sealed class TranslationCatalog
{
    private readonly ResourceManager _resources = new(
        "HD2Mimnt.Application.Localization.Resources.Strings", typeof(TranslationCatalog).Assembly);

    public IReadOnlyList<LanguageInfo> SupportedLanguages { get; } = Array.AsReadOnly(new[]
    {
        new LanguageInfo(LanguageCodes.SimplifiedChinese, "简体中文"),
        new LanguageInfo(LanguageCodes.TraditionalChinese, "繁體中文"),
        new LanguageInfo(LanguageCodes.English, "English")
    });

    public string GetString(string key, CultureInfo culture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(culture);
        return _resources.GetString(key, culture) ?? key;
    }

    /// <summary>供展示层建立旧消息模板到资源键的映射，不改变业务数据。</summary>
    public IReadOnlyDictionary<string, string> GetStrings(CultureInfo culture)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        var resources = _resources.GetResourceSet(culture, true, true);
        if (resources != null)
            foreach (System.Collections.DictionaryEntry entry in resources)
                if (entry.Key is string key && entry.Value is string value) entries[key] = value;
        return entries;
    }
}
