using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// 平台缓存仓储接口
/// 管理平台 Mod 元数据缓存
/// </summary>
public interface IPlatformCacheRepository
{
    /// <summary>
    /// 获取指定平台和 Mod 的缓存
    /// </summary>
    Task<PlatformCache?> GetAsync(string platformId, string platformModId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 添加缓存条目
    /// </summary>
    Task AddAsync(PlatformCache cache, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新缓存条目
    /// </summary>
    Task UpdateAsync(PlatformCache cache, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除缓存条目
    /// </summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取所有已过期的缓存条目
    /// </summary>
    Task<List<PlatformCache>> GetExpiredAsync(DateTime now, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除所有已过期的缓存条目
    /// </summary>
    Task DeleteExpiredAsync(DateTime now, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取缓存条目总数
    /// </summary>
    Task<int> GetCacheSizeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 清空缓存（可选择仅清空特定平台）
    /// </summary>
    Task ClearAsync(string? platformId = null, CancellationToken cancellationToken = default);
}
