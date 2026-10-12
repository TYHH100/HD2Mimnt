using System.Buffers.Binary;
using System.Buffers;

namespace HD2Mimnt.Infrastructure.Analysis;

public readonly record struct PatchResourceEntry(ulong ResourceId, ulong TypeId, uint DataSize,
    uint StreamSize, uint GpuSize, uint? UnitVersion);

/// <summary>只读取 TOC 和 Unit 版本，不加载整份补丁或伴随文件。</summary>
public static class PatchResourceIndexReader
{
    private const ulong UnitTypeId = 0xe0a48d0be9a7453f;

    public static IReadOnlyList<PatchResourceEntry> Read(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // 部署占位文件不含资源。
        if (stream.Length == 0) return Array.Empty<PatchResourceEntry>();
        Span<byte> header = stackalloc byte[72];
        if (stream.Length < header.Length) throw new InvalidDataException("补丁头部不完整");
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0xF0000011)
            throw new InvalidDataException("不是有效的 Stingray 补丁");
        var typeCount = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        var fileCount = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var entriesStart = 72L + 32L * typeCount;
        var entriesEnd = entriesStart + 80L * fileCount;
        if (entriesEnd > stream.Length) throw new InvalidDataException("补丁资源索引超出文件长度");

        if (fileCount > int.MaxValue) throw new InvalidDataException("补丁资源索引过大");
        var resources = new List<PatchResourceEntry>((int)fileCount);
        var buffer = ArrayPool<byte>.Shared.Rent(80 * 512);
        Span<byte> version = stackalloc byte[4];
        try
        {
            for (long chunk = 0; chunk < fileCount; chunk += 512)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(512, fileCount - chunk);
                stream.Position = entriesStart + chunk * 80;
                stream.ReadExactly(buffer.AsSpan(0, count * 80));
                for (var index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = buffer.AsSpan(index * 80, 80);
                    var id = BinaryPrimitives.ReadUInt64LittleEndian(entry);
                    var type = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
                    var offset = BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]);
                    var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[56..]);
                    if (size > 0 && (offset < (ulong)entriesEnd || offset > (ulong)stream.Length
                        || size > (ulong)stream.Length - offset))
                        throw new InvalidDataException($"资源 0x{id:X16} 主数据超出文件范围");
                    uint? unitVersion = null;
                    if (type == UnitTypeId && size > 0)
                    {
                        if (size < 0x30) throw new InvalidDataException($"Unit 0x{id:X16} 版本头部不完整");
                        stream.Position = (long)offset + 0x2C;
                        stream.ReadExactly(version);
                        unitVersion = BinaryPrimitives.ReadUInt32LittleEndian(version);
                    }
                    resources.Add(new(id, type, size, BinaryPrimitives.ReadUInt32LittleEndian(entry[60..]),
                        BinaryPrimitives.ReadUInt32LittleEndian(entry[64..]), unitVersion));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return resources;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
