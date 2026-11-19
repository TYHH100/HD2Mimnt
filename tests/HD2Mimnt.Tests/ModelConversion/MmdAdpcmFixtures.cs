using System.Buffers.Binary;
using System.Text;
using HD2Mimnt.Infrastructure.ModelConversion;
using HD2Mimnt.Infrastructure.ModelConversion.Mmd;

namespace HD2Mimnt.Tests.ModelConversion;

/// <summary>
/// 合成的 MS ADPCM 单块样例：2 声道、32 kHz、4 样本/块，头部两个样本为 0，
/// 数据字节 <c>0x12 0xF0</c> 依次解码为 L=16 R=32 与 L=0 R=32。
/// </summary>
internal static class MmdAdpcmFixtures
{
    public const int Rate = 32_000;
    public const int BlockAlign = 16;

    /// <summary>WAVEFORMATEX：MS ADPCM、2 声道、32 kHz、4 样本/块、标准 7 组预测系数。</summary>
    public static byte[] Format()
    {
        var format = new byte[50];
        BinaryPrimitives.WriteUInt16LittleEndian(format, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(4), Rate);
        BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(8), (uint)(Rate * BlockAlign / 4));
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(12), BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(14), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(16), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(18), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(20), 7);
        int[] coefficient1 = [256, 512, 0, 192, 240, 460, 392];
        int[] coefficient2 = [0, -256, 0, 64, 0, -208, -232];
        for (var index = 0; index < 7; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(format.AsSpan(22 + 4 * index), (short)coefficient1[index]);
            BinaryPrimitives.WriteInt16LittleEndian(format.AsSpan(24 + 4 * index), (short)coefficient2[index]);
        }
        return format;
    }

    public static byte[] Data() =>
    [
        0, 0, 16, 0, 16, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0x12, 0xF0,
    ];

    public static MmdMdl3Audio Audio() => new(Data(), Format(), Rate, 2);

    /// <summary>生成可直接嵌入 MDL3 音轨 Lua 的 format 字段。</summary>
    public static string FormatBase64() => Convert.ToBase64String(Format());

    public static string DataBase64() => Convert.ToBase64String(Data());

    public static string Sha256() => IasHash.Sha256Hex(Data());

    public static byte[] Envelope(string code)
    {
        var raw = Encoding.UTF8.GetBytes(code);
        var result = new byte[8 + raw.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)raw.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 2);
        raw.CopyTo(result, 8);
        return result;
    }
}
