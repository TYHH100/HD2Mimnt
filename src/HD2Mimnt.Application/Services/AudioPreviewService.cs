using System.IO;
using HD2Mimnt.Core.AudioPreview;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.AudioPreview;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 音频预览门面：解析模组 patch 的音频清单（按 profile 选项筛选文件集，LRU 缓存最近两份）、
/// 提供游戏原版基线解析器（未配置游戏目录时不做比对），并把播放控制转发给
/// <see cref="IAudioPlaybackEngine"/>。UI 只经本服务访问音频预览能力。
/// </summary>
public sealed class AudioPreviewService(
    IModRepository mods,
    IProfileRepository profiles,
    IAudioPreviewReader reader,
    IAudioPlaybackEngine playback,
    GameAudioBaselineProvider baselines,
    SettingsService settings) : IDisposable
{
    private const int MaxCachedInventories = 2;

    private readonly object _cacheGate = new();
    private readonly Dictionary<string, AudioInventoryResult> _inventoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _inventoryOrder = [];

    /// <summary>播放引擎（单实例；同一时间只播放一个条目）。</summary>
    public IAudioPlaybackEngine Playback => playback;

    /// <summary>解析模组与生效选项（与模型预览同款数据源语义）。</summary>
    public async Task<ModelPreviewSource> GetSourceAsync(Guid modId, Guid? profileId = null, CancellationToken cancellationToken = default)
    {
        var mod = await GetModAsync(modId, cancellationToken);
        string? options = null;
        if (profileId is { } id)
        {
            var profile = await profiles.GetByIdAsync(id, cancellationToken)
                ?? throw new KeyNotFoundException($"Profile {id} not found.");
            options = (profile.Mods.FirstOrDefault(item => item.ModId == modId)
                ?? throw new KeyNotFoundException($"Mod {modId} not in profile {id}.")).OptionsJson;
        }
        return new(mod.Id, mod.Name, mod.Manifest, ProfileModOptionResolver.ResolveEffective(mod.Manifest, options));
    }

    /// <summary>巡检模组的音频清单；结果按 patch 集合指纹缓存。游戏目录未配置时条目不做原版比对。</summary>
    public async Task<AudioInventoryResult> LoadAsync(Guid modId, Guid? profileId = null, CancellationToken cancellationToken = default)
    {
        var mod = await GetModAsync(modId, cancellationToken);
        string? options = null;
        if (profileId is { } id)
        {
            var profile = await profiles.GetByIdAsync(id, cancellationToken)
                ?? throw new KeyNotFoundException($"Profile {id} not found.");
            options = (profile.Mods.FirstOrDefault(item => item.ModId == modId)
                ?? throw new KeyNotFoundException($"Mod {modId} not in profile {id}.")).OptionsJson;
        }
        var effective = ProfileModOptionResolver.ResolveEffective(mod.Manifest, options);
        var patches = await Task.Run(() => CollectPatchFiles(mod, effective.ToJson(), cancellationToken), cancellationToken);

        var key = CreatePatchSetKey(patches);
        lock (_cacheGate)
        {
            if (_inventoryCache.TryGetValue(key, out var cached))
            {
                TouchCache(key);
                return cached;
            }
        }

        var resolver = baselines.CreateResolver(settings.GetEffectiveGamePath());
        var result = await reader.InspectAsync(new DirectoryInfo(mod.Path), patches, resolver, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_cacheGate)
        {
            if (!_inventoryCache.TryGetValue(key, out var existing))
            {
                while (_inventoryOrder.Count >= MaxCachedInventories)
                {
                    var oldest = _inventoryOrder.Dequeue();
                    if (oldest != key)
                        _inventoryCache.Remove(oldest);
                }
                _inventoryCache[key] = result;
                _inventoryOrder.Enqueue(key);
            }
            else
            {
                result = existing;
            }
        }
        return result;
    }

    private static List<FileInfo> CollectPatchFiles(Mod mod, string optionsJson, CancellationToken cancellationToken)
    {
        var files = PatchDeploymentPlanner.BuildFiles(
            PatchDeploymentPlanner.CollectSelectedFiles(mod, optionsJson, cancellationToken),
            new(StringComparer.OrdinalIgnoreCase), cancellationToken, mod.Path);
        return files.Where(file => file.Suffix.Length == 0)
            .Select(file => new FileInfo(file.SourcePath!)).ToList();
    }

    private static string CreatePatchSetKey(IReadOnlyList<FileInfo> patches)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var patch in patches.OrderBy(static file => file.FullName, StringComparer.OrdinalIgnoreCase))
        {
            patch.Refresh();
            builder.Append(patch.FullName).Append(':').Append(patch.Length).Append(':')
                .Append(patch.LastWriteTimeUtc.Ticks).Append(';');
        }
        return builder.ToString();
    }

    private void TouchCache(string key)
    {
        if (_inventoryOrder.Count > 0 && _inventoryOrder.Peek() == key)
            return;
        var remaining = _inventoryOrder.ToList();
        remaining.Remove(key);
        _inventoryOrder.Clear();
        foreach (var item in remaining)
            _inventoryOrder.Enqueue(item);
        _inventoryOrder.Enqueue(key);
    }

    private async Task<Mod> GetModAsync(Guid id, CancellationToken token) =>
        await mods.GetByIdAsync(id, token) ?? throw new KeyNotFoundException($"Mod {id} not found.");

    public void Dispose()
    {
        lock (_cacheGate)
        {
            _inventoryCache.Clear();
            _inventoryOrder.Clear();
        }
        baselines.Clear();
    }
}
