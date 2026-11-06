using System.Buffers.Binary;

namespace HD2Mimnt.ModelConversion.Hd2;

/// <summary>
/// 写入后的自检器：用与 <c>PatchResourceInspectionService</c> / <c>ModCompatibilityAnalyzer</c>
/// 相同的约束复核 Unit，确保产物能被本程序和自己写出的补丁索引正确读取。
/// 它不替代游戏内验收，只把「写错就静默不可见」的失败提前变成明确错误。
/// </summary>
public static class Hd2UnitInspector
{
    private static readonly HashSet<uint> KnownVersions = [1, 10800437, 10800438];

    public static IReadOnlyList<string> Validate(ReadOnlySpan<byte> main, ReadOnlySpan<byte> gpu)
    {
        var issues = new List<string>();
        if (main.Length < 0x80) { issues.Add("Unit header is shorter than 0x80 bytes."); return issues; }

        var version = U32(main, 0x2C);
        if (!KnownVersions.Contains(version)) issues.Add($"Unrecognized Unit version {version}.");
        var ending = U32(main, 0x60);
        if ((long)ending + 8 != main.Length)
            issues.Add($"Ending offset {ending} does not match the {main.Length}-byte resource size.");

        var transformOffset = U32(main, 0x34);
        var boneInfoOffset = U32(main, 0x58);
        var streamInfoOffset = U32(main, 0x5C);
        var meshInfoOffset = U32(main, 0x64);
        var materialsOffset = U32(main, 0x70);
        foreach (var (name, offset) in new[]
                 {
                     ("TransformInfo", transformOffset), ("BoneInfo", boneInfoOffset),
                     ("StreamInfo", streamInfoOffset), ("MeshInfo", meshInfoOffset),
                     ("Materials", materialsOffset)
                 })
            if (offset < 0x68 || offset + 4 > main.Length)
                issues.Add($"{name} offset {offset} is outside the resource.");
        if (issues.Count > 0) return issues;

        var transformCount = ValidateTransforms(main, transformOffset, issues);
        ValidateBoneInfo(main, boneInfoOffset, transformCount, issues);
        var streams = ValidateStreamInfo(main, streamInfoOffset, gpu, issues);
        ValidateMeshInfo(main, meshInfoOffset, materialsOffset, streams, issues);
        ValidateMaterials(main, materialsOffset, issues);
        return issues;
    }

    private static int ValidateTransforms(ReadOnlySpan<byte> main, uint offset, List<string> issues)
    {
        var count = (int)U32(main, (int)offset);
        if (count == 0 || count > Hd2UnitBuilder.MaxTransforms)
        {
            issues.Add($"TransformInfo count {count} is invalid.");
            return 0;
        }
        var size = 16L + 136L * count;
        if (offset + size > main.Length) { issues.Add("TransformInfo table is outside the resource."); return 0; }
        for (var i = 0; i < count; i++)
        {
            var matrixOffset = (int)offset + 16 + 64 * count + i * 64;
            for (var value = 0; value < 16; value++)
                if (!float.IsFinite(BitConverter.Int32BitsToSingle((int)U32(main, matrixOffset + value * 4))))
                {
                    issues.Add($"Transform {i} has a non-finite world matrix.");
                    return count;
                }
            var parentOffset = (int)offset + 16 + 128 * count + i * 4 + 2;
            var parent = BinaryPrimitives.ReadUInt16LittleEndian(main[parentOffset..]);
            if (parent != 0xFFFF && parent >= count) issues.Add($"Transform {i} has an invalid parent {parent}.");
        }
        return count;
    }

    private static void ValidateBoneInfo(ReadOnlySpan<byte> main, uint offset, int transformCount, List<string> issues)
    {
        var count = U32(main, (int)offset);
        if (count == 0 || count > 64) { issues.Add($"BoneInfo table count {count} is invalid."); return; }
        if ((long)offset + 4 + 4 * count > main.Length) { issues.Add("BoneInfo offset table is outside the resource."); return; }
        for (var record = 0; record < count; record++)
        {
            var recordOffset = (long)offset + U32(main, (int)offset + 4 + record * 4);
            if (recordOffset + 16 > main.Length) { issues.Add($"BoneInfo record {record} is outside the resource."); continue; }
            var boneCount = U32(main, (int)recordOffset);
            var matrices = U32(main, (int)recordOffset + 4);
            var realIndices = U32(main, (int)recordOffset + 8);
            var remaps = U32(main, (int)recordOffset + 12);
            if (boneCount == 0 || (transformCount > 0 && boneCount > transformCount))
            {
                issues.Add($"BoneInfo record {record} bone count {boneCount} is invalid.");
                continue;
            }
            if (matrices < 16 || realIndices < 16 || remaps < 16 ||
                recordOffset + remaps + 4 > main.Length)
            {
                issues.Add($"BoneInfo record {record} has invalid sub-offsets.");
                continue;
            }
            for (var i = 0; i < boneCount; i++)
            {
                var real = U32(main, (int)(recordOffset + realIndices + i * 4));
                if (transformCount > 0 && real >= transformCount)
                    issues.Add($"BoneInfo record {record} maps to transform {real}, outside the scene graph.");
            }
            var remapCount = U32(main, (int)(recordOffset + remaps));
            if (remapCount == 0 || remapCount > 4096)
            {
                issues.Add($"BoneInfo record {record} remap count {remapCount} is invalid.");
                continue;
            }
            var entriesOffset = recordOffset + remaps + 4;
            if (entriesOffset + 8L * remapCount > main.Length)
            {
                issues.Add($"BoneInfo record {record} remap table is outside the resource.");
                continue;
            }
            for (var remap = 0; remap < remapCount; remap++)
            {
                var arrayOffset = U32(main, (int)(entriesOffset + remap * 8));
                var paletteCount = U32(main, (int)(entriesOffset + remap * 8 + 4));
                if (paletteCount == 0 || paletteCount > Hd2UnitBuilder.MaxPaletteSize)
                {
                    issues.Add($"BoneInfo record {record} palette {remap} size {paletteCount} is invalid.");
                    continue;
                }
                if (recordOffset + remaps + arrayOffset + 4L * paletteCount > main.Length)
                {
                    issues.Add($"BoneInfo record {record} palette {remap} is outside the resource.");
                    continue;
                }
                for (var slot = 0; slot < paletteCount; slot++)
                {
                    var index = U32(main, (int)(recordOffset + remaps + arrayOffset + slot * 4));
                    if (index >= boneCount)
                        issues.Add($"BoneInfo record {record} palette {remap} slot {slot} exceeds the bone count.");
                }
            }
        }
    }

    private sealed record StreamState(int VertexCount, int IndexCount, int IndexElementSize);

    private static IReadOnlyList<StreamState> ValidateStreamInfo(ReadOnlySpan<byte> main, uint offset,
        ReadOnlySpan<byte> gpu, List<string> issues)
    {
        var count = U32(main, (int)offset);
        var states = new List<StreamState>();
        if (count == 0 || count > 100)
        {
            issues.Add($"StreamInfo count {count} is invalid.");
            return states;
        }
        if ((long)offset + 4 + 8 * count + 4 > main.Length)
        {
            issues.Add("StreamInfo table is outside the resource.");
            return states;
        }
        for (var stream = 0; stream < count; stream++)
        {
            var recordOffset = (long)offset + U32(main, (int)offset + 4 + stream * 4);
            if (recordOffset + Hd2UnitBuilder.StreamInfoSize > main.Length)
            {
                issues.Add($"StreamInfo record {stream} is outside the resource.");
                states.Add(new StreamState(0, 0, 2));
                continue;
            }
            var componentCount = BinaryPrimitives.ReadUInt64LittleEndian(main[(int)(recordOffset + 0x148)..]);
            if (componentCount == 0 || componentCount > 16)
            {
                issues.Add($"StreamInfo record {stream} component count {componentCount} is invalid.");
                states.Add(new StreamState(0, 0, 2));
                continue;
            }
            var strideSum = 0;
            var hasPosition = false;
            for (var component = 0; component < (int)componentCount; component++)
            {
                var componentOffset = (int)recordOffset + 8 + component * 20;
                var type = U32(main, componentOffset);
                var format = U32(main, componentOffset + 4);
                if (!TryGetFormatSize(format, out var size))
                {
                    issues.Add($"StreamInfo record {stream} uses unknown vertex format {format}.");
                    continue;
                }
                if (type == (uint)Hd2VertexComponent.Position && format == (uint)Hd2VertexFormat.Float3)
                    hasPosition = true;
                strideSum += size;
            }
            var vertexCount = (int)U32(main, (int)(recordOffset + 0x160));
            var stride = (int)U32(main, (int)(recordOffset + 0x164));
            var indexCount = (int)U32(main, (int)(recordOffset + 0x188));
            var indexType = U32(main, (int)(recordOffset + 0x18C));
            var vertexOffset = (int)U32(main, (int)(recordOffset + 0x1A0));
            var vertexSize = (int)U32(main, (int)(recordOffset + 0x1A4));
            var indexOffset = (int)U32(main, (int)(recordOffset + 0x1A8));
            var indexSize = (int)U32(main, (int)(recordOffset + 0x1AC));
            if (!hasPosition) issues.Add($"StreamInfo record {stream} has no float3 position component.");
            if (stride != strideSum) issues.Add($"StreamInfo record {stream} stride {stride} != component bytes {strideSum}.");
            if (indexType > 1) issues.Add($"StreamInfo record {stream} index type {indexType} is invalid.");
            var elementSize = indexType == 0 ? 2 : 4;
            var vertexBytes = (long)vertexCount * stride;
            if (vertexBytes > vertexSize || vertexSize - vertexBytes > 15)
                issues.Add($"StreamInfo record {stream} vertex buffer size {vertexSize} does not match {vertexBytes}.");
            if ((long)indexCount * elementSize != indexSize)
                issues.Add($"StreamInfo record {stream} index buffer size {indexSize} does not match {indexCount} indices.");
            if ((long)vertexOffset + vertexSize > gpu.Length)
                issues.Add($"StreamInfo record {stream} vertex buffer exceeds the GPU payload.");
            if ((long)indexOffset + indexSize > gpu.Length)
                issues.Add($"StreamInfo record {stream} index buffer exceeds the GPU payload.");
            states.Add(new StreamState(vertexCount, indexCount, elementSize));
        }
        return states;
    }

    private static void ValidateMeshInfo(ReadOnlySpan<byte> main, uint meshInfoOffset, uint materialsOffset,
        IReadOnlyList<StreamState> streams, List<string> issues)
    {
        var count = U32(main, (int)meshInfoOffset);
        if (count == 0 || count > 4096) { issues.Add($"MeshInfo count {count} is invalid."); return; }
        if ((long)meshInfoOffset + 4 + 8 * count > main.Length)
        {
            issues.Add("MeshInfo table is outside the resource.");
            return;
        }
        var materialCount = U32(main, (int)materialsOffset);
        if (materialCount == 0 || materialCount > 4096 ||
            (long)materialsOffset + 4 + 12L * materialCount > main.Length)
        {
            issues.Add($"Material table count {materialCount} is invalid.");
            return;
        }
        var slots = new HashSet<uint>();
        for (var i = 0; i < materialCount; i++) slots.Add(U32(main, (int)materialsOffset + 4 + i * 4));

        for (var mesh = 0; mesh < count; mesh++)
        {
            var recordOffset = (long)meshInfoOffset + U32(main, (int)meshInfoOffset + 4 + mesh * 4);
            if (recordOffset + Hd2UnitBuilder.MeshInfoHeaderSize > main.Length)
            {
                issues.Add($"MeshInfo record {mesh} is outside the resource.");
                continue;
            }
            var streamIndex = (int)U32(main, (int)(recordOffset + 0x3C));
            var meshMaterialCount = (int)U32(main, (int)(recordOffset + 0x68));
            var meshMaterialOffset = (int)U32(main, (int)(recordOffset + 0x6C));
            var sectionCount = (int)U32(main, (int)(recordOffset + 0x78));
            var sectionsOffset = (int)U32(main, (int)(recordOffset + 0x7C));
            if (meshMaterialCount == 0 || meshMaterialCount > 4096 ||
                sectionCount == 0 || sectionCount > 4096 ||
                meshMaterialOffset < Hd2UnitBuilder.MeshInfoHeaderSize || sectionsOffset < Hd2UnitBuilder.MeshInfoHeaderSize)
            {
                issues.Add($"MeshInfo record {mesh} has invalid material/section counts or offsets.");
                continue;
            }
            if (streamIndex < 0 || streamIndex >= streams.Count)
            {
                issues.Add($"MeshInfo record {mesh} references stream {streamIndex}.");
                continue;
            }
            for (var i = 0; i < meshMaterialCount; i++)
            {
                var offset = recordOffset + meshMaterialOffset + i * 4;
                if (offset + 4 > main.Length) { issues.Add($"MeshInfo record {mesh} material array is truncated."); break; }
                if (!slots.Contains(U32(main, (int)offset)))
                    issues.Add($"MeshInfo record {mesh} uses material slot 0x{U32(main, (int)offset):x8} that is missing from the material table.");
            }
            var stream = streams[streamIndex];
            for (var section = 0; section < sectionCount; section++)
            {
                var sectionOffset = recordOffset + sectionsOffset + section * Hd2UnitBuilder.SectionSize;
                if (sectionOffset + Hd2UnitBuilder.SectionSize > main.Length)
                {
                    issues.Add($"MeshInfo record {mesh} section {section} is outside the resource.");
                    continue;
                }
                var materialIndex = (int)U32(main, (int)sectionOffset);
                var vertexOffset = (int)U32(main, (int)sectionOffset + 4);
                var vertexCount = (int)U32(main, (int)sectionOffset + 8);
                var indexOffset = (int)U32(main, (int)sectionOffset + 12);
                var indexCount = (int)U32(main, (int)sectionOffset + 16);
                if (materialIndex >= meshMaterialCount)
                    issues.Add($"MeshInfo record {mesh} section {section} material index {materialIndex} is invalid.");
                if (indexCount < 3 || indexCount % 3 != 0)
                    issues.Add($"MeshInfo record {mesh} section {section} index count {indexCount} is not whole triangles.");
                if ((long)vertexOffset + vertexCount > stream.VertexCount)
                    issues.Add($"MeshInfo record {mesh} section {section} vertices exceed the stream.");
                if ((long)indexOffset + indexCount > stream.IndexCount)
                    issues.Add($"MeshInfo record {mesh} section {section} indices exceed the stream.");
            }
        }
    }

    private static void ValidateMaterials(ReadOnlySpan<byte> main, uint materialsOffset, List<string> issues)
    {
        var count = U32(main, (int)materialsOffset);
        if (count == 0 || count > 4096) return;
        var idsOffset = (long)materialsOffset + 4 + 4L * count;
        if (idsOffset + 8L * count > main.Length) issues.Add("Material ID table is outside the resource.");
    }

    private static bool TryGetFormatSize(uint format, out int size)
    {
        size = format switch
        {
            (uint)Hd2VertexFormat.Float => 4,
            (uint)Hd2VertexFormat.Float2 => 8,
            (uint)Hd2VertexFormat.Float3 => 12,
            (uint)Hd2VertexFormat.Float4 => 16,
            (uint)Hd2VertexFormat.Rgba8 => 4,
            (uint)Hd2VertexFormat.Uint8x4 => 4,
            (uint)Hd2VertexFormat.OctNormal => 4,
            (uint)Hd2VertexFormat.Half2 => 4,
            (uint)Hd2VertexFormat.Half4 => 8,
            _ => 0
        };
        return size != 0;
    }

    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
}
