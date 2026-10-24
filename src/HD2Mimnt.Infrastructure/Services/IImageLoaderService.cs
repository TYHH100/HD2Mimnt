using Avalonia.Media.Imaging;

namespace HD2Mimnt.Infrastructure.Services;

/// <summary>
/// 图片加载服务接口
/// 支持异步加载、取消、优先级队列
/// </summary>
public interface IImageLoaderService
{
    /// <summary>
    /// 异步加载图片
    /// </summary>
    /// <param name="path">图片路径</param>
    /// <param name="maxWidth">最大宽度（用于生成缩略图）</param>
    /// <param name="maxHeight">最大高度（用于生成缩略图）</param>
    /// <param name="priority">加载优先级（可见的图片优先加载）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>位图对象</returns>
    Task<Bitmap?> LoadAsync(
        string path,
        int? maxWidth = null,
        int? maxHeight = null,
        LoadPriority priority = LoadPriority.Normal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量预加载图片
    /// </summary>
    Task<IReadOnlyList<Bitmap?>> LoadBatchAsync(
        IEnumerable<string> paths,
        int? maxWidth = null,
        int? maxHeight = null,
        LoadPriority priority = LoadPriority.Low,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 取消所有待处理的加载任务
    /// </summary>
    void CancelPendingLoads();
}

/// <summary>
/// 加载优先级
/// </summary>
public enum LoadPriority
{
    /// <summary>
    /// 低优先级（预加载）
    /// </summary>
    Low = 0,

    /// <summary>
    /// 正常优先级
    /// </summary>
    Normal = 1,

    /// <summary>
    /// 高优先级（可见元素）
    /// </summary>
    High = 2,

    /// <summary>
    /// 立即加载（用户交互触发）
    /// </summary>
    Immediate = 3
}
