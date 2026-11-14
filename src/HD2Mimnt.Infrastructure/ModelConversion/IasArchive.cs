using System.Buffers.Binary;

namespace HD2Mimnt.Infrastructure.ModelConversion;

/// <summary>Stingray Archive 的资源类型与上游固定 ID。</summary>
public static class IasResourceTypes
{
    public const ulong Unit = 0xE0A48D0BE9A7453F;
    public const ulong Package = 0xAD9C6D9ED1E5E77A;
    public const ulong Material = 0xEAC0B497876ADEDF;
    public const ulong Texture = 0xCD4238C6A0C69E32;
    public const ulong Lua = 0xA14E8DFA2CD117E2;
    /// <summary>murmur64("bones")，不直接等于 Unit 类型。</summary>
    public const ulong Bones = 0x18DEAD01056B72E9;
    public const ulong Avatar = 5556372446766824087;
}

/// <summary>一条资源索引记录，字段顺序与上游 <c>ROW = &lt;7Q6I</c> 对应。</summary>
public readonly record struct IasArchiveRow(
    ulong Id, ulong Type, ulong TocOffset, ulong StreamOffset, ulong GpuOffset,
    uint Meta0, uint Meta1, uint TocSize, uint StreamSize, uint GpuSize, uint Meta2, uint Meta3, uint Index);

/// <summary>单个资源的三个载荷段；EntryMetadata 对应上游 [meta0, meta1, meta2, meta3]。</summary>
public sealed record IasResource(byte[] Toc, byte[] Gpu, byte[] Stream, int[]? EntryMetadata = null, string? Origin = null)
{
    public int[] Metadata => EntryMetadata ?? [0, 0, 16, 64];
}

public sealed record IasArchiveBuffers(byte[] Toc, byte[] Gpu, byte[] Stream);

/// <summary>只读写上游使用的 Stingray Archive 子集，不重建 Unit 图或材质。</summary>
public static class IasArchive
{
    public const uint Magic = 0xF0000011;
    public const int HeaderSize = 72;
    public const int TypeRecordSize = 32;
    public const int RowSize = 80;
    public const ulong TypeRecordTail = 0x4000000010UL;

    public static IReadOnlyDictionary<(ulong Type, ulong Id), IasArchiveRow> ReadRows(ReadOnlySpan<byte> toc)
    {
        if (toc.Length < HeaderSize) throw new InvalidDataException("模型数据检查：archive header");
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(toc);
        var typeCount = BinaryPrimitives.ReadUInt32LittleEndian(toc[4..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(toc[8..]);
        if (magic != Magic || typeCount > 1024 || count > 100_000)
            throw new InvalidDataException("模型数据检查：archive header");
        var tableEnd = HeaderSize + (long)TypeRecordSize * typeCount + (long)RowSize * count;
        if (tableEnd > toc.Length) throw new InvalidDataException("模型数据检查：short archive table");
        var result = new Dictionary<(ulong, ulong), IasArchiveRow>((int)count);
        for (var index = 0; index < count; index++)
        {
            var offset = HeaderSize + (int)(TypeRecordSize * typeCount) + RowSize * index;
            var row = new IasArchiveRow(
                BinaryPrimitives.ReadUInt64LittleEndian(toc[offset..]),
                BinaryPrimitives.ReadUInt64LittleEndian(toc[(offset + 8)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(toc[(offset + 16)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(toc[(offset + 24)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(toc[(offset + 32)..]),
                (uint)BinaryPrimitives.ReadUInt64LittleEndian(toc[(offset + 40)..]),
                (uint)BinaryPrimitives.ReadUInt64LittleEndian(toc[(offset + 48)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(toc[(offset + 56)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(toc[(offset + 60)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(toc[(offset + 64)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(toc[(offset + 68)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(toc[(offset + 72)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(toc[(offset + 76)..]));
            if (!result.TryAdd((row.Type, row.Id), row))
                throw new InvalidDataException("模型数据检查：duplicate archive identity");
        }
        return result;
    }

    public static IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> ReadEntries(
        ReadOnlySpan<byte> toc, ReadOnlySpan<byte> gpu, ReadOnlySpan<byte> stream)
    {
        var result = new Dictionary<(ulong, ulong), IasResource>();
        foreach (var (key, row) in ReadRows(toc))
        {
            var tocData = Slice(toc, row.TocOffset, row.TocSize, "TOC", key);
            var gpuData = Slice(gpu, row.GpuOffset, row.GpuSize, ".gpu_resources", key);
            var streamData = Slice(stream, row.StreamOffset, row.StreamSize, ".stream", key);
            result[key] = new IasResource(tocData, gpuData, streamData,
                [(int)row.Meta0, (int)row.Meta1, (int)row.Meta2, (int)row.Meta3]);
        }
        return result;
    }

    private static byte[] Slice(ReadOnlySpan<byte> raw, ulong offset, uint size, string name, (ulong Type, ulong Id) key)
    {
        if (size == 0) return [];
        if (offset + size > (ulong)raw.Length)
            throw new InvalidDataException(
                $"模型数据检查：truncated resource payload ({name} {key.Type:x16}/{key.Id:x16}, offset {offset}, length {size}, file size {raw.Length})");
        return raw.Slice((int)offset, (int)size).ToArray();
    }

    /// <summary>按上游布局写出 TOC/GPU/Stream；TOC 段按 16 字节、其余按 64 字节对齐。</summary>
    public static IasArchiveBuffers Write(IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> entries)
    {
        var ordered = entries.OrderBy(entry => entry.Key.Type).ThenBy(entry => entry.Key.Id).ToList();
        var kinds = entries.Keys.Select(key => key.Type).Distinct().Order().ToArray();
        var counts = entries.Keys.GroupBy(key => key.Type).ToDictionary(group => group.Key, group => group.Count());
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)kinds.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)entries.Count);
        var types = new byte[TypeRecordSize * kinds.Length];
        for (var index = 0; index < kinds.Length; index++)
        {
            var offset = index * TypeRecordSize;
            BinaryPrimitives.WriteUInt64LittleEndian(types.AsSpan(offset), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(types.AsSpan(offset + 8), kinds[index]);
            BinaryPrimitives.WriteUInt64LittleEndian(types.AsSpan(offset + 16), (ulong)counts[kinds[index]]);
            BinaryPrimitives.WriteUInt64LittleEndian(types.AsSpan(offset + 24), TypeRecordTail);
        }

        // 先按对齐规则算出精确长度再一次性分配，避免 List<byte> 扩容与 ToArray 造成的多份副本。
        long tocLength = HeaderSize + types.Length + (long)RowSize * ordered.Count;
        long gpuLength = 0;
        long streamLength = 0;
        foreach (var (_, value) in ordered)
        {
            ValidateMetadata(value);
            tocLength = AlignedLength(tocLength, value.Toc.Length, 16);
            gpuLength = AlignedLength(gpuLength, value.Gpu.Length, 64);
            streamLength = AlignedLength(streamLength, value.Stream.Length, 64);
        }

        var toc = new byte[checked((int)tocLength)];
        var gpu = new byte[checked((int)gpuLength)];
        var stream = new byte[checked((int)streamLength)];
        header.CopyTo(toc, 0);
        types.CopyTo(toc, HeaderSize);
        long tocPosition = HeaderSize + types.Length + (long)RowSize * ordered.Count;
        long gpuPosition = 0;
        long streamPosition = 0;
        var rowIndex = 0;
        foreach (var ((type, id), value) in ordered)
        {
            rowIndex++;
            var (tocOffset, tocSize) = Append(toc, ref tocPosition, value.Toc, 16);
            var (gpuOffset, gpuSize) = Append(gpu, ref gpuPosition, value.Gpu, 64);
            var (streamOffset, streamSize) = Append(stream, ref streamPosition, value.Stream, 64);
            var metadata = value.Metadata;
            var rowOffset = checked(HeaderSize + types.Length + RowSize * (rowIndex - 1));
            var row = toc.AsSpan(rowOffset, RowSize);
            BinaryPrimitives.WriteUInt64LittleEndian(row, id);
            BinaryPrimitives.WriteUInt64LittleEndian(row[8..], type);
            BinaryPrimitives.WriteUInt64LittleEndian(row[16..], tocOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(row[24..], streamOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(row[32..], gpuOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(row[40..], (ulong)metadata[0]);
            BinaryPrimitives.WriteUInt64LittleEndian(row[48..], (ulong)metadata[1]);
            BinaryPrimitives.WriteUInt32LittleEndian(row[56..], (uint)tocSize);
            BinaryPrimitives.WriteUInt32LittleEndian(row[60..], (uint)streamSize);
            BinaryPrimitives.WriteUInt32LittleEndian(row[64..], (uint)gpuSize);
            BinaryPrimitives.WriteUInt32LittleEndian(row[68..], (uint)metadata[2]);
            BinaryPrimitives.WriteUInt32LittleEndian(row[72..], (uint)metadata[3]);
            BinaryPrimitives.WriteUInt32LittleEndian(row[76..], (uint)rowIndex);
        }
        return new(toc, gpu, stream);
    }

    /// <summary>不复制载荷地核对刚写出的归档与源资源一致。</summary>
    public static void VerifyReadback(ReadOnlySpan<byte> toc, ReadOnlySpan<byte> gpu, ReadOnlySpan<byte> stream,
        IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> expected)
    {
        var rows = ReadRows(toc);
        if (rows.Count != expected.Count) throw new InvalidDataException("模型补丁回读身份不一致。");
        foreach (var (key, row) in rows)
        {
            if (!expected.TryGetValue(key, out var value)) throw new InvalidDataException("模型补丁回读身份不一致。");
            VerifyPayload(toc, row.TocOffset, row.TocSize, value.Toc, "TOC", key);
            VerifyPayload(gpu, row.GpuOffset, row.GpuSize, value.Gpu, ".gpu_resources", key);
            VerifyPayload(stream, row.StreamOffset, row.StreamSize, value.Stream, ".stream", key);
        }
    }

    private static void ValidateMetadata(IasResource value)
    {
        var metadata = value.Metadata;
        if (metadata.Length != 4 || metadata.Any(item => item < 0))
            throw new InvalidDataException("模型数据检查：invalid archive entry metadata");
    }

    private static long AlignedLength(long position, int length, int alignment)
        => position + (alignment - position % alignment) % alignment + length;

    private static (ulong Offset, uint Size) Append(byte[] buffer, ref long position, byte[] data, int alignment)
    {
        position += (alignment - position % alignment) % alignment;
        var offset = data.Length > 0 ? (ulong)position : 0UL;
        if (data.Length > 0) data.CopyTo(buffer, position);
        position += data.Length;
        return (offset, (uint)data.Length);
    }

    private static void VerifyPayload(ReadOnlySpan<byte> raw, ulong offset, uint size, byte[] expected,
        string name, (ulong Type, ulong Id) key)
    {
        if (size != (uint)expected.Length) throw new InvalidDataException("模型补丁回读载荷不一致。");
        if (size == 0) return;
        if (offset + size > (ulong)raw.Length)
            throw new InvalidDataException(
                $"模型补丁回读载荷越界（{name} {key.Type:x16}/{key.Id:x16}, offset {offset}, length {size}）。");
        if (!raw.Slice((int)offset, (int)size).SequenceEqual(expected))
            throw new InvalidDataException("模型补丁回读载荷不一致。");
    }
}
