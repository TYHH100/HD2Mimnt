using System.Buffers.Binary;
using System.Text;

namespace HD2Mimnt.Infrastructure.ModelConversion;

/// <summary>CRC32、以零结尾的二进制名字与 Rig/物理文件共用的基础读取。</summary>
public static class IasBinary
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var index = 0; index < table.Length; index++)
        {
            var value = (uint)index;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320U ^ (value >> 1) : value >> 1;
            table[index] = value;
        }
        return table;
    }

    /// <summary>zlib <c>crc32</c>：初值与结果均按 IEEE 多项式异或。</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFU;
        foreach (var value in data) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFU;
    }

    public static string Name64(ReadOnlySpan<byte> raw) => Name(raw, 64);

    /// <summary>固定长度、零填充的二进制名字。上游分别使用 64/96 字节字段。</summary>
    public static string Name(ReadOnlySpan<byte> raw, int size)
    {
        if (raw.Length < size) throw new InvalidDataException("模型数据检查：invalid binary name");
        var separator = raw[..size].IndexOf((byte)0);
        if (separator <= 0) throw new InvalidDataException("模型数据检查：invalid binary name");
        for (var index = separator + 1; index < size; index++)
            if (raw[index] != 0) throw new InvalidDataException("模型数据检查：invalid binary name");
        return Encoding.UTF8.GetString(raw[..separator]);
    }

    public static byte[] FixedName(string name, int size)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length >= size) throw new InvalidDataException("模型数据检查：binary name too long");
        var result = new byte[size];
        bytes.CopyTo(result, 0);
        return result;
    }

    /// <summary>上游结构中的 12f/8f/4f 都是 32 位浮点，读入后按双精度参与仿射运算。</summary>
    public static double[] ReadFloats(ReadOnlySpan<byte> raw, int offset, int count)
    {
        var result = new double[count];
        for (var index = 0; index < count; index++) result[index] = BinaryPrimitives.ReadSingleLittleEndian(raw[(offset + index * 4)..]);
        return result;
    }

    public static uint ReadUInt32(ReadOnlySpan<byte> raw, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(raw[offset..]);
    public static ulong ReadUInt64(ReadOnlySpan<byte> raw, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(raw[offset..]);
    public static int ReadInt32(ReadOnlySpan<byte> raw, int offset) => BinaryPrimitives.ReadInt32LittleEndian(raw[offset..]);
    public static float ReadSingle(ReadOnlySpan<byte> raw, int offset) => BinaryPrimitives.ReadSingleLittleEndian(raw[offset..]);

    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException("模型数据检查：" + message);
    }
}
