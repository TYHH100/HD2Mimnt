using System.Buffers.Binary;

namespace HD2Mimnt.Infrastructure.Hd2IAS;

/// <summary>单一源码 Lua 资源的 Stingray Archive，布局与用户提供的 1.4.0 Lua 样本一致。</summary>
public static class StingrayArchiveWriter
{
    public const string PatchName = "9ba626afa44a3aa3.patch_0";
    private const uint Magic = 0xf0000011;
    private const ulong LuaType = 0xa14e8dfa2cd117e2;
    private const int DataOffset = 184;

    public static byte[] Build(byte[] source, ulong resourceId)
    {
        var data = new byte[checked(DataOffset + 8 + source.Length)];
        Write32(0, Magic); Write32(4, 1); Write32(8, 1);
        Write64(80, LuaType); Write32(88, 1);
        Write64(104, resourceId); Write64(112, LuaType); Write64(120, DataOffset);
        Write32(160, checked((uint)source.Length + 8)); Write32(176, 64); Write32(180, 1);
        Write32(DataOffset, checked((uint)source.Length)); Write32(DataOffset + 4, 2);
        source.CopyTo(data, DataOffset + 8);
        return data;

        void Write32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
        void Write64(int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), value);
    }

    public static byte[] ReadSource(byte[] archive, ulong resourceId)
    {
        if (archive.Length < DataOffset + 8) throw new InvalidDataException("Truncated HD2IAS archive.");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(DataOffset));
        if (length != archive.Length - DataOffset - 8) throw new InvalidDataException("Invalid HD2IAS Lua envelope size.");
        var source = archive.AsSpan(DataOffset + 8).ToArray();
        // 重建后逐字节比对，覆盖资源 ID、类型、TOC 偏移/大小、版本及空 stream/GPU 字段。
        if (!Build(source, resourceId).AsSpan().SequenceEqual(archive))
            throw new InvalidDataException("Invalid HD2IAS archive layout or resource identity.");
        return source;
    }
}
