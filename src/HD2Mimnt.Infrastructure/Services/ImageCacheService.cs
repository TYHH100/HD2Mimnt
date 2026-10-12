using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace HD2Mimnt.Infrastructure.Services;

/// <summary>
/// 图片缓存服务实现
/// 两层缓存：内存 LRU + 磁盘缓存
/// </summary>
public class ImageCacheService : IImageCacheService
{
    private readonly Cache.LruCache<string, Bitmap> _memoryCache;
    private readonly string _diskCachePath;
    private readonly SemaphoreSlim _diskLock = new(1, 1);
    private int _cacheHits;
    private int _cacheMisses;

    private const int DefaultMemoryCacheSize = 100; // 内存中最多缓存 100 张图片
    private const int MaxDiskCacheAgeDays = 30; // 磁盘缓存最长保留 30 天

    public ImageCacheService(string? cachePath = null, int memoryCacheSize = DefaultMemoryCacheSize)
    {
        _memoryCache = new Cache.LruCache<string, Bitmap>(memoryCacheSize);
        _diskCachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HD2Mimnt.dev",
            "ImageCache");

        Directory.CreateDirectory(_diskCachePath);
    }

    public async Task<Bitmap?> GetOrLoadAsync(
        string key,
        Func<CancellationToken, Task<Bitmap?>> loadFunc,
        CancellationToken cancellationToken = default)
    {
        // 1. 尝试从内存缓存获取
        if (_memoryCache.TryGet(key, out var cached))
        {
            Interlocked.Increment(ref _cacheHits);
            return cached;
        }

        // 2. 尝试从磁盘缓存获取
        var diskCached = await LoadFromDiskCacheAsync(key, cancellationToken);
        if (diskCached != null)
        {
            _memoryCache.Add(key, diskCached);
            Interlocked.Increment(ref _cacheHits);
            return diskCached;
        }

        // 3. 缓存未命中，调用加载函数
        Interlocked.Increment(ref _cacheMisses);
        var bitmap = await loadFunc(cancellationToken);

        if (bitmap != null)
        {
            // 保存到缓存
            _memoryCache.Add(key, bitmap);
            await SaveToDiskCacheAsync(key, bitmap, cancellationToken);
        }

        return bitmap;
    }

    public Task<Bitmap?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_memoryCache.TryGet(key, out var cached))
        {
            return Task.FromResult<Bitmap?>(cached);
        }

        return LoadFromDiskCacheAsync(key, cancellationToken);
    }

    public async Task PreloadAsync(string key, Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        _memoryCache.Add(key, bitmap);
        await SaveToDiskCacheAsync(key, bitmap, cancellationToken);
    }

    public void ClearMemoryCache()
    {
        _memoryCache.Clear();
    }

    public async Task ClearDiskCacheAsync(CancellationToken cancellationToken = default)
    {
        await _diskLock.WaitAsync(cancellationToken);
        try
        {
            if (Directory.Exists(_diskCachePath))
            {
                foreach (var file in Directory.GetFiles(_diskCachePath))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // 忽略删除失败
                    }
                }
            }
        }
        finally
        {
            _diskLock.Release();
        }
    }

    public async Task ClearAllCachesAsync(CancellationToken cancellationToken = default)
    {
        ClearMemoryCache();
        await ClearDiskCacheAsync(cancellationToken);
    }

    public ImageCacheStats GetStats()
    {
        var diskCacheCount = 0;
        long diskCacheSize = 0;

        if (Directory.Exists(_diskCachePath))
        {
            var files = Directory.GetFiles(_diskCachePath);
            diskCacheCount = files.Length;
            diskCacheSize = files.Sum(f => new FileInfo(f).Length);
        }

        return new ImageCacheStats
        {
            MemoryCacheCount = _memoryCache.Count,
            MemoryCacheSizeBytes = 0, // 难以准确计算内存大小
            DiskCacheCount = diskCacheCount,
            DiskCacheSizeBytes = diskCacheSize,
            CacheHits = _cacheHits,
            CacheMisses = _cacheMisses
        };
    }

    private async Task<Bitmap?> LoadFromDiskCacheAsync(string key, CancellationToken cancellationToken)
    {
        var cacheFilePath = GetDiskCacheFilePath(key);

        if (!File.Exists(cacheFilePath))
            return null;

        await _diskLock.WaitAsync(cancellationToken);
        try
        {
            // 检查文件是否过期
            var fileInfo = new FileInfo(cacheFilePath);
            if (DateTime.Now - fileInfo.LastAccessTime > TimeSpan.FromDays(MaxDiskCacheAgeDays))
            {
                File.Delete(cacheFilePath);
                return null;
            }

            // 更新访问时间
            File.SetLastAccessTime(cacheFilePath, DateTime.Now);

            // 加载图片
            using var stream = File.OpenRead(cacheFilePath);
            return new Bitmap(stream);
        }
        catch
        {
            // 加载失败，删除损坏的缓存文件
            try
            {
                File.Delete(cacheFilePath);
            }
            catch
            {
                // 忽略删除失败
            }

            return null;
        }
        finally
        {
            _diskLock.Release();
        }
    }

    private async Task SaveToDiskCacheAsync(string key, Bitmap bitmap, CancellationToken cancellationToken)
    {
        var cacheFilePath = GetDiskCacheFilePath(key);

        await _diskLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath)!);

            // 保存为 PNG 格式
            bitmap.Save(cacheFilePath);
        }
        catch
        {
            // 忽略保存失败
        }
        finally
        {
            _diskLock.Release();
        }
    }

    private string GetDiskCacheFilePath(string key)
    {
        // 使用 SHA256 生成文件名，避免路径长度限制和特殊字符
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var fileName = Convert.ToHexString(hash) + ".png";
        return Path.Combine(_diskCachePath, fileName);
    }
}
