using System.Buffers;

namespace HD2Mimnt.Infrastructure.ModelConversion;

/// <summary>
/// 单个资源的载荷（TOC 之外的 <c>.gpu_resources</c>/<c>.stream</c> 段）。
/// 既可以是内存中的字节数组，也可以只记录「文件 + 偏移 + 长度」，由读写方按需读取，
/// 这样超过 .NET 单数组上限（约 2 GB）的补丁边车不必整块载入。
/// </summary>
public abstract class IasPayload
{
    public static IasPayload Empty { get; } = new MemoryPayload([]);

    public abstract long Length { get; }

    /// <summary>是否为内存载荷；分段哈希可据此走零拷贝路径。</summary>
    public abstract bool InMemory { get; }

    /// <summary>连续字节视图，仅用于需要整块解析的小资源（Unit 顶点、材质 TOC）。</summary>
    public abstract ReadOnlySpan<byte> Span { get; }

    /// <summary>把指定偏移的一段载荷复制到目标缓冲区，不要求整块可寻址。</summary>
    public abstract void CopyTo(long offset, Span<byte> destination);

    /// <summary>顺序写出整段载荷，供归档写入与校验使用。</summary>
    public abstract void WriteTo(Stream stream, CancellationToken token);

    public abstract IasPayload Slice(long offset, long length);

    public static IasPayload From(byte[] data) => data.Length == 0 ? Empty : new MemoryPayload(data);

    public static IasPayload From(string path, long offset, long length)
        => length == 0 ? Empty : new FilePayload(path, offset, length);

    public static implicit operator IasPayload(byte[] data) => From(data);
}

internal sealed class MemoryPayload(byte[] data, int offset = 0, int length = -1) : IasPayload
{
    private readonly int _length = length < 0 ? data.Length : length;

    public override long Length => _length;

    public override bool InMemory => true;

    public override ReadOnlySpan<byte> Span => data.AsSpan(offset, _length);

    public override void CopyTo(long position, Span<byte> destination)
    {
        if (position < 0 || position + destination.Length > _length)
            throw new InvalidDataException("模型数据检查：resource payload slice out of range");
        Span.Slice((int)position, destination.Length).CopyTo(destination);
    }

    public override void WriteTo(Stream stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        stream.Write(Span);
    }

    public override IasPayload Slice(long position, long count)
    {
        if (position < 0 || count < 0 || position + count > _length)
            throw new InvalidDataException("模型数据检查：resource payload slice out of range");
        return count == 0 ? Empty : new MemoryPayload(data, offset + (int)position, (int)count);
    }
}

/// <summary>
/// 指向补丁边车文件一段的载荷。整块视图首次访问时缓存，顶点解析这类密集读取不必反复读盘；
/// 超过单数组上限的载荷不会缓存，只能按段复制或顺序写出。
/// </summary>
internal sealed class FilePayload(string path, long offset, long length) : IasPayload
{
    private const int ChunkBytes = 1 << 20;
    private byte[]? _cache;
    private readonly Lock _gate = new();

    public override long Length => length;

    public override bool InMemory => false;

    public override ReadOnlySpan<byte> Span
    {
        get
        {
            if (length > Array.MaxLength)
                throw new InvalidDataException(
                    $"模型数据检查：{Path.GetFileName(path)} 中的资源载荷为 {length:N0} 字节，超过单块解析上限，"
                    + "不能整块载入内存。");
            if (_cache is { } cached) return cached;
            lock (_gate)
            {
                if (_cache is { } ready) return ready;
                var buffer = new byte[length];
                using var source = Open();
                source.Seek(offset, SeekOrigin.Begin);
                source.ReadExactly(buffer);
                _cache = buffer;
                return buffer;
            }
        }
    }

    public override void CopyTo(long position, Span<byte> destination)
    {
        if (position < 0 || position + destination.Length > length)
            throw new InvalidDataException("模型数据检查：resource payload slice out of range");
        if (_cache is { } cached)
        {
            cached.AsSpan((int)position, destination.Length).CopyTo(destination);
            return;
        }
        using var source = Open();
        source.Seek(offset + position, SeekOrigin.Begin);
        source.ReadExactly(destination);
    }

    public override void WriteTo(Stream stream, CancellationToken token)
    {
        using var source = Open();
        source.Seek(offset, SeekOrigin.Begin);
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0)
                    throw new InvalidDataException($"模型数据检查：{Path.GetFileName(path)} 读取提前结束。");
                stream.Write(buffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public override IasPayload Slice(long position, long count)
    {
        if (position < 0 || count < 0 || position + count > length)
            throw new InvalidDataException("模型数据检查：resource payload slice out of range");
        return count == 0 ? Empty : new FilePayload(path, offset + position, count);
    }

    private FileStream Open()
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
}
