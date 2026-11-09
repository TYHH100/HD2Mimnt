using System.IO;
using HD2Mimnt.Core.AudioPreview;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Analysis;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Infrastructure.AudioPreview;

/// <summary>
/// 按游戏目录与 patch 包基名加载并缓存 <see cref="GameAudioBaseline"/>。基线持有常开只读
/// 句柄（legacy 布局）或归档会话（slim 布局），逐出时必须释放；缓存容量与旧管理器一致（16）。
/// </summary>
public sealed class GameAudioBaselineProvider(IGameUnitReferenceReader gameReader, ILogger<GameAudioBaselineProvider>? logger = null)
{
    private const int MaxCachedBaselines = 16;

    private readonly object _gate = new();
    private readonly Dictionary<string, IGameAudioBaseline?> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = [];

    /// <summary>构造传给 <see cref="IAudioPreviewReader.InspectAsync"/> 的基线解析器。
    /// 游戏目录未配置或 data 目录不存在时返回 null（条目 MatchesOriginal 保持 null）。</summary>
    public Func<string, IGameAudioBaseline?>? CreateResolver(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(Path.Combine(gameDirectory, "data")))
            return null;
        var root = gameDirectory;
        return packageBaseName => Resolve(root, packageBaseName);
    }

    private IGameAudioBaseline? Resolve(string gameRoot, string packageBaseName)
    {
        var key = Path.Combine(gameRoot, "data") + "|" + packageBaseName;
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;
        }

        var baseline = GameAudioBaseline.TryLoad(gameRoot, packageBaseName,
            (directory, name, token) =>
                (gameReader as GameUnitReferenceReader)?.TryOpenAudioPackage(directory, name, token),
            logger);

        lock (_gate)
        {
            if (_cache.ContainsKey(key))
            {
                // 并发下已被其他线程缓存；丢弃本次多加载的实例。
                baseline?.Dispose();
                return _cache[key];
            }
            while (_order.Count >= MaxCachedBaselines)
            {
                // 基线持有常开只读句柄，逐出时必须释放。
                var oldest = _order.Dequeue();
                if (_cache.Remove(oldest, out var evicted))
                    evicted?.Dispose();
            }
            _cache[key] = baseline;
            _order.Enqueue(key);
        }
        return baseline;
    }

    /// <summary>释放全部缓存的基线（应用退出或设置中的游戏目录变化时调用）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (var baseline in _cache.Values)
                baseline?.Dispose();
            _cache.Clear();
            _order.Clear();
        }
    }
}
