namespace HD2Mimnt.Infrastructure.Analysis;

/// <summary>进程内 LRU 索引缓存；仅保存完整解析结果，不缓存目录、选项或部署顺序。</summary>
public sealed class PatchResourceIndexCache(long maximumBytes = 64 * 1024 * 1024, int maximumFiles = 8192)
{
    private sealed record Stamp(long Length, DateTime Modified, DateTime Created);
    private sealed record Entry(Stamp Stamp, IReadOnlyList<PatchResourceEntry> Resources, long Bytes, LinkedListNode<string> Node);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private long _bytes;

    public IReadOnlyList<PatchResourceEntry> Read(string path, CancellationToken token, out bool cached)
    {
        token.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        var before = GetStamp(path);
        lock (_gate)
        {
            if (_entries.TryGetValue(path, out var existing))
            {
                if (existing.Stamp == before)
                {
                    _lru.Remove(existing.Node);
                    _lru.AddLast(existing.Node);
                    cached = true;
                    return existing.Resources;
                }
                Remove(path);
            }
        }
        cached = false;
        var resources = PatchResourceIndexReader.Read(path, token);
        var after = GetStamp(path);
        if (before != after) throw new IOException("补丁在检查时发生变更，请重新检查");
        token.ThrowIfCancellationRequested();
        var bytes = 256L + path.Length * 2L
            + resources.Count * (long)System.Runtime.CompilerServices.Unsafe.SizeOf<PatchResourceEntry>();
        if (bytes > maximumBytes || maximumFiles <= 0) return resources;
        lock (_gate)
        {
            Remove(path);
            while (_entries.Count >= maximumFiles || _bytes + bytes > maximumBytes)
                Remove(_lru.First!.Value);
            var node = _lru.AddLast(path);
            _entries.Add(path, new(after, resources, bytes, node));
            _bytes += bytes;
        }
        return resources;
    }

    private static Stamp GetStamp(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("补丁文件不存在", path);
        return new(info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
    }

    private void Remove(string path)
    {
        if (!_entries.Remove(path, out var entry)) return;
        _lru.Remove(entry.Node);
        _bytes -= entry.Bytes;
    }
}
