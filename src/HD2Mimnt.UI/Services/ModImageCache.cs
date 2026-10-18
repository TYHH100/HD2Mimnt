using System.Collections.Concurrent;
using Avalonia.Media.Imaging;

namespace HD2Mimnt.UI.Services;

/// <summary>
/// 进程级缩略图解码缓存：库页与配置页共享同一份位图，页面切换或刷新时不再重复解码。
/// 使用弱引用保存，不会阻止 GC 回收，也不会把整库图片永久钉在内存里；
/// 缓存键包含文件修改时间，缩略图重新生成后不会取到旧图。
/// </summary>
internal static class ModImageCache
{
    private static readonly ConcurrentDictionary<string, WeakReference<Bitmap>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static string CreateKey(string path, int decodeWidth)
    {
        long stamp;
        try { stamp = File.GetLastWriteTimeUtc(path).Ticks; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { stamp = 0; }
        return $"{decodeWidth}|{stamp}|{path}";
    }

    public static Bitmap? TryGet(string path, int decodeWidth = 256)
        => Cache.TryGetValue(CreateKey(path, decodeWidth), out var reference)
            && reference.TryGetTarget(out var bitmap) && bitmap != null ? bitmap : null;

    /// <summary>在后台线程解码并缓存；同一路径已有存活位图时直接复用。</summary>
    public static async Task<Bitmap?> DecodeAsync(string path, int decodeWidth = 256, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (TryGet(path, decodeWidth) is { } cached) return cached;
        var bitmap = await Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(path);
                return decodeWidth > 0 ? Bitmap.DecodeToWidth(stream, decodeWidth) : new Bitmap(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // 文件暂不可读或正在生成时允许后续重试，不能永久缓存失败。
                return null;
            }
        }, token);
        token.ThrowIfCancellationRequested();
        if (bitmap != null) Cache[CreateKey(path, decodeWidth)] = new WeakReference<Bitmap>(bitmap);
        return bitmap;
    }
}
