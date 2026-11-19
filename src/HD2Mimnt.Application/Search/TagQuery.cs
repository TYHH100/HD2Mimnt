using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Application.Search;

/// <summary>
/// 查询中的 @标签 过滤：可出现在任意位置、大小写不敏感；多个标签取交集，
/// 剩余单词作为普通关键字继续匹配名称/作者/描述。
/// </summary>
public static class TagQuery
{
    public static (string Keyword, IReadOnlyList<string> Tags) Parse(string? query)
    {
        var tags = new List<string>();
        var words = new List<string>();
        foreach (var token in (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length > 1 && token[0] == '@') tags.Add(token[1..]);
            else words.Add(token);
        }
        return (string.Join(' ', words), tags);
    }

    /// <summary>模组可被 @ 搜索命中的标签：来源标签、用户标签与全部系统虚拟标签。</summary>
    public static IEnumerable<string> EnumerateModTags(Mod mod)
    {
        foreach (var tag in mod.Tags ?? []) yield return tag;
        foreach (var tag in mod.UserTags ?? []) yield return tag;
        foreach (var tag in ModSystemTags.For(mod)) yield return tag;
    }

    /// <summary>标签集合需同时包含所有过滤词；过滤词按前缀匹配，兼容“视频 BIK2”“Lua Mods”这类多项名称。</summary>
    public static bool MatchesAllTags(IEnumerable<string>? tags, IReadOnlyList<string> filters)
    {
        if (filters.Count == 0) return true;
        var available = new HashSet<string>(tags ?? [], StringComparer.OrdinalIgnoreCase);
        return filters.All(filter => available.Any(tag => tag.StartsWith(filter, StringComparison.OrdinalIgnoreCase)));
    }
}
