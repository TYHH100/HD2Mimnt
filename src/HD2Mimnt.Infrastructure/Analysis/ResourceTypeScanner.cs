using System.Buffers.Binary;

namespace HD2Mimnt.Infrastructure.Analysis;

/// <summary>只读 Stingray TOC 索引，不加载 GPU/stream 数据，不按文件名或图片猜测资源类型。</summary>
public static class ResourceTypeScanner
{
    public static Task<List<ulong>?> ScanAsync(string directory, CancellationToken cancellationToken = default)
        => Task.Run<List<ulong>?>(() =>
        {
            if (!Directory.Exists(directory)) return null;
            var ids = new HashSet<ulong>();
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                var extension = Path.GetExtension(name);
                var isPatch = extension.Equals(".patch", StringComparison.OrdinalIgnoreCase)
                    || (extension.StartsWith(".patch_", StringComparison.OrdinalIgnoreCase)
                        && uint.TryParse(extension.AsSpan(7), out _));
                var isBaseArchive = name.Length == 16 && name.All(Uri.IsHexDigit);
                if (!isPatch && !isBaseArchive) continue;
                foreach (var id in ReadTypeIds(path, cancellationToken)) ids.Add(id);
            }
            return ids.Order().ToList();
        }, cancellationToken);

    public static IReadOnlyList<ulong> ReadTypeIds(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 4) return Array.Empty<ulong>();
        Span<byte> header = stackalloc byte[72];
        stream.ReadExactly(header[..4]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0xF0000011) return Array.Empty<ulong>();
        if (stream.Length < header.Length) throw new InvalidDataException($"TOC 头部不完整: {path}");
        stream.ReadExactly(header[4..]);
        var typeCount = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        var fileCount = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var entriesStart = 72L + 32L * typeCount;
        if (entriesStart + 80L * fileCount > stream.Length)
            throw new InvalidDataException($"TOC 索引超出文件长度: {path}");
        stream.Position = entriesStart;
        Span<byte> entry = stackalloc byte[80];
        var ids = new HashSet<ulong>();
        for (long index = 0; index < fileCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.ReadExactly(entry);
            ids.Add(BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]));
        }
        return ids.Order().ToArray();
    }
}
