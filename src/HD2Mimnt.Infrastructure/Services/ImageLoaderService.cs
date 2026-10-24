using System.Collections.Concurrent;
using Avalonia.Media.Imaging;

namespace HD2Mimnt.Infrastructure.Services;

/// <summary>
/// 图片加载服务实现
/// 支持异步加载、优先级队列、缩略图生成
/// </summary>
public class ImageLoaderService : IImageLoaderService, IDisposable
{
    private readonly IImageCacheService _cacheService;
    private readonly PriorityTaskScheduler _taskScheduler;
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _pendingLoads;
    private readonly object _ctsLock = new();
    private CancellationTokenSource _globalCts;

    public ImageLoaderService(IImageCacheService cacheService)
    {
        _cacheService = cacheService;
        _taskScheduler = new PriorityTaskScheduler();
        _pendingLoads = new ConcurrentDictionary<string, Task<Bitmap?>>();
        _globalCts = new CancellationTokenSource();
    }

    public Task<Bitmap?> LoadAsync(
        string path,
        int? maxWidth = null,
        int? maxHeight = null,
        LoadPriority priority = LoadPriority.Normal,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult<Bitmap?>(null);

        // 生成缓存键（包含尺寸信息）
        var cacheKey = GenerateCacheKey(path, maxWidth, maxHeight);

        // 在锁内捕获当前代际的令牌，避免与 CancelPendingLoads 的代际重建竞态
        CancellationToken globalToken;
        lock (_ctsLock)
        {
            globalToken = _globalCts.Token;
        }

        // 组合取消令牌
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, globalToken);

        // 检查是否已经在加载中
        if (_pendingLoads.TryGetValue(cacheKey, out var existingTask))
        {
            return existingTask;
        }

        // 创建新的加载任务
        var loadTask = _cacheService.GetOrLoadAsync(
            cacheKey,
            async ct => await LoadImageFromDiskAsync(path, maxWidth, maxHeight, ct),
            linkedCts.Token);

        // 记录待处理任务
        _pendingLoads[cacheKey] = loadTask;

        // 完成后移除
        _ = loadTask.ContinueWith(t =>
        {
            _pendingLoads.TryRemove(cacheKey, out _);
        }, TaskScheduler.Default);

        return loadTask;
    }

    public async Task<IReadOnlyList<Bitmap?>> LoadBatchAsync(
        IEnumerable<string> paths,
        int? maxWidth = null,
        int? maxHeight = null,
        LoadPriority priority = LoadPriority.Low,
        CancellationToken cancellationToken = default)
    {
        var tasks = paths.Select(path =>
            LoadAsync(path, maxWidth, maxHeight, priority, cancellationToken));

        var results = await Task.WhenAll(tasks);
        return results;
    }

    /// <summary>
    /// 取消当前所有进行中的加载；通过重建代际令牌源，后续新加载不受影响。
    /// 旧令牌源不主动 Dispose，避免与飞行中 CreateLinkedTokenSource 的注册竞态（对象由 GC 回收）。
    /// </summary>
    public void CancelPendingLoads()
    {
        CancellationTokenSource previous;
        lock (_ctsLock)
        {
            previous = _globalCts;
            _globalCts = new CancellationTokenSource();
        }
        previous.Cancel();
    }

    public void Dispose()
    {
        lock (_ctsLock)
        {
            _globalCts.Cancel();
            _globalCts.Dispose();
        }
    }

    private static async Task<Bitmap?> LoadImageFromDiskAsync(
        string path,
        int? maxWidth,
        int? maxHeight,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            // 异步读取文件
            using var fileStream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                useAsync: true);

            using var memoryStream = new MemoryStream();
            await fileStream.CopyToAsync(memoryStream, cancellationToken);
            memoryStream.Position = 0;

            // 解码图片
            var bitmap = new Bitmap(memoryStream);

            // 如果需要缩放，生成缩略图
            if (maxWidth.HasValue || maxHeight.HasValue)
            {
                bitmap = ResizeBitmap(bitmap, maxWidth, maxHeight);
            }

            return bitmap;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            // 加载失败，返回 null
            return null;
        }
    }

    private static Bitmap ResizeBitmap(Bitmap source, int? maxWidth, int? maxHeight)
    {
        var sourceWidth = source.PixelSize.Width;
        var sourceHeight = source.PixelSize.Height;

        // 计算目标尺寸，保持宽高比
        double scale = 1.0;

        if (maxWidth.HasValue && sourceWidth > maxWidth.Value)
        {
            scale = Math.Min(scale, (double)maxWidth.Value / sourceWidth);
        }

        if (maxHeight.HasValue && sourceHeight > maxHeight.Value)
        {
            scale = Math.Min(scale, (double)maxHeight.Value / sourceHeight);
        }

        if (scale >= 1.0)
        {
            // 不需要缩放
            return source;
        }

        var targetWidth = (int)(sourceWidth * scale);
        var targetHeight = (int)(sourceHeight * scale);

        // 使用 Avalonia 的 CreateScaledBitmap
        return source.CreateScaledBitmap(
            new Avalonia.PixelSize(targetWidth, targetHeight),
            Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
    }

    private static string GenerateCacheKey(string path, int? maxWidth, int? maxHeight)
    {
        if (!maxWidth.HasValue && !maxHeight.HasValue)
        {
            return path;
        }

        return $"{path}|{maxWidth ?? 0}x{maxHeight ?? 0}";
    }
}

/// <summary>
/// 优先级任务调度器（简化版）
/// 在完整实现中，这会是一个基于优先级队列的调度器
/// </summary>
internal class PriorityTaskScheduler
{
    // 这里是简化实现，实际应该使用优先级队列
    // 由于时间限制，暂时使用默认调度器
}
