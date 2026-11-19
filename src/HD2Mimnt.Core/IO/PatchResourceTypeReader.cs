using System.Buffers.Binary;

namespace HD2Mimnt.Core.IO;

/// <summary>
/// 只读补丁文件的头部与资源索引，回答“这个补丁包含哪些资源类型”。
/// 只读取类型表之前的文件条目表，不触碰补丁正文；任一结构异常都返回 false，
/// 让调用方按“无法确认”处理，而不是猜测。
/// 布局与 <c>PatchResourceIndexReader</c> 一致：72 字节头（magic/类型数/文件数），
/// 随后 typeCount × 32 字节类型表，再 fileCount × 80 字节文件条目表，类型 id 在条目偏移 8。
/// </summary>
public static class PatchResourceTypeReader
{
    private const int HeaderSize = 72;
    private const uint PatchHeaderMagic = 0xF0000011;
    private const int TypeEntrySize = 32;
    private const int FileEntrySize = 80;
    private const int MaxTypes = 1_000;
    private const int MaxFiles = 100_000;

    /// <summary>
    /// 读取补丁内所有资源的类型 id。文件不存在、头部非法、计数越界或索引超出文件长度时返回 false。
    /// 一个没有任何文件的补丁同样返回 false：它无法说明自己没有 gameplay 内容。
    /// </summary>
    public static bool TryReadTypeIds(string patchPath, out IReadOnlySet<ulong> typeIds)
    {
        typeIds = new HashSet<ulong>();
        try
        {
            using var stream = new FileStream(patchPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < HeaderSize) return false;

            Span<byte> header = stackalloc byte[HeaderSize];
            stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != PatchHeaderMagic) return false;

            var typeCount = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            var fileCount = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            if (fileCount == 0 || fileCount > MaxFiles || typeCount > MaxTypes) return false;

            // 类型表必须整体落在文件内，否则后面读到的“文件条目”会错位成正文数据。
            var fileEntriesOffset = HeaderSize + (long)typeCount * TypeEntrySize;
            var tableEnd = fileEntriesOffset + (long)fileCount * FileEntrySize;
            if (fileEntriesOffset > stream.Length || tableEnd > stream.Length) return false;

            var table = new byte[checked((int)fileCount * FileEntrySize)];
            stream.Seek(fileEntriesOffset, SeekOrigin.Begin);
            stream.ReadExactly(table);

            var types = new HashSet<ulong>();
            for (var index = 0; index < table.Length; index += FileEntrySize)
                types.Add(BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(index + 8)));
            typeIds = types;
            return types.Count > 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
