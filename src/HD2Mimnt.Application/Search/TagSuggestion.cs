using HD2Mimnt.Application.Services;

namespace HD2Mimnt.Application.Search;

/// <summary>搜索框里 @ 词元的补全建议：前缀或拼音匹配，取前若干条。</summary>
public static class TagSuggestion
{
    private const int DefaultLimit = 8;
    private static readonly SearchMatchOptions Options = new(Fuzzy: false, Pinyin: true);

    /// <summary>查询文本末尾的 @词元；token 不含 @，tokenStart 是 @ 的下标。</summary>
    public static bool TryGetTrailingToken(string? text, out string token, out int tokenStart)
    {
        token = string.Empty;
        tokenStart = 0;
        if (string.IsNullOrEmpty(text)) return false;
        var start = text.Length;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        var word = text[start..];
        if (word.Length == 0 || word[0] != '@') return false;
        token = word.Length > 1 ? word[1..] : string.Empty;
        tokenStart = start;
        return true;
    }

    public static IReadOnlyList<string> Rank(IEnumerable<string> tags, string token, int limit = DefaultLimit)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (token.Length == 0)
        {
            var all = new List<string>();
            foreach (var tag in tags)
                if (!string.IsNullOrWhiteSpace(tag) && seen.Add(tag))
                    all.Add(tag);
            all.Sort(StringComparer.OrdinalIgnoreCase);
            return all.Take(limit).ToArray();
        }

        var scored = new List<(string Tag, int Kind)>();
        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag) || !seen.Add(tag)) continue;
            var kind = MatchKind(tag, token);
            if (kind > 0) scored.Add((tag, kind));
        }
        return scored.OrderByDescending(item => item.Kind)
            .ThenBy(item => item.Tag.Length)
            .ThenBy(item => item.Tag, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(item => item.Tag)
            .ToArray();
    }

    private static int MatchKind(string tag, string token)
    {
        var best = (int)SearchMatcher.Match(tag, token, Options);
        // 多词名称（如 "Lua Mods"）允许命中其中任意一个词。
        foreach (var word in tag.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            best = Math.Max(best, (int)SearchMatcher.Match(word, token, Options));
        return best;
    }

    /// <summary>用选中的完整标签替换末尾 @词元，并补一个空格方便继续输入。</summary>
    public static string Apply(string? text, int tokenStart, string tag)
    {
        var prefix = text is null || tokenStart <= 0 || tokenStart > text.Length ? string.Empty : text[..tokenStart];
        return prefix + "@" + tag + " ";
    }
}
