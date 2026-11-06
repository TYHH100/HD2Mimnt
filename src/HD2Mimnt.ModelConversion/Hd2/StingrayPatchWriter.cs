using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace HD2Mimnt.ModelConversion.Hd2;

/// <summary>Stingray 补丁里的资源类型 ID（与 AQSDK <c>utils/constants.py</c> 一致）。</summary>
public static class StingrayTypeIds
{
    public const ulong Unit = 0xE0A48D0BE9A7453F;
    public const ulong Material = 0xEAC0B497876ADEDF;
    public const ulong Texture = 0xCD4238C6A0C69E32;
    public const ulong Bones = 0x18DEAD01056B72E9;
}

/// <summary>
/// 一份打包进补丁的资源：<c>Main</c> 写入 <c>.patch_0</c>，<c>Gpu</c> 写入
/// <c>.patch_0.gpu_resources</c>，<c>Stream</c> 写入 <c>.patch_0.stream</c>。
/// </summary>
public sealed record StingrayResource(ulong FileId, ulong TypeId, ReadOnlyMemory<byte> Main,
    ReadOnlyMemory<byte> Gpu, ReadOnlyMemory<byte> Stream)
{
    public StingrayResource(ulong fileId, ulong typeId, ReadOnlyMemory<byte> main)
        : this(fileId, typeId, main, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty) { }
}

/// <summary>写入后的条目索引，用于回读校验与报告。</summary>
public sealed record StingrayPatchEntry(ulong FileId, ulong TypeId, int Index, long MainOffset, uint MainSize,
    long GpuOffset, uint GpuSize, long StreamOffset, uint StreamSize);

public sealed record StingrayPatchBundle(byte[] Patch, byte[] Gpu, byte[] Stream,
    IReadOnlyList<StingrayPatchEntry> Entries)
{
    public long TotalGpuBytes => Gpu.LongLength;
    public long TotalStreamBytes => Stream.LongLength;
}

/// <summary>
/// 多资源 Stingray 补丁写入器。布局：72 字节头 + 32 字节类型表 + 80 字节条目表 +
/// 按条目顺序首尾相接的主数据；GPU 与 stream 载荷写在同名的 <c>.gpu_resources</c> /
/// <c>.stream</c> 伴随文件里，每个资源块按 64 字节对齐。
///
/// 多个条目可以共享同一段载荷：调用方把同一个缓冲区实例传给多个资源时，条目会指向同一偏移，
/// 这是 IAS 常见形态（同一几何按不同开关发布成多个 Unit）避免重复存储的方式。
/// </summary>
public static class StingrayPatchWriter
{
    public const uint Magic = 0xF0000011;
    public const int HeaderSize = 72;
    public const int TypeEntrySize = 32;
    public const int FileEntrySize = 80;
    public const int GpuAlignment = 64;

    /// <summary>游戏基础补丁名；护甲/材质/贴图模组都写这个名字的 <c>.patch_0</c>。</summary>
    public const string DefaultPatchName = "9ba626afa44a3aa3.patch_0";

    public static StingrayPatchBundle Build(IReadOnlyList<StingrayResource> resources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (resources.Count == 0) throw new Hd2FormatException("A patch must contain at least one resource.");

        // 同一 (FileID, TypeID) 只能出现一次：游戏按该键查找资源，重复会让加载结果不确定。
        var seen = new HashSet<(ulong, ulong)>();
        foreach (var resource in resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (resource.FileId == 0) throw new Hd2FormatException("A resource FileID must be non-zero.");
            if (resource.Main.IsEmpty) throw new Hd2FormatException("Every resource needs main data.");
            if (!seen.Add((resource.FileId, resource.TypeId)))
                throw new Hd2FormatException($"Duplicate resource 0x{resource.FileId:x16}/0x{resource.TypeId:x16}.");
        }

        // 类型表按首次出现顺序分组，条目表按类型分组连续编号（AQSDK StreamToc 的写顺序）。
        var types = new List<ulong>();
        var byType = new Dictionary<ulong, List<StingrayResource>>();
        foreach (var resource in resources)
        {
            if (!byType.TryGetValue(resource.TypeId, out var list))
            {
                byType[resource.TypeId] = list = [];
                types.Add(resource.TypeId);
            }
            list.Add(resource);
        }

        var ordered = types.SelectMany(type => byType[type]).ToArray();
        var mainStart = HeaderSize + TypeEntrySize * types.Count + FileEntrySize * ordered.Length;
        var entries = new StingrayPatchEntry[ordered.Length];
        var patch = new List<byte>(mainStart + ordered.Sum(resource => resource.Main.Length));
        var mainOffsets = new Dictionary<ReadOnlyMemory<byte>, long>(MemoryIdentity.Instance);
        var gpu = new PayloadWriter(GpuAlignment);
        var stream = new PayloadWriter(GpuAlignment);

        for (var index = 0; index < ordered.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resource = ordered[index];
            // 同一个缓冲区实例被多个资源复用时只落一份数据，条目共享偏移。
            if (!mainOffsets.TryGetValue(resource.Main, out var mainOffset))
            {
                mainOffset = mainStart + patch.Count;
                patch.AddRange(resource.Main.Span);
                mainOffsets[resource.Main] = mainOffset;
            }
            var gpuOffset = gpu.Append(resource.Gpu);
            var streamOffset = stream.Append(resource.Stream);
            entries[index] = new StingrayPatchEntry(resource.FileId, resource.TypeId, index + 1, mainOffset,
                (uint)resource.Main.Length, gpuOffset, (uint)resource.Gpu.Length, streamOffset,
                (uint)resource.Stream.Length);
        }

        var output = new byte[mainStart + patch.Count];
        var typesStart = HeaderSize;
        var filesStart = HeaderSize + TypeEntrySize * types.Count;
        BinaryPrimitives.WriteUInt32LittleEndian(output, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), (uint)types.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), (uint)ordered.Length);

        for (var index = 0; index < types.Count; index++)
        {
            var offset = typesStart + index * TypeEntrySize;
            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset + 8), types[index]);
            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset + 16), (ulong)byType[types[index]].Count);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset + 24), 16);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset + 28), 64);
        }

        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var offset = filesStart + index * FileEntrySize;
            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset), entry.FileId);
            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset + 8), entry.TypeId);
            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset + 16), (ulong)entry.MainOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset + 24), (ulong)entry.StreamOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(offset + 32), (ulong)entry.GpuOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset + 56), entry.MainSize);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset + 60), entry.StreamSize);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset + 64), entry.GpuSize);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset + 68), 16);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset + 72), 64);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset + 76), (uint)entry.Index);
        }

        patch.CopyTo(output, mainStart);
        return new StingrayPatchBundle(output, gpu.ToArray(), stream.ToArray(), entries);
    }

    /// <summary>可共享载荷的顺序写入器：同一个缓冲区实例只写一次，重复资源指向同一偏移。</summary>
    private sealed class PayloadWriter(int alignment)
    {
        private readonly List<byte> _bytes = [];
        private readonly Dictionary<ReadOnlyMemory<byte>, long> _offsets = new(MemoryIdentity.Instance);

        public long Append(ReadOnlyMemory<byte> payload)
        {
            if (payload.IsEmpty) return 0;
            if (_offsets.TryGetValue(payload, out var reused)) return reused;
            var remainder = _bytes.Count % alignment;
            if (remainder != 0) _bytes.AddRange(new byte[alignment - remainder]);
            var offset = _bytes.Count;
            _bytes.AddRange(payload.Span);
            _offsets[payload] = offset;
            return offset;
        }

        public byte[] ToArray() => _bytes.ToArray();
    }

    /// <summary>按「同一个后备数组 + 相同起点与长度」判断载荷是否同一份，避免对大模型做逐字节比较。</summary>
    private sealed class MemoryIdentity : IEqualityComparer<ReadOnlyMemory<byte>>
    {
        public static readonly MemoryIdentity Instance = new();

        public bool Equals(ReadOnlyMemory<byte> left, ReadOnlyMemory<byte> right)
        {
            if (left.Length != right.Length) return false;
            if (MemoryMarshal.TryGetArray(left, out var a) && MemoryMarshal.TryGetArray(right, out var b))
                return ReferenceEquals(a.Array, b.Array) && a.Offset == b.Offset;
            return left.Span.SequenceEqual(right.Span);
        }

        public int GetHashCode(ReadOnlyMemory<byte> value) => MemoryMarshal.TryGetArray(value, out var segment)
            ? HashCode.Combine(segment.Array, segment.Offset, value.Length)
            : value.Length;
    }
}

/// <summary>HD2 二进制写入失败的统一异常类型（模型转换层不依赖 Infrastructure）。</summary>
public sealed class Hd2FormatException(string message) : Exception(message);
