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
    /// <summary>murmur64("animation")：MMD 动作片段所在的资源类型。</summary>
    public const ulong Animation = 10600967118105529382;
    /// <summary>murmur64("bones")，不直接等于 Unit 类型。</summary>
    public const ulong Bones = 0x18DEAD01056B72E9;
    public const ulong Avatar = 5556372446766824087;
}

/// <summary>一条资源索引记录，字段顺序与上游 <c>ROW = &lt;7Q6I</c> 对应。</summary>
public readonly record struct IasArchiveRow(
    ulong Id, ulong Type, ulong TocOffset, ulong StreamOffset, ulong GpuOffset,
    uint Meta0, uint Meta1, uint TocSize, uint StreamSize, uint GpuSize, uint Meta2, uint Meta3, uint Index);

/// <summary>单个资源的三个载荷段；EntryMetadata 对应上游 [meta0, meta1, meta2, meta3]。</summary>
public sealed record IasResource(byte[] Toc, IasPayload Gpu, IasPayload Stream, int[]? EntryMetadata = null, string? Origin = null)
{
    /// <summary>内存载荷的便捷构造（游戏原生资源与小规模合成资源）。</summary>
    public IasResource(byte[] toc, byte[] gpu, byte[] stream, int[]? entryMetadata = null, string? origin = null)
        : this(toc, IasPayload.From(gpu), IasPayload.From(stream), entryMetadata, origin)
    {
    }

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

    public static IReadOnlyDictionary<(ulong Type, ulong Id), IasArchiveRow> ReadRows(ReadOnlySpan<byte> toc,
        bool replaceDuplicates = false)
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
            {
                if (!replaceDuplicates)
                    throw new InvalidDataException("模型数据检查：duplicate archive identity");
                result[(row.Type, row.Id)] = row;
            }
        }
        return result;
    }

    public static IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> ReadEntries(
        ReadOnlySpan<byte> toc, IasPayload gpu, IasPayload stream, bool replaceDuplicates = false)
    {
        var result = new Dictionary<(ulong, ulong), IasResource>();
        foreach (var (key, row) in ReadRows(toc, replaceDuplicates))
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

    private static IasPayload Slice(IasPayload raw, ulong offset, uint size, string name, (ulong Type, ulong Id) key)
    {
        if (size == 0) return IasPayload.Empty;
        if (offset + size > (ulong)raw.Length)
            throw new InvalidDataException(
                $"模型数据检查：truncated resource payload ({name} {key.Type:x16}/{key.Id:x16}, offset {offset}, length {size}, file size {raw.Length})");
        return raw.Slice((long)offset, size);
    }

    /// <summary>按上游布局把归档写进三个内存缓冲区；仅用于小规模归档与测试。</summary>
    public static IasArchiveBuffers Write(IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> entries)
    {
        using var toc = new MemoryStream();
        using var gpu = new MemoryStream();
        using var stream = new MemoryStream();
        WriteTo(entries, toc, gpu, stream, CancellationToken.None);
        return new(toc.ToArray(), gpu.ToArray(), stream.ToArray());
    }

    /// <summary>
    /// 按上游布局写出 TOC/GPU/Stream：TOC 段按 16 字节、其余按 64 字节对齐。
    /// 载荷边写边读，成品超过 2 GB 时也只占一个缓冲区，不再整块驻留内存。
    /// </summary>
    public static void WriteTo(IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> entries,
        Stream toc, Stream gpu, Stream stream, CancellationToken token)
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

        // 先按对齐规则算出行偏移，再顺序写出载荷：偏移只依赖长度，不依赖内容。
        var tableLength = HeaderSize + types.Length + (long)RowSize * ordered.Count;
        var tocOffsets = new ulong[ordered.Count];
        var gpuOffsets = new ulong[ordered.Count];
        var streamOffsets = new ulong[ordered.Count];
        long tocPosition = tableLength;
        long gpuPosition = 0;
        long streamPosition = 0;
        for (var index = 0; index < ordered.Count; index++)
        {
            var value = ordered[index].Value;
            ValidateMetadata(value);
            tocOffsets[index] = Advance(ref tocPosition, value.Toc.Length, 16);
            gpuOffsets[index] = Advance(ref gpuPosition, value.Gpu.Length, 64);
            streamOffsets[index] = Advance(ref streamPosition, value.Stream.Length, 64);
        }

        var table = new byte[checked((int)tableLength)];
        header.CopyTo(table, 0);
        types.CopyTo(table, HeaderSize);
        for (var index = 0; index < ordered.Count; index++)
        {
            var ((type, id), value) = ordered[index];
            var metadata = value.Metadata;
            var row = table.AsSpan(checked(HeaderSize + types.Length + RowSize * index), RowSize);
            BinaryPrimitives.WriteUInt64LittleEndian(row, id);
            BinaryPrimitives.WriteUInt64LittleEndian(row[8..], type);
            BinaryPrimitives.WriteUInt64LittleEndian(row[16..], tocOffsets[index]);
            BinaryPrimitives.WriteUInt64LittleEndian(row[24..], streamOffsets[index]);
            BinaryPrimitives.WriteUInt64LittleEndian(row[32..], gpuOffsets[index]);
            BinaryPrimitives.WriteUInt64LittleEndian(row[40..], (ulong)metadata[0]);
            BinaryPrimitives.WriteUInt64LittleEndian(row[48..], (ulong)metadata[1]);
            BinaryPrimitives.WriteUInt32LittleEndian(row[56..], (uint)value.Toc.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(row[60..], (uint)value.Stream.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(row[64..], (uint)value.Gpu.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(row[68..], (uint)metadata[2]);
            BinaryPrimitives.WriteUInt32LittleEndian(row[72..], (uint)metadata[3]);
            BinaryPrimitives.WriteUInt32LittleEndian(row[76..], (uint)(index + 1));
        }

        token.ThrowIfCancellationRequested();
        toc.Write(table);
        var tocPayloadPosition = tableLength;
        foreach (var (_, value) in ordered)
        {
            token.ThrowIfCancellationRequested();
            WritePadding(toc, ref tocPayloadPosition, 16);
            toc.Write(value.Toc);
            tocPayloadPosition += value.Toc.Length;
        }
        WritePayloads(ordered, gpu, value => value.Gpu, 64, token);
        WritePayloads(ordered, stream, value => value.Stream, 64, token);
        toc.Flush();
        gpu.Flush();
        stream.Flush();
    }

    private static void WritePayloads(IReadOnlyList<KeyValuePair<(ulong Type, ulong Id), IasResource>> ordered,
        Stream target, Func<IasResource, IasPayload> select, int alignment, CancellationToken token)
    {
        long position = 0;
        foreach (var (_, value) in ordered)
        {
            token.ThrowIfCancellationRequested();
            WritePadding(target, ref position, alignment);
            var payload = select(value);
            if (payload.Length > 0) payload.WriteTo(target, token);
            position += payload.Length;
        }
    }

    private static void WritePadding(Stream target, ref long position, int alignment)
    {
        var padding = (int)((alignment - position % alignment) % alignment);
        if (padding == 0) return;
        Span<byte> zeros = stackalloc byte[64];
        target.Write(zeros[..padding]);
        position += padding;
    }

    private static ulong Advance(ref long position, long length, int alignment)
    {
        if (length > uint.MaxValue)
            throw new InvalidDataException("模型数据检查：单个资源载荷超过归档行上限（4 GB）。");
        position += (alignment - position % alignment) % alignment;
        var offset = length > 0 ? (ulong)position : 0UL;
        position += length;
        return offset;
    }

    /// <summary>写完后逐资源回读核对，载荷按段比较，成品大小不影响内存占用。</summary>
    public static void VerifyReadback(string tocPath, string gpuPath, string streamPath,
        IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> expected)
    {
        var toc = File.ReadAllBytes(tocPath);
        var rows = ReadRows(toc);
        if (rows.Count != expected.Count) throw new InvalidDataException("模型补丁回读身份不一致。");
        using var gpu = OpenRead(gpuPath);
        using var stream = OpenRead(streamPath);
        foreach (var (key, row) in rows)
        {
            if (!expected.TryGetValue(key, out var value)) throw new InvalidDataException("模型补丁回读身份不一致。");
            VerifyPayload(toc, row.TocOffset, row.TocSize, value.Toc, "TOC", key);
            VerifySegment(gpu, row.GpuOffset, row.GpuSize, value.Gpu, ".gpu_resources", key);
            VerifySegment(stream, row.StreamOffset, row.StreamSize, value.Stream, ".stream", key);
        }
    }

    /// <summary>内存归档的回读校验；载荷可以是文件视图，按段比较，不要求整块可寻址。</summary>
    public static void VerifyReadback(ReadOnlySpan<byte> toc, ReadOnlySpan<byte> gpu, ReadOnlySpan<byte> stream,
        IReadOnlyDictionary<(ulong Type, ulong Id), IasResource> expected)
    {
        var rows = ReadRows(toc);
        if (rows.Count != expected.Count) throw new InvalidDataException("模型补丁回读身份不一致。");
        foreach (var (key, row) in rows)
        {
            if (!expected.TryGetValue(key, out var value)) throw new InvalidDataException("模型补丁回读身份不一致。");
            VerifyPayload(toc, row.TocOffset, row.TocSize, value.Toc, "TOC", key);
            VerifySegment(gpu, row.GpuOffset, row.GpuSize, value.Gpu, ".gpu_resources", key);
            VerifySegment(stream, row.StreamOffset, row.StreamSize, value.Stream, ".stream", key);
        }
    }

    private static void VerifySegment(ReadOnlySpan<byte> raw, ulong offset, uint size, IasPayload expected,
        string name, (ulong Type, ulong Id) key)
    {
        if (size != (uint)expected.Length) throw new InvalidDataException("模型补丁回读载荷不一致。");
        if (size == 0) return;
        if (offset + size > (ulong)raw.Length)
            throw new InvalidDataException(
                $"模型补丁回读载荷越界（{name} {key.Type:x16}/{key.Id:x16}, offset {offset}, length {size}）。");
        var buffer = new byte[1 << 20];
        long position = 0;
        while (position < size)
        {
            var count = (int)Math.Min(buffer.Length, size - position);
            expected.CopyTo(position, buffer.AsSpan(0, count));
            if (!raw.Slice((int)(offset + (ulong)position), count).SequenceEqual(buffer.AsSpan(0, count)))
                throw new InvalidDataException("模型补丁回读载荷不一致。");
            position += count;
        }
    }

    private static FileStream OpenRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);

    private static void ValidateMetadata(IasResource value)
    {
        var metadata = value.Metadata;
        if (metadata.Length != 4 || metadata.Any(item => item < 0))
            throw new InvalidDataException("模型数据检查：invalid archive entry metadata");
    }

    private static void VerifyPayload(ReadOnlySpan<byte> raw, ulong offset, uint size, ReadOnlySpan<byte> expected,
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

    private static void VerifySegment(FileStream raw, ulong offset, uint size, IasPayload expected,
        string name, (ulong Type, ulong Id) key)
    {
        if (size != (uint)expected.Length) throw new InvalidDataException("模型补丁回读载荷不一致。");
        if (size == 0) return;
        if (offset + size > (ulong)raw.Length)
            throw new InvalidDataException(
                $"模型补丁回读载荷越界（{name} {key.Type:x16}/{key.Id:x16}, offset {offset}, length {size}）。");
        raw.Seek((long)offset, SeekOrigin.Begin);
        var written = new byte[1 << 20];
        var source = new byte[1 << 20];
        long position = 0;
        while (position < size)
        {
            var count = (int)Math.Min(written.Length, size - position);
            raw.ReadExactly(written.AsSpan(0, count));
            expected.CopyTo(position, source.AsSpan(0, count));
            if (!written.AsSpan(0, count).SequenceEqual(source.AsSpan(0, count)))
                throw new InvalidDataException("模型补丁回读载荷不一致。");
            position += count;
        }
    }
}
