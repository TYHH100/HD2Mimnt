using System.Collections.Concurrent;
using System.Security.Cryptography;
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

    /// <summary>同一路径的内容哈希缓存（键含长度和修改时间），避免每次打开来源相同的图片都重读整份文件。</summary>
    private static readonly ConcurrentDictionary<string, string> ContentHashCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 按「内容哈希 + 解码宽度」复用位图：内容相同的副本（例如 HD2IAS 组件目录里复制的同一张预览图）
    /// 只解码一次并共享同一 Bitmap，不按路径反复解码同一张图。
    /// </summary>
    private static readonly ConcurrentDictionary<string, WeakReference<Bitmap>> ContentCache =
        new(StringComparer.Ordinal);

    private static string CreateKey(string path, int decodeWidth)
    {
        long stamp;
        try { stamp = File.GetLastWriteTimeUtc(path).Ticks; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { stamp = 0; }
        return $"{decodeWidth}|{stamp}|{path}";
    }

    private static string CreateContentKey(string hash, int decodeWidth) => $"{decodeWidth}|{hash}";

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
        if (token.IsCancellationRequested)
        {
            // 取消后不再发布到缓存，也要释放已经解码出来的原生位图。
            bitmap?.Dispose();
            token.ThrowIfCancellationRequested();
        }
        if (bitmap != null) Cache[CreateKey(path, decodeWidth)] = new WeakReference<Bitmap>(bitmap);
        return bitmap;
    }

    /// <summary>
    /// 按内容哈希复用解码结果：先查路径缓存，再查相同内容（不同路径）的缓存，最后才真正解码。
    /// 适用于选项弹窗这类同一张图被多个条目引用、或导入时被复制成多份的场景。
    /// </summary>
    public static async Task<Bitmap?> DecodeSharedAsync(string path, int decodeWidth = 256, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (TryGet(path, decodeWidth) is { } cached) return cached;

        var hash = await GetContentHashAsync(path, token);
        if (hash != null
            && ContentCache.TryGetValue(CreateContentKey(hash, decodeWidth), out var reference)
            && reference.TryGetTarget(out var shared) && shared != null)
        {
            // 命中的可能是另一条路径的相同内容：补上路径缓存，之后不再计算哈希。
            Cache[CreateKey(path, decodeWidth)] = new WeakReference<Bitmap>(shared);
            return shared;
        }

        var bitmap = await DecodeAsync(path, decodeWidth, token);
        if (bitmap != null && hash != null)
            ContentCache[CreateContentKey(hash, decodeWidth)] = new WeakReference<Bitmap>(bitmap);
        return bitmap;
    }

    /// <summary>
    /// 释放共享位图前先移出缓存，避免后续相同路径/内容命中已 Dispose 的旧对象。
    /// </summary>
    public static void Forget(Bitmap image)
    {
        foreach (var pair in Cache)
            if (pair.Value.TryGetTarget(out var cached) && ReferenceEquals(cached, image))
                Cache.TryRemove(pair.Key, out _);
        foreach (var pair in ContentCache)
            if (pair.Value.TryGetTarget(out var cached) && ReferenceEquals(cached, image))
                ContentCache.TryRemove(pair.Key, out _);
    }

    /// <summary>
    /// 读取文件内容哈希（同一路径的哈希会缓存到长度/修改时间变化为止）。
    /// 文件不可读时返回 null，让调用方回退到按路径处理。
    /// </summary>
    internal static async Task<string?> GetContentHashAsync(string path, CancellationToken token = default)
    {
        long length;
        long stamp;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            length = info.Length;
            stamp = info.LastWriteTimeUtc.Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        var key = $"{length}|{stamp}|{path}";
        if (ContentHashCache.TryGetValue(key, out var cached)) return cached;

        string hash;
        try
        {
            hash = await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return Convert.ToHexString(SHA256.HashData(stream));
            }, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        ContentHashCache[key] = hash;
        return hash;
    }
}
