using Avalonia.Media.Imaging;

namespace HD2Mimnt.Infrastructure.Services;

/// <summary>
/// 图片缓存服务接口
/// 提供多层缓存：内存 LRU + 磁盘缓存
/// </summary>
public interface IImageCacheService
{
    /// <summary>
    /// 获取或加载图片
    /// </summary>
    /// <param name="key">缓存键（通常是文件路径或 URL）</param>
    /// <param name="loadFunc">加载函数，当缓存未命中时调用</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>位图对象</returns>
    Task<Bitmap?> GetOrLoadAsync(
        string key,
        Func<CancellationToken, Task<Bitmap?>> loadFunc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 从缓存中获取图片（不加载）
    /// </summary>
    Task<Bitmap?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// 预加载图片到缓存
    /// </summary>
    Task PreloadAsync(string key, Bitmap bitmap, CancellationToken cancellationToken = default);

    /// <summary>
    /// 清除内存缓存
    /// </summary>
    void ClearMemoryCache();

    /// <summary>
    /// 清除磁盘缓存
    /// </summary>
    Task ClearDiskCacheAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 清除所有缓存
    /// </summary>
    Task ClearAllCachesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取缓存统计信息
    /// </summary>
    ImageCacheStats GetStats();
}

/// <summary>
/// 图片缓存统计信息
/// </summary>
public record ImageCacheStats
{
    public int MemoryCacheCount { get; init; }
    public long MemoryCacheSizeBytes { get; init; }
    public int DiskCacheCount { get; init; }
    public long DiskCacheSizeBytes { get; init; }
    public int CacheHits { get; init; }
    public int CacheMisses { get; init; }
}
