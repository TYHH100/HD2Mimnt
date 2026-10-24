using HD2Mimnt.Application.Services;

namespace HD2Mimnt.Application.Search;

/// <summary>搜索框匹配行为，来自“设置 → 搜索”。</summary>
public readonly record struct SearchMatchOptions(bool Fuzzy, bool Pinyin)
{
    public static SearchMatchOptions Default => default;

    public static SearchMatchOptions FromSettings(SettingsService? settings)
        => settings == null
            ? Default
            : new SearchMatchOptions(
                settings.GetSetting(SettingKeys.SearchFuzzy, false),
                settings.GetSetting(SettingKeys.SearchPinyin, false));
}

/// <summary>匹配强度，数值越大越接近用户输入；0 表示不匹配。</summary>
public enum SearchMatchKind
{
    None = 0,
    Fuzzy = 1,
    Pinyin = 2,
    Normalized = 3,
    Contains = 4,
    Prefix = 5,
    Exact = 6
}

/// <summary>
/// 所有本地搜索框共用的文本匹配：
/// 默认保持连续包含匹配；开启模糊搜索后额外允许不连续字符，开启拼音搜索后支持全拼与首字母。
/// </summary>
public static class SearchMatcher
{
    private const int MinimumPinyinQueryLength = 2;
    private const int MinimumFuzzyQueryLength = 3;

    public static SearchMatchKind Match(string? text, string query, SearchMatchOptions options)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(query)) return SearchMatchKind.None;

        var trimmed = query.Trim();
        if (text.Equals(trimmed, StringComparison.OrdinalIgnoreCase)) return SearchMatchKind.Exact;
        if (text.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)) return SearchMatchKind.Prefix;
        if (text.Contains(trimmed, StringComparison.OrdinalIgnoreCase)) return SearchMatchKind.Contains;

        var normalizedText = Normalize(text);
        var normalizedQuery = Normalize(trimmed);
        if (normalizedQuery.Length == 0) return SearchMatchKind.None;
        if (normalizedText.Contains(normalizedQuery, StringComparison.Ordinal))
            return SearchMatchKind.Normalized;

        var pinyinQuery = IsAsciiQuery(normalizedQuery) ? NormalizePinyin(normalizedQuery) : string.Empty;
        if (options.Pinyin && pinyinQuery.Length >= MinimumPinyinQueryLength
            && PinyinConverter.TryGetForms(text, out var forms))
        {
            if (MatchesPinyin(forms, pinyinQuery)) return SearchMatchKind.Pinyin;
            if (options.Fuzzy && IsAnySubsequence(forms, pinyinQuery)) return SearchMatchKind.Pinyin;
        }

        if (options.Fuzzy && IsSubsequence(normalizedText, normalizedQuery))
            return SearchMatchKind.Fuzzy;

        return SearchMatchKind.None;
    }

    /// <summary>多个字段取最高匹配强度，任一字段命中即可。</summary>
    public static SearchMatchKind MatchAny(string query, SearchMatchOptions options, params string?[] values)
    {
        var best = SearchMatchKind.None;
        foreach (var value in values)
        {
            var kind = Match(value, query, options);
            if (kind > best) best = kind;
            if (best == SearchMatchKind.Exact) break;
        }
        return best;
    }

    private static bool MatchesPinyin(PinyinConverter.Forms forms, string query)
    {
        foreach (var full in forms.Full)
        {
            if (full.Contains(query, StringComparison.Ordinal)) return true;
        }
        foreach (var initials in forms.Initials)
        {
            if (initials.Contains(query, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool IsAnySubsequence(PinyinConverter.Forms forms, string query)
    {
        foreach (var full in forms.Full)
        {
            if (IsSubsequence(full, query)) return true;
        }
        foreach (var initials in forms.Initials)
        {
            if (IsSubsequence(initials, query)) return true;
        }
        return false;
    }

    private static bool IsSubsequence(string text, string query)
    {
        if (query.Length < MinimumFuzzyQueryLength || text.Length < query.Length) return false;
        var queryIndex = 0;
        foreach (var ch in text)
        {
            if (ch != query[queryIndex]) continue;
            queryIndex++;
            if (queryIndex == query.Length) return true;
        }
        return false;
    }

    private static bool IsAsciiQuery(string value)
    {
        foreach (var ch in value)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') continue;
            return false;
        }
        return value.Length > 0;
    }

    private static string NormalizePinyin(string value) => value.Replace('v', 'u');

    private static string Normalize(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(char.ToLowerInvariant(ch));
        }
        return builder.ToString();
    }
}
