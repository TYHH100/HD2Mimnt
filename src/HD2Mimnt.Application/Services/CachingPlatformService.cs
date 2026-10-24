using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using System.Text.Json;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 带缓存的平台服务
/// 包装平台适配器，添加元数据缓存层
/// </summary>
public class CachingPlatformService
{
    private readonly IPlatformRegistry _registry;
    private readonly IPlatformCacheRepository _cacheRepository;
    private readonly TimeSpan _defaultCacheDuration;

    public CachingPlatformService(
        IPlatformRegistry registry,
        IPlatformCacheRepository cacheRepository,
        TimeSpan? cacheDuration = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _cacheRepository = cacheRepository ?? throw new ArgumentNullException(nameof(cacheRepository));
        _defaultCacheDuration = cacheDuration ?? TimeSpan.FromHours(6);
    }

    /// <summary>
    /// 获取 Mod 信息（优先从缓存）
    /// </summary>
    public async Task<PlatformModInfo?> GetModInfoAsync(
        string platformId,
        string platformModId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        // 检查缓存（除非强制刷新）
        if (!forceRefresh)
        {
            var cached = await _cacheRepository.GetAsync(platformId, platformModId, cancellationToken);
            if (cached != null && cached.ExpiresAt > DateTime.UtcNow)
            {
                // 缓存有效，反序列化并返回
                return JsonSerializer.Deserialize<PlatformModInfo>(cached.CachedData);
            }
        }

        // 缓存未命中或已过期，调用平台适配器
        var platform = _registry.GetPlatform(platformId);
        if (platform == null)
        {
            return null;
        }

        var modInfo = await platform.GetModInfoAsync(platformModId, cancellationToken);
        if (modInfo == null)
        {
            return null;
        }

        // 序列化并存入缓存
        var cacheEntry = new PlatformCache
        {
            Id = Guid.NewGuid(),
            PlatformId = platformId,
            PlatformModId = platformModId,
            CachedData = JsonSerializer.Serialize(modInfo),
            CachedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(_defaultCacheDuration),
            Etag = string.Empty
        };

        // 检查是否已存在缓存条目
        var existing = await _cacheRepository.GetAsync(platformId, platformModId, cancellationToken);
        if (existing != null)
        {
            // 更新现有条目
            cacheEntry.Id = existing.Id;
            await _cacheRepository.UpdateAsync(cacheEntry, cancellationToken);
        }
        else
        {
            // 添加新条目
            await _cacheRepository.AddAsync(cacheEntry, cancellationToken);
        }

        return modInfo;
    }

    /// <summary>
    /// 清理所有过期的缓存条目
    /// </summary>
    public async Task ClearExpiredCachesAsync(CancellationToken cancellationToken = default)
    {
        await _cacheRepository.DeleteExpiredAsync(DateTime.UtcNow, cancellationToken);
    }

    /// <summary>
    /// 获取缓存条目总数
    /// </summary>
    public async Task<int> GetCacheSizeAsync(CancellationToken cancellationToken = default)
    {
        return await _cacheRepository.GetCacheSizeAsync(cancellationToken);
    }

    /// <summary>
    /// 清空缓存（可选择仅清空特定平台）
    /// </summary>
    public async Task ClearCacheAsync(string? platformId = null, CancellationToken cancellationToken = default)
    {
        await _cacheRepository.ClearAsync(platformId, cancellationToken);
    }
}
