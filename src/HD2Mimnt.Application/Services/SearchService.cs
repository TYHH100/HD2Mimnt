using HD2Mimnt.Application.Search;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 高性能搜索服务
/// 支持精准搜索、前缀搜索、模糊搜索和标签搜索
/// 目标：10,000 Mods 时达到毫秒级响应
/// </summary>
public class SearchService
{
    private readonly IModRepository _modRepository;
    private readonly ILogger<SearchService>? _logger;

    // 内存索引
    private Dictionary<string, Mod> _exactIndex = new();
    private Dictionary<string, List<Mod>> _prefixIndex = new();
    private Dictionary<string, HashSet<Mod>> _tagIndex = new();
    private Dictionary<string, List<Mod>> _normalizedIndex = new();
    private List<Mod> _allMods = new();
    private bool _indexBuilt = false;
    private readonly object _indexLock = new();

    public SearchService(IModRepository modRepository, ILogger<SearchService>? logger = null)
    {
        _modRepository = modRepository;
        _logger = logger;
    }

    /// <summary>
    /// 构建搜索索引
    /// </summary>
    public async Task BuildIndexAsync(CancellationToken cancellationToken = default)
    {
        // 取数必须在锁外并真正 await：若在 UI 线程上以 .Result 阻塞等待，
        // 异步续体被投递到 Avalonia 调度器后会形成启动死锁
        var mods = (await _modRepository.GetAllAsync(cancellationToken)).ToList();

        lock (_indexLock)
        {
            _logger?.LogInformation("Building search index...");
            var startTime = DateTime.UtcNow;

            // 清空现有索引
            _exactIndex.Clear();
            _prefixIndex.Clear();
            _tagIndex.Clear();
            _normalizedIndex.Clear();
            _allMods = mods;

            foreach (var mod in _allMods)
            {
                // Exact Index - 完整名称
                var exactKey = mod.Name.ToLowerInvariant();
                _exactIndex[exactKey] = mod;

                // Normalized Index - 去除空格和特殊字符的名称
                var normalized = NormalizeName(mod.Name);
                if (!_normalizedIndex.ContainsKey(normalized))
                {
                    _normalizedIndex[normalized] = new List<Mod>();
                }
                _normalizedIndex[normalized].Add(mod);

                // Prefix Index - 前缀索引
                BuildPrefixIndex(mod);

                // Tag Index - 标签索引
                if (mod.Tags != null)
                {
                    foreach (var tag in mod.Tags)
                    {
                        var tagKey = tag.ToLowerInvariant();
                        if (!_tagIndex.ContainsKey(tagKey))
                        {
                            _tagIndex[tagKey] = new HashSet<Mod>();
                        }
                        _tagIndex[tagKey].Add(mod);
                    }
                }
            }

            _indexBuilt = true;
            var duration = DateTime.UtcNow - startTime;
            _logger?.LogInformation($"Search index built in {duration.TotalMilliseconds}ms. Total mods: {_allMods.Count}");
        }
    }

    /// <summary>
    /// 更新单个 Mod 的索引
    /// </summary>
    public void UpdateModIndex(Mod mod)
    {
        lock (_indexLock)
        {
            // 移除旧索引
            RemoveModFromIndex(mod.Id);

            // 添加新索引
            _allMods.Add(mod);

            var exactKey = mod.Name.ToLowerInvariant();
            _exactIndex[exactKey] = mod;

            var normalized = NormalizeName(mod.Name);
            if (!_normalizedIndex.ContainsKey(normalized))
            {
                _normalizedIndex[normalized] = new List<Mod>();
            }
            _normalizedIndex[normalized].Add(mod);

            BuildPrefixIndex(mod);

            if (mod.Tags != null)
            {
                foreach (var tag in mod.Tags)
                {
                    var tagKey = tag.ToLowerInvariant();
                    if (!_tagIndex.ContainsKey(tagKey))
                    {
                        _tagIndex[tagKey] = new HashSet<Mod>();
                    }
                    _tagIndex[tagKey].Add(mod);
                }
            }
        }
    }

    /// <summary>
    /// 从索引中移除 Mod
    /// </summary>
    public void RemoveModFromIndex(Guid modId)
    {
        lock (_indexLock)
        {
            var mod = _allMods.FirstOrDefault(m => m.Id == modId);
            if (mod == null) return;

            _allMods.Remove(mod);

            var exactKey = mod.Name.ToLowerInvariant();
            _exactIndex.Remove(exactKey);

            var normalized = NormalizeName(mod.Name);
            if (_normalizedIndex.ContainsKey(normalized))
            {
                _normalizedIndex[normalized].Remove(mod);
                if (_normalizedIndex[normalized].Count == 0)
                {
                    _normalizedIndex.Remove(normalized);
                }
            }

            // 移除前缀索引
            RemovePrefixIndex(mod);

            // 移除标签索引
            if (mod.Tags != null)
            {
                foreach (var tag in mod.Tags)
                {
                    var tagKey = tag.ToLowerInvariant();
                    if (_tagIndex.ContainsKey(tagKey))
                    {
                        _tagIndex[tagKey].Remove(mod);
                        if (_tagIndex[tagKey].Count == 0)
                        {
                            _tagIndex.Remove(tagKey);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// 搜索 Mods（使用默认匹配行为，保持既有调用方兼容）。
    /// </summary>
    public Task<IEnumerable<Mod>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
        => SearchAsync(query, SearchMatchOptions.Default, cancellationToken);

    /// <summary>
    /// 按“设置 → 搜索”的匹配选项搜索 Mods。
    /// </summary>
    public async Task<IEnumerable<Mod>> SearchAsync(
        string query,
        SearchMatchOptions options,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return _allMods;
        }

        // 确保索引已构建
        if (!_indexBuilt)
        {
            await BuildIndexAsync(cancellationToken);
        }

        var trimmed = query.Trim();

        // 检查是否是标签搜索
        if (trimmed.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
        {
            var tag = trimmed[4..].Trim().ToLowerInvariant();
            return SearchByTag(tag);
        }

        Mod[] snapshot;
        lock (_indexLock)
        {
            snapshot = _allMods.ToArray();
        }

        var results = new List<(Mod mod, int priority)>();
        for (var index = 0; index < snapshot.Length; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var priority = Score(snapshot[index], trimmed, options);
            if (priority >= 0) results.Add((snapshot[index], priority));
        }

        // 名称精确/前缀优先，其次作者、描述、标签，最后拼音与模糊匹配。
        return results.OrderBy(r => r.priority).ThenBy(r => r.mod.Name).Select(r => r.mod).ToArray();
    }

    private static int Score(Mod mod, string query, SearchMatchOptions options)
    {
        var nameKind = SearchMatcher.Match(mod.Name, query, options);
        if (nameKind != SearchMatchKind.None)
        {
            return nameKind switch
            {
                SearchMatchKind.Exact => 0,
                SearchMatchKind.Prefix => 1,
                SearchMatchKind.Contains => 2,
                SearchMatchKind.Normalized => 3,
                SearchMatchKind.Pinyin => 7,
                _ => 8
            };
        }

        if (SearchMatcher.Match(mod.Author, query, options) != SearchMatchKind.None) return 4;
        if (SearchMatcher.Match(mod.Description, query, options) != SearchMatchKind.None) return 5;
        if (mod.Tags.Any(tag => SearchMatcher.Match(tag, query, options) != SearchMatchKind.None)) return 6;
        return -1;
    }

    /// <summary>
    /// 标签搜索
    /// </summary>
    private IEnumerable<Mod> SearchByTag(string tag)
    {
        if (_tagIndex.TryGetValue(tag, out var mods))
        {
            return mods;
        }
        return Enumerable.Empty<Mod>();
    }

    /// <summary>
    /// 构建前缀索引
    /// </summary>
    private void BuildPrefixIndex(Mod mod)
    {
        var name = mod.Name.ToLowerInvariant();

        // 为每个单词建立前缀索引
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            // 建立 1-N 字符的前缀
            for (int i = 1; i <= Math.Min(word.Length, 10); i++)
            {
                var prefix = word.Substring(0, i);
                if (!_prefixIndex.ContainsKey(prefix))
                {
                    _prefixIndex[prefix] = new List<Mod>();
                }
                if (!_prefixIndex[prefix].Contains(mod))
                {
                    _prefixIndex[prefix].Add(mod);
                }
            }
        }
    }

    /// <summary>
    /// 移除前缀索引
    /// </summary>
    private void RemovePrefixIndex(Mod mod)
    {
        var name = mod.Name.ToLowerInvariant();
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            for (int i = 1; i <= Math.Min(word.Length, 10); i++)
            {
                var prefix = word.Substring(0, i);
                if (_prefixIndex.ContainsKey(prefix))
                {
                    _prefixIndex[prefix].Remove(mod);
                    if (_prefixIndex[prefix].Count == 0)
                    {
                        _prefixIndex.Remove(prefix);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 标准化名称（去除空格和特殊字符）
    /// </summary>
    private string NormalizeName(string name)
    {
        return new string(name.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c))
            .ToArray());
    }

    /// <summary>
    /// 获取索引统计信息
    /// </summary>
    public SearchIndexStats GetIndexStats()
    {
        lock (_indexLock)
        {
            return new SearchIndexStats
            {
                TotalMods = _allMods.Count,
                ExactIndexSize = _exactIndex.Count,
                PrefixIndexSize = _prefixIndex.Count,
                TagIndexSize = _tagIndex.Count,
                NormalizedIndexSize = _normalizedIndex.Count,
                IsBuilt = _indexBuilt
            };
        }
    }
}

/// <summary>
/// 搜索索引统计信息
/// </summary>
public class SearchIndexStats
{
    public int TotalMods { get; set; }
    public int ExactIndexSize { get; set; }
    public int PrefixIndexSize { get; set; }
    public int TagIndexSize { get; set; }
    public int NormalizedIndexSize { get; set; }
    public bool IsBuilt { get; set; }
}
