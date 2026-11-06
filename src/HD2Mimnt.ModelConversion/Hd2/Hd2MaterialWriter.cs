using System.Buffers.Binary;

namespace HD2Mimnt.ModelConversion.Hd2;

/// <summary>HD2 材质模板族：决定着色器族（父材质 ID）、贴图槽语义与默认参数。</summary>
public enum Hd2MaterialFamily
{
    /// <summary>不透明受光护甲材质：Mra + Albedo + Normal。</summary>
    Basic,
    /// <summary>带裁切的护甲材质：Nar + OpacityClipMap + BaseColorMetalMap（颜色贴图的 A 承载金属度）。</summary>
    AlphaClip
}

/// <summary>材质的一个贴图输入端：语义哈希 + 贴图资源 ID（0 表示该槽留空）。</summary>
public readonly record struct Hd2MaterialInput(uint Semantic, ulong TextureId);

public sealed record Hd2MaterialResult(byte[] Main, Hd2MaterialFamily Family, int TextureCount);

/// <summary>
/// 写游戏原生 Stingray 材质资源。
///
/// 模板结构（父材质 + 贴图槽语义 + 着色器默认参数）取自 AQSDK 的
/// <c>materials/basic.material</c> / <c>alphaclip.material</c>：这些常量是着色器族的
/// 参数指纹，不能由转换器猜测生成，否则游戏会落到默认材质。可变的只有贴图资源 ID。
/// </summary>
public static class Hd2MaterialWriter
{
    private sealed record Template(ulong ParentMaterialId, uint[] Semantics, ShaderVariable[] Variables,
        uint[] Values);

    private readonly record struct ShaderVariable(uint Id, uint Offset);

    private static readonly byte[] HeaderPrefix = [2, 1, 0, 0, 1, 0, 0, 0, 24, 0, 0, 0];

    private static readonly Template Basic = new(
        0xD51D2A873865D4E5,
        [0x756F6FA6, 0xAC652E43, 0xCAED6CD6],
        [
            new(0xA3351311, 52), new(0x43695F7B, 4), new(0x64AAB07B, 8), new(0x6FD0B9E7, 12),
            new(0x60E7D2A1, 56), new(0xE045BC57, 20), new(0xBD16A396, 24), new(0x1BF10D7F, 28),
            new(0xA83F44CD, 68), new(0x6DDBAE8F, 72), new(0x4A7CD0EF, 60), new(0x9ED04DA2, 64),
            new(0x43C7218A, 48)
        ],
        [
            0x3F800000u, 0x40400000u, 0x3F800000u, 0x3F800000u, 0x40400000u, 0x3F800000u, 0x3F800000u,
            0x3F000000u, 0x3F000000u, 0x3F800000u, 0x3F733333u, 0x3E800000u, 0x00000000u, 0x3E1CAC08u,
            0x3F800000u, 0x3F000000u, 0x3F800000u, 0x3F800000u, 0xC0A00000u
        ]);

    private static readonly Template AlphaClip = new(
        0xD84D04634A1E2F60,
        [0x1D57DCF3, 0xCBDE381B, 0x848BA63B],
        [
            new(0x529A4AAF, 0), new(0xA3351311, 4), new(0x43695F7B, 8), new(0x64AAB07B, 12),
            new(0x6FD0B9E7, 16), new(0x60E7D2A1, 20), new(0x4A7CD0EF, 24), new(0xBD16A396, 28),
            new(0xA83F44CD, 32), new(0x6DDBAE8F, 36), new(0x9ED04DA2, 40)
        ],
        [
            0x3F000000u, 0x3F800000u, 0x40400000u, 0x3F800000u, 0x00000000u, 0x3F800000u, 0x3F000000u,
            0x3F800000u, 0x3F800000u, 0x3F800000u, 0x3F800000u
        ]);

    public static Hd2MaterialResult Build(Hd2MaterialFamily family, IReadOnlyList<Hd2MaterialInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var template = family == Hd2MaterialFamily.AlphaClip ? AlphaClip : Basic;
        if (inputs.Count != template.Semantics.Length)
            throw new Hd2FormatException($"The {family} material template needs exactly {template.Semantics.Length} texture slots.");

        var textureCount = inputs.Count;
        var variableCount = template.Variables.Length;
        var valueBytes = template.Values.Length * 4;
        var blockStart = 0x88 + 12 * textureCount + 20 * variableCount;
        var size = blockStart + valueBytes;
        var endOffset = size - 24;
        var padded = (size + 15) & ~15;
        var main = new byte[padded];

        HeaderPrefix.CopyTo(main, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(main.AsSpan(12), (uint)endOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(main.AsSpan(24), template.ParentMaterialId);
        BinaryPrimitives.WriteUInt32LittleEndian(main.AsSpan(0x40), (uint)textureCount);
        BinaryPrimitives.WriteUInt32LittleEndian(main.AsSpan(0x68), (uint)variableCount);
        BinaryPrimitives.WriteUInt32LittleEndian(main.AsSpan(0x78), (uint)valueBytes);

        var semantics = 0x88;
        var ids = semantics + 4 * textureCount;
        for (var i = 0; i < textureCount; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(main.AsSpan(semantics + i * 4), template.Semantics[i]);
            BinaryPrimitives.WriteUInt64LittleEndian(main.AsSpan(ids + i * 8), inputs[i].TextureId);
        }

        var descriptors = ids + 8 * textureCount;
        for (var i = 0; i < variableCount; i++)
        {
            var offset = descriptors + i * 20;
            BinaryPrimitives.WriteUInt32LittleEndian(main.AsSpan(offset), 0);          // klass = Scalar
            BinaryPrimitives.WriteUInt32LittleEndian(main.AsSpan(offset + 4), 0);      // elements
            BinaryPrimitives.WriteUInt32LittleEndian(main.AsSpan(offset + 8), template.Variables[i].Id);
            BinaryPrimitives.WriteUInt32LittleEndian(main.AsSpan(offset + 12), template.Variables[i].Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(main.AsSpan(offset + 16), 0);     // elementStride
        }
        for (var i = 0; i < template.Values.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(main.AsSpan(blockStart + i * 4), template.Values[i]);

        var issues = Hd2MaterialInspector.Validate(main);
        if (issues.Count > 0)
            throw new Hd2FormatException("Generated material failed self-validation: " + string.Join(" | ", issues));
        return new Hd2MaterialResult(main, family, textureCount);
    }

    /// <summary>模板里的贴图槽语义，供调用方按角色填贴图 ID。</summary>
    public static IReadOnlyList<uint> Semantics(Hd2MaterialFamily family) =>
        (family == Hd2MaterialFamily.AlphaClip ? AlphaClip : Basic).Semantics;
}

/// <summary>材质结构自检：长度、贴图表与变量表必须自洽。</summary>
public static class Hd2MaterialInspector
{
    public static IReadOnlyList<string> Validate(ReadOnlySpan<byte> main)
    {
        var issues = new List<string>();
        if (main.Length < 0x88 + 12)
        {
            issues.Add("Material header is too short.");
            return issues;
        }
        var textureCount = BinaryPrimitives.ReadUInt32LittleEndian(main[0x40..]);
        var variableCount = BinaryPrimitives.ReadUInt32LittleEndian(main[0x68..]);
        var valueBytes = BinaryPrimitives.ReadUInt32LittleEndian(main[0x78..]);
        var endOffset = BinaryPrimitives.ReadUInt32LittleEndian(main[12..]);
        if (textureCount == 0 || textureCount > 4096) issues.Add($"Material texture count {textureCount} is invalid.");
        if (variableCount > 4096) issues.Add($"Material variable count {variableCount} is invalid.");
        var block = 0x88L + 12L * textureCount + 20L * variableCount;
        if (block + valueBytes > main.Length) issues.Add("Material variable data is outside the resource.");
        if (endOffset != block + valueBytes - 24) issues.Add($"Material EndOffset {endOffset} does not match its layout.");
        return issues;
    }
}
