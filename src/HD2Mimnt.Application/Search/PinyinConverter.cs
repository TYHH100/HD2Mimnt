using System.Collections.Concurrent;

namespace HD2Mimnt.Application.Search;

/// <summary>
/// 中文文本的拼音表示，供拼音搜索匹配使用。
/// 逐字取常用读音并生成候选全拼与首字母组合，避免引入第三方运行时依赖。
/// </summary>
internal static class PinyinConverter
{
    private const int MaxCandidates = 64;
    private const int MaxTextLength = 512;
    private const int MaxCacheEntries = 8192;

    private static readonly Dictionary<char, string[]> CharacterReadings = BuildReadings();
    private static readonly ConcurrentDictionary<string, Forms> Cache = new(StringComparer.Ordinal);

    /// <summary>文本不含汉字或过长时返回 false，调用方继续使用普通文本匹配。</summary>
    public static bool TryGetForms(string text, out Forms forms)
    {
        forms = Forms.Empty;
        if (text.Length == 0 || text.Length > MaxTextLength || !ContainsChinese(text)) return false;
        if (Cache.TryGetValue(text, out forms)) return forms.HasValue;

        forms = Build(text);
        if (Cache.Count < MaxCacheEntries) Cache.TryAdd(text, forms);
        return forms.HasValue;
    }

    private static bool ContainsChinese(string text)
    {
        foreach (var ch in text)
        {
            if (CharacterReadings.ContainsKey(ch)) return true;
        }
        return false;
    }

    private static Forms Build(string text)
    {
        var tokens = new List<Token>();
        for (var index = 0; index < text.Length;)
        {
            var ch = text[index];
            if (CharacterReadings.TryGetValue(ch, out var readings))
            {
                tokens.Add(new Token(readings, IsAsciiRun: false));
                index++;
                continue;
            }
            if (char.IsLetterOrDigit(ch))
            {
                var start = index;
                while (index < text.Length && char.IsLetterOrDigit(text[index])
                       && !CharacterReadings.ContainsKey(text[index]))
                {
                    index++;
                }
                tokens.Add(new Token(new[] { text[start..index].ToLowerInvariant() }, IsAsciiRun: true));
                continue;
            }
            index++;
        }
        if (tokens.Count == 0) return Forms.Empty;

        var candidates = new List<Candidate> { new(string.Empty, string.Empty) };
        foreach (var token in tokens)
        {
            var next = new List<Candidate>(candidates.Count * token.Alternatives.Length);
            foreach (var candidate in candidates)
            {
                foreach (var alternative in token.Alternatives)
                {
                    var full = token.IsAsciiRun ? alternative : NormalizeReading(alternative);
                    var initials = token.IsAsciiRun ? alternative : alternative[..1];
                    next.Add(new Candidate(candidate.Full + full, candidate.Initials + initials));
                }
            }
            candidates = Deduplicate(next);
        }

        var fullForms = candidates.Select(candidate => candidate.Full).Distinct(StringComparer.Ordinal).ToArray();
        var initialForms = candidates.Select(candidate => candidate.Initials).Distinct(StringComparer.Ordinal).ToArray();
        return fullForms.Length == 0 ? Forms.Empty : new Forms(fullForms, initialForms);
    }

    private static List<Candidate> Deduplicate(List<Candidate> candidates)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<Candidate>(Math.Min(candidates.Count, MaxCandidates));
        foreach (var candidate in candidates)
        {
            if (!seen.Add(candidate.Full)) continue;
            result.Add(candidate);
            if (result.Count == MaxCandidates) break;
        }
        return result;
    }

    /// <summary>ü 在输入法里通常写作 v 或 u，统一后查询 “lv/lu” 都能命中“吕”。</summary>
    private static string NormalizeReading(string reading) => reading.Replace('v', 'u');

    private static Dictionary<char, string[]> BuildReadings()
    {
        var readings = new Dictionary<char, string[]>();
        foreach (var rawLine in PinyinData.Table.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var separator = line.IndexOf(':');
            if (separator <= 0 || separator == line.Length - 1) continue;
            var values = line[..separator].Split(',');
            foreach (var ch in line[(separator + 1)..])
            {
                readings[ch] = values;
            }
        }
        return readings;
    }

    private readonly record struct Token(string[] Alternatives, bool IsAsciiRun);

    private readonly record struct Candidate(string Full, string Initials);

    internal readonly record struct Forms(IReadOnlyList<string> Full, IReadOnlyList<string> Initials)
    {
        public static Forms Empty => new(Array.Empty<string>(), Array.Empty<string>());

        public bool HasValue => Full.Count > 0;
    }
}
