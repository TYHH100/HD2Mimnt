using System.Buffers.Binary;
using System.Numerics;

namespace HD2Mimnt.ModelConversion.Hd2;

/// <summary>顶点分量语义（<c>StreamComponentType</c>）。</summary>
public enum Hd2VertexComponent : uint
{
    Position = 0,
    Normal = 1,
    Tangent = 2,
    Bitangent = 3,
    Uv = 4,
    Color = 5,
    BoneIndex = 6,
    BoneWeight = 7
}

/// <summary>顶点分量格式（<c>StreamComponentFormat</c>，Unit 版本 10800438 的取值）。</summary>
public enum Hd2VertexFormat : uint
{
    Float = 0,
    Float2 = 1,
    Float3 = 2,
    Float4 = 3,
    Rgba8 = 4,
    Uint8x4 = 28,
    OctNormal = 30,
    Half2 = 33,
    Half4 = 35
}

/// <summary>Unit 场景节点：名称、父节点下标与 rest 局部变换（行向量约定）。</summary>
public sealed record Hd2UnitTransform(string Name, int Parent, Matrix4x4 RestLocal);

/// <summary>一个 Unit 材质槽：32 位槽名（材质 ID 低 32 位）与材质资源 ID。</summary>
public sealed record Hd2UnitMaterial(uint Slot, ulong MaterialId);

/// <summary>
/// 一个可蒙皮顶点。骨骼下标是 <b>Unit 场景节点下标</b>（对应 <see cref="Hd2UnitDefinition.Transforms"/>），
/// 写入时会被换成该材质调色板内的 8 位局部下标。
/// </summary>
public readonly record struct Hd2UnitVertex(Vector3 Position, Vector3 Normal, Vector2 Uv,
    float W0, float W1, float W2, float W3, int B0, int B1, int B2, int B3)
{
    public static Hd2UnitVertex Skinned(Vector3 position, Vector3 normal, Vector2 uv,
        ReadOnlySpan<float> weights, ReadOnlySpan<int> bones)
    {
        Span<float> w = stackalloc float[4];
        Span<int> b = stackalloc int[4];
        for (var i = 0; i < 4 && i < weights.Length; i++) { w[i] = weights[i]; b[i] = bones[i]; }
        return new Hd2UnitVertex(position, normal, uv, w[0], w[1], w[2], w[3], b[0], b[1], b[2], b[3]);
    }
}

/// <summary>一个网格：单一材质、自己的顶点与索引（索引相对本网格顶点起点）。</summary>
public sealed record Hd2UnitMesh(uint MeshId, int MaterialIndex, IReadOnlyList<Hd2UnitVertex> Vertices,
    IReadOnlyList<int> Indices);

public sealed record Hd2UnitDefinition(IReadOnlyList<Hd2UnitTransform> Transforms,
    IReadOnlyList<Hd2UnitMaterial> Materials, IReadOnlyList<Hd2UnitMesh> Meshes);

public sealed record Hd2UnitSummary(int MeshCount, int VertexCount, int TriangleCount, int TransformCount,
    int MaterialCount, int RealBoneCount, int MaxPaletteSize);

public sealed record Hd2UnitResult(byte[] Main, byte[] Gpu, Hd2UnitSummary Summary);

/// <summary>
/// 从转换结果直接写游戏原生 Stingray <c>.unit</c> 资源。
///
/// 结构依据：AQSDK <c>stingray/unit.py</c> 的写入顺序，以及本仓库
/// <c>PatchResourceInspectionService</c> 的读取实现。写入后由 <see cref="Hd2UnitInspector"/>
/// 逐项复核（尺寸、偏移、顶点步长、缓冲范围、调色板），失败即抛异常，绝不落半成品。
/// </summary>
public static class Hd2UnitBuilder
{
    /// <summary>当前验证过的最高 Unit 版本；旧版顶点格式表被整体替换，因此只用这个版本。</summary>
    public const uint UnitVersion = 10800438;

    public const int HeaderSize = 0x80;
    public const int MeshInfoHeaderSize = 0x80;
    public const int SectionSize = 24;
    public const int StreamInfoSize = 0x1B0;
    /// <summary>单条 StreamInfo 的写入跨度（含尾部 16 字节保留区），AQSDK 写 448 字节。</summary>
    public const int StreamInfoRecordSize = 0x1C0;
    public const int MaxTransforms = 4096;
    public const int MaxPaletteSize = 256;

    public static Hd2UnitResult Build(Hd2UnitDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Validate(definition);

        var transforms = definition.Transforms;
        var meshes = definition.Meshes;

        // 1. 每个材质用到的骨骼（升序）与全局真实骨骼表。
        var palettes = new int[definition.Materials.Count][];
        var usedBones = new SortedSet<int>();
        for (var materialIndex = 0; materialIndex < definition.Materials.Count; materialIndex++)
        {
            var bones = new SortedSet<int>();
            foreach (var mesh in meshes)
            {
                if (mesh.MaterialIndex != materialIndex) continue;
                foreach (var vertex in mesh.Vertices) Collect(vertex, bones);
            }
            if (bones.Count == 0) bones.Add(0);
            if (bones.Count > MaxPaletteSize)
                throw new Hd2FormatException($"Material {materialIndex} needs {bones.Count} bone influences; " +
                    $"a Unit palette holds at most {MaxPaletteSize}.");
            palettes[materialIndex] = bones.ToArray();
            usedBones.UnionWith(bones);
        }
        var realBones = usedBones.ToArray();
        var realIndex = new Dictionary<int, int>(realBones.Length);
        for (var i = 0; i < realBones.Length; i++) realIndex[realBones[i]] = i;

        cancellationToken.ThrowIfCancellationRequested();

        // 2. GPU 载荷：每个网格一段顶点缓冲（16 字节对齐）+ 紧随其后的索引缓冲。
        var layout = new List<(int VertexOffset, int VertexSize, int IndexOffset, int IndexSize)>(meshes.Count);
        var indexElementSize = meshes.All(mesh => mesh.Vertices.Count <= ushort.MaxValue) ? 2 : 4;
        var gpu = new List<byte>();
        foreach (var mesh in meshes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vertexOffset = gpu.Count;
            var palette = palettes[mesh.MaterialIndex];
            var paletteIndex = new Dictionary<int, int>(palette.Length);
            for (var i = 0; i < palette.Length; i++) paletteIndex[palette[i]] = i;

            foreach (var vertex in mesh.Vertices) WriteVertex(gpu, vertex, paletteIndex);
            var vertexSize = Align16(gpu.Count - vertexOffset);
            while (gpu.Count - vertexOffset < vertexSize) gpu.Add(0);

            var indexOffset = gpu.Count;
            foreach (var index in mesh.Indices)
            {
                if (indexElementSize == 2) WriteU16(gpu, (ushort)index);
                else WriteU32(gpu, (uint)index);
            }
            layout.Add((vertexOffset, vertexSize, indexOffset, mesh.Indices.Count * indexElementSize));
        }

        // 3. 主数据：头部 + TransformInfo + BoneInfo + StreamInfo + MeshInfo + 材质表 + 结束标记。
        var main = new Blob();
        main.Skip(HeaderSize);
        var transformOffset = main.Position;
        WriteTransformInfo(main, transforms);
        main.Align16();
        var boneInfoOffset = main.Position;
        WriteBoneInfo(main, transforms, realBones, realIndex, palettes);
        main.Align16();
        var streamInfoOffset = main.Position;
        WriteStreamInfo(main, meshes, layout, indexElementSize);
        main.Align16();
        var meshInfoOffset = main.Position;
        WriteMeshInfo(main, definition);
        var materialsOffset = main.Position;
        WriteMaterials(main, definition.Materials);
        var endingOffset = main.Position;
        main.WriteU64((ulong)meshes.Count);

        main.PatchU32(0x00, 0);
        main.PatchU64(0x08, 0);                       // BonesRef：本包不附骨骼资源
        main.PatchU64(0x10, 0);                       // CompositeRef
        main.PatchU32(0x28, 0);                       // HeaderData1
        main.PatchU32(0x2C, UnitVersion);
        main.PatchU32(0x30, 0);                       // 无 LOD group 区段（0 = 缺省）
        main.PatchU32(0x34, (uint)transformOffset);
        main.PatchU32(0x58, (uint)boneInfoOffset);
        main.PatchU32(0x5C, (uint)streamInfoOffset);
        main.PatchU32(0x60, (uint)endingOffset);
        main.PatchU32(0x64, (uint)meshInfoOffset);
        main.PatchU32(0x70, (uint)materialsOffset);

        var bytes = main.ToArray();
        var gpuBytes = gpu.ToArray();
        var issues = Hd2UnitInspector.Validate(bytes, gpuBytes);
        if (issues.Count > 0)
            throw new Hd2FormatException("Generated Unit failed self-validation: " + string.Join(" | ", issues));

        var summary = new Hd2UnitSummary(meshes.Count, meshes.Sum(mesh => mesh.Vertices.Count),
            meshes.Sum(mesh => mesh.Indices.Count / 3), transforms.Count, definition.Materials.Count,
            realBones.Length, palettes.Length == 0 ? 0 : palettes.Max(palette => palette.Length));
        return new Hd2UnitResult(bytes, gpuBytes, summary);
    }

    /// <summary>本模块写入的顶点布局：RGBA8 颜色、float3 位置、八面体法线、float2 UV、half4 权重、u8×4 骨骼。</summary>
    public static int VertexStride => 4 + 12 + 4 + 8 + 8 + 4;

    private static void Collect(Hd2UnitVertex vertex, SortedSet<int> bones)
    {
        Span<float> weights = [vertex.W0, vertex.W1, vertex.W2, vertex.W3];
        Span<int> bonesOfVertex = [vertex.B0, vertex.B1, vertex.B2, vertex.B3];
        for (var lane = 0; lane < 4; lane++)
            if (weights[lane] > 1e-4f) bones.Add(bonesOfVertex[lane]);
    }

    private static void Validate(Hd2UnitDefinition definition)
    {
        if (definition.Transforms.Count == 0) throw new Hd2FormatException("A Unit needs at least one transform.");
        if (definition.Transforms.Count > MaxTransforms)
            throw new Hd2FormatException($"A Unit supports at most {MaxTransforms} transforms.");
        if (definition.Meshes.Count == 0) throw new Hd2FormatException("A Unit needs at least one mesh.");
        if (definition.Materials.Count == 0) throw new Hd2FormatException("A Unit needs at least one material.");
        var slots = new HashSet<uint>();
        foreach (var material in definition.Materials)
            if (!slots.Add(material.Slot))
                throw new Hd2FormatException($"Duplicate material slot 0x{material.Slot:x8}.");
        foreach (var transform in definition.Transforms)
        {
            if (transform.Parent >= definition.Transforms.Count)
                throw new Hd2FormatException($"Transform \"{transform.Name}\" has an out-of-range parent.");
            if (!IsFinite(transform.RestLocal))
                throw new Hd2FormatException($"Transform \"{transform.Name}\" has a non-finite matrix.");
        }
        foreach (var mesh in definition.Meshes)
        {
            if (mesh.Vertices.Count == 0) throw new Hd2FormatException($"Mesh 0x{mesh.MeshId:x8} has no vertices.");
            if (mesh.Indices.Count == 0 || mesh.Indices.Count % 3 != 0)
                throw new Hd2FormatException($"Mesh 0x{mesh.MeshId:x8} must contain whole triangles.");
            if (mesh.MaterialIndex < 0 || mesh.MaterialIndex >= definition.Materials.Count)
                throw new Hd2FormatException($"Mesh 0x{mesh.MeshId:x8} references a missing material.");
            if (mesh.Indices.Any(index => index < 0 || index >= mesh.Vertices.Count))
                throw new Hd2FormatException($"Mesh 0x{mesh.MeshId:x8} has an out-of-range index.");
            foreach (var vertex in mesh.Vertices)
            {
                if (!IsFinite(vertex.Position) || !IsFinite(vertex.Normal) || !IsFinite(vertex.Uv))
                    throw new Hd2FormatException($"Mesh 0x{mesh.MeshId:x8} has a non-finite vertex.");
                Span<int> bones = [vertex.B0, vertex.B1, vertex.B2, vertex.B3];
                foreach (var bone in bones)
                    if (bone < 0 || bone >= definition.Transforms.Count)
                        throw new Hd2FormatException($"Mesh 0x{mesh.MeshId:x8} references transform {bone}.");
            }
        }
    }

    private static void WriteTransformInfo(Blob main, IReadOnlyList<Hd2UnitTransform> transforms)
    {
        main.WriteU32((uint)transforms.Count);
        main.WriteBytes(new byte[12]);

        var world = new Matrix4x4[transforms.Count];
        for (var i = 0; i < transforms.Count; i++)
        {
            var local = transforms[i].RestLocal;
            world[i] = transforms[i].Parent >= 0 && transforms[i].Parent < i
                ? local * world[transforms[i].Parent]
                : local;
            main.WriteF32(local.M11); main.WriteF32(local.M12); main.WriteF32(local.M13);
            main.WriteF32(local.M21); main.WriteF32(local.M22); main.WriteF32(local.M23);
            main.WriteF32(local.M31); main.WriteF32(local.M32); main.WriteF32(local.M33);
            main.WriteF32(local.M41); main.WriteF32(local.M42); main.WriteF32(local.M43);
            main.WriteF32(1f); main.WriteF32(1f); main.WriteF32(1f); main.WriteF32(0f);
        }
        foreach (var matrix in world) WriteMatrix(main, matrix);
        for (var i = 0; i < transforms.Count; i++)
        {
            main.WriteU16(1);
            main.WriteU16((ushort)(transforms[i].Parent < 0 ? 0xFFFF : transforms[i].Parent));
        }
        foreach (var transform in transforms) main.WriteU32(StingrayHash.Murmur32(transform.Name));
    }

    private static void WriteBoneInfo(Blob main, IReadOnlyList<Hd2UnitTransform> transforms, int[] realBones,
        IReadOnlyDictionary<int, int> realIndex, int[][] palettes)
    {
        // 只有一条 LOD0 记录：材质调色板以 Remaps 的形式共存于同一条记录。
        const int tableSize = 8;
        main.WriteU32(1);
        main.WriteU32(tableSize);
        var recordStart = main.Position;

        var matricesOffset = 16;
        var realOffset = matricesOffset + 64 * realBones.Length;
        var remapOffset = realOffset + 4 * realBones.Length;
        main.WriteU32((uint)realBones.Length);
        main.WriteU32((uint)matricesOffset);
        main.WriteU32((uint)realOffset);
        main.WriteU32((uint)remapOffset);

        var world = WorldMatrices(transforms);
        foreach (var bone in realBones)
        {
            if (!Matrix4x4.Invert(world[bone], out var inverse))
                throw new Hd2FormatException($"Transform \"{transforms[bone].Name}\" has a singular world matrix.");
            WriteMatrix(main, inverse);
        }
        foreach (var bone in realBones) main.WriteU32((uint)bone);

        main.WriteU32((uint)palettes.Length);
        var palettesStart = 4 + 8 * palettes.Length;
        var cursor = palettesStart;
        foreach (var palette in palettes)
        {
            main.WriteU32((uint)cursor);
            main.WriteU32((uint)palette.Length);
            cursor += 4 * palette.Length;
        }
        foreach (var palette in palettes)
            foreach (var bone in palette)
                main.WriteU32((uint)realIndex[bone]);

        var expectedSize = remapOffset + palettesStart + palettes.Sum(palette => 4 * palette.Length);
        if (main.Position - recordStart != expectedSize)
            throw new Hd2FormatException("Internal BoneInfo record size mismatch.");
    }

    private static void WriteStreamInfo(Blob main, IReadOnlyList<Hd2UnitMesh> meshes,
        IReadOnlyList<(int VertexOffset, int VertexSize, int IndexOffset, int IndexSize)> layout,
        int indexElementSize)
    {
        var start = main.Position;
        main.WriteU32((uint)meshes.Count);
        var recordsStart = Align16(4 + 8 * meshes.Count + 4);
        for (var i = 0; i < meshes.Count; i++) main.WriteU32((uint)(recordsStart + i * StreamInfoRecordSize));
        foreach (var mesh in meshes) main.WriteU32(mesh.MeshId);
        main.WriteU32(0);
        while (main.Position - start < recordsStart) main.WriteByte(0);

        for (var i = 0; i < meshes.Count; i++)
        {
            var mesh = meshes[i];
            var (vertexOffset, vertexSize, indexOffset, indexSize) = layout[i];
            var recordStartPosition = main.Position;
            main.WriteU64(0);                                   // ComponentInfoID
            main.WriteU32((uint)Hd2VertexComponent.Color);
            main.WriteU32((uint)Hd2VertexFormat.Rgba8); main.WriteU32(0); main.WriteU64(0);
            main.WriteU32((uint)Hd2VertexComponent.Position);
            main.WriteU32((uint)Hd2VertexFormat.Float3); main.WriteU32(0); main.WriteU64(0);
            main.WriteU32((uint)Hd2VertexComponent.Normal);
            main.WriteU32((uint)Hd2VertexFormat.OctNormal); main.WriteU32(0); main.WriteU64(0);
            main.WriteU32((uint)Hd2VertexComponent.Uv);
            main.WriteU32((uint)Hd2VertexFormat.Float2); main.WriteU32(0); main.WriteU64(0);
            main.WriteU32((uint)Hd2VertexComponent.BoneWeight);
            main.WriteU32((uint)Hd2VertexFormat.Half4); main.WriteU32(0); main.WriteU64(0);
            main.WriteU32((uint)Hd2VertexComponent.BoneIndex);
            main.WriteU32((uint)Hd2VertexFormat.Uint8x4); main.WriteU32(0); main.WriteU64(0);
            while (main.Position - recordStartPosition < 0x148) main.WriteByte(0);
            main.WriteU64(6);                                   // ComponentCount
            main.WriteU64(0);                                   // VertexBufferID
            main.WriteU64(0);
            main.WriteU32((uint)mesh.Vertices.Count);
            main.WriteU32((uint)VertexStride);
            main.WriteU64(0);
            main.WriteU64(0);
            main.WriteU64(0);                                   // IndexBufferID
            main.WriteU64(0);
            main.WriteU32((uint)mesh.Indices.Count);
            main.WriteU32((uint)(indexElementSize == 2 ? 0 : 1));
            main.WriteU64(0);
            main.WriteU64(0);
            main.WriteU32((uint)vertexOffset);
            main.WriteU32((uint)vertexSize);
            main.WriteU32((uint)indexOffset);
            main.WriteU32((uint)indexSize);
            main.WriteBytes(new byte[16]);
            if (main.Position - recordStartPosition != StreamInfoRecordSize)
                throw new Hd2FormatException("Internal StreamInfo record size mismatch.");
        }
    }

    private static void WriteMeshInfo(Blob main, Hd2UnitDefinition definition)
    {
        var meshes = definition.Meshes;
        main.WriteU32((uint)meshes.Count);
        var tableSize = 4 + 8 * meshes.Count;
        var cursor = tableSize;
        var recordSizes = new List<int>(meshes.Count);
        foreach (var mesh in meshes)
        {
            var recordSize = MeshInfoHeaderSize + 4 + SectionSize;
            recordSizes.Add(recordSize);
            main.WriteU32((uint)cursor);
            cursor += recordSize;
        }
        foreach (var mesh in meshes) main.WriteU32(mesh.MeshId);

        for (var i = 0; i < meshes.Count; i++)
        {
            var mesh = meshes[i];
            var start = main.Position;
            main.WriteU64(0);
            main.WriteBytes(new byte[32]);
            main.WriteU32(mesh.MeshId);
            main.WriteU32(0);
            main.WriteU32(0xFFFFFFFF);                          // TransformIndex = -1（网格不附加到独立节点）
            main.WriteU32(0);
            main.WriteU32(0);                                   // LodIndex = 0 → BoneInfo[0]
            main.WriteU32((uint)i);                             // StreamIndex
            main.WriteBytes(new byte[40]);
            main.WriteU32(1);                                   // MaterialCount
            main.WriteU32(MeshInfoHeaderSize);                  // MaterialArrayOffset
            main.WriteU64(0);
            main.WriteU32(1);                                   // SectionCount
            main.WriteU32(MeshInfoHeaderSize + 4);              // SectionsOffset
            main.WriteU32(definition.Materials[mesh.MaterialIndex].Slot);
            main.WriteU32(0);                                   // Section.MaterialIndex
            main.WriteU32(0);                                   // Section.VertexOffset
            main.WriteU32((uint)mesh.Vertices.Count);
            main.WriteU32(0);                                   // Section.IndexOffset
            main.WriteU32((uint)mesh.Indices.Count);
            main.WriteU32(0);                                   // Section.GroupIndex
            if (main.Position - start != recordSizes[i])
                throw new Hd2FormatException("Internal MeshInfo record size mismatch.");
        }
    }

    private static void WriteMaterials(Blob main, IReadOnlyList<Hd2UnitMaterial> materials)
    {
        main.WriteU32((uint)materials.Count);
        foreach (var material in materials) main.WriteU32(material.Slot);
        foreach (var material in materials) main.WriteU64(material.MaterialId);
    }

    private static Matrix4x4[] WorldMatrices(IReadOnlyList<Hd2UnitTransform> transforms)
    {
        var world = new Matrix4x4[transforms.Count];
        for (var i = 0; i < transforms.Count; i++)
        {
            var local = transforms[i].RestLocal;
            world[i] = transforms[i].Parent >= 0 && transforms[i].Parent < i
                ? local * world[transforms[i].Parent]
                : local;
        }
        return world;
    }

    private static void WriteMatrix(Blob main, Matrix4x4 matrix)
    {
        main.WriteF32(matrix.M11); main.WriteF32(matrix.M12); main.WriteF32(matrix.M13); main.WriteF32(matrix.M14);
        main.WriteF32(matrix.M21); main.WriteF32(matrix.M22); main.WriteF32(matrix.M23); main.WriteF32(matrix.M24);
        main.WriteF32(matrix.M31); main.WriteF32(matrix.M32); main.WriteF32(matrix.M33); main.WriteF32(matrix.M34);
        main.WriteF32(matrix.M41); main.WriteF32(matrix.M42); main.WriteF32(matrix.M43); main.WriteF32(matrix.M44);
    }

    private static void WriteVertex(List<byte> gpu, Hd2UnitVertex vertex, IReadOnlyDictionary<int, int> palette)
    {
        WriteU32(gpu, 0xFFFFFFFFu);                             // RGBA8 颜色：白，不参与着色
        WriteF32(gpu, vertex.Position.X); WriteF32(gpu, vertex.Position.Y); WriteF32(gpu, vertex.Position.Z);
        WriteU32(gpu, PackNormal(vertex.Normal));
        WriteF32(gpu, vertex.Uv.X); WriteF32(gpu, vertex.Uv.Y);
        var weights = NormalizeWeights(vertex);
        for (var lane = 0; lane < 4; lane++) WriteU16(gpu, BitConverter.HalfToUInt16Bits((Half)weights[lane]));
        Span<int> bones = [vertex.B0, vertex.B1, vertex.B2, vertex.B3];
        for (var lane = 0; lane < 4; lane++)
        {
            var weight = weights[lane];
            if (weight <= 0f) { gpu.Add(0); continue; }
            gpu.Add((byte)Math.Clamp(palette[bones[lane]], 0, 255));
        }
    }

    /// <summary>权重归一化为半精度且和尽量精确等于 1（读取器与游戏都按此校验）。</summary>
    private static float[] NormalizeWeights(Hd2UnitVertex vertex)
    {
        Span<float> source = [vertex.W0, vertex.W1, vertex.W2, vertex.W3];
        var weights = new float[4];
        var sum = 0f;
        for (var i = 0; i < 4; i++) { weights[i] = MathF.Max(0f, source[i]); sum += weights[i]; }
        if (sum <= 0f) { weights[0] = 1f; return weights; }
        for (var i = 0; i < 4; i++) weights[i] /= sum;

        var half = new float[4];
        var total = 0f;
        for (var i = 0; i < 4; i++) { half[i] = (float)(Half)weights[i]; total += half[i]; }
        if (MathF.Abs(total - 1f) > 1f / 2048f)
        {
            var largest = 0;
            for (var i = 1; i < 4; i++) if (half[i] > half[largest]) largest = i;
            half[largest] = MathF.Max(0f, (float)(Half)(half[largest] + (1f - total)));
            total = half.Sum();
            if (total <= 0f) half[0] = 1f;
        }
        return half;
    }

    /// <summary>八面体 10:10 打包；与 AQSDK <c>encode_packed_oct_norm</c> 的截断行为一致。</summary>
    public static uint PackNormal(Vector3 normal)
    {
        var length = normal.Length();
        if (!float.IsFinite(length) || length <= 0f) return 0x80000000u;
        var x = normal.X / length;
        var y = normal.Y / length;
        var z = normal.Z / length;
        var l1 = MathF.Abs(x) + MathF.Abs(y) + MathF.Abs(z);
        if (l1 <= 0f) { x = 0f; y = 0f; }
        else { x /= l1; y /= l1; }
        if (z < 0f)
        {
            var sx = x >= 0f ? 1f : -1f;
            var sy = y >= 0f ? 1f : -1f;
            var encodedX = (1f - MathF.Abs(y)) * sx;
            var encodedY = (1f - MathF.Abs(x)) * sy;
            x = encodedX; y = encodedY;
        }
        var packedX = (uint)Math.Clamp((int)Math.Truncate((x + 1f) * (1023f / 2f)), 0, 1023);
        var packedY = (uint)Math.Clamp((int)Math.Truncate((y + 1f) * (1023f / 2f)), 0, 1023);
        return packedX | (packedY << 10);
    }

    private static bool IsFinite(Matrix4x4 matrix) =>
        float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12) && float.IsFinite(matrix.M13) && float.IsFinite(matrix.M14) &&
        float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22) && float.IsFinite(matrix.M23) && float.IsFinite(matrix.M24) &&
        float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32) && float.IsFinite(matrix.M33) && float.IsFinite(matrix.M34) &&
        float.IsFinite(matrix.M41) && float.IsFinite(matrix.M42) && float.IsFinite(matrix.M43) && float.IsFinite(matrix.M44);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);

    internal static int Align16(int value) => (value + 15) & ~15;

    private static void WriteU16(List<byte> buffer, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        buffer.Add(bytes[0]); buffer.Add(bytes[1]);
    }

    private static void WriteU32(List<byte> buffer, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        buffer.AddRange(bytes.ToArray());
    }

    private static void WriteF32(List<byte> buffer, float value) =>
        WriteU32(buffer, BitConverter.SingleToUInt32Bits(value));

    /// <summary>可回填的小端字节缓冲。</summary>
    private sealed class Blob
    {
        private readonly List<byte> _bytes = [];
        public int Position => _bytes.Count;
        public void Skip(int count) => _bytes.AddRange(new byte[count]);
        public void WriteByte(byte value) => _bytes.Add(value);
        public void WriteBytes(ReadOnlySpan<byte> value) => _bytes.AddRange(value.ToArray());
        public void WriteU16(ushort value) => Hd2UnitBuilder.WriteU16(_bytes, value);
        public void WriteU32(uint value) => Hd2UnitBuilder.WriteU32(_bytes, value);
        public void WriteU64(ulong value)
        {
            WriteU32((uint)value);
            WriteU32((uint)(value >> 32));
        }
        public void WriteF32(float value) => Hd2UnitBuilder.WriteF32(_bytes, value);
        public void PatchU32(int offset, uint value)
        {
            if (offset + 4 > _bytes.Count) throw new Hd2FormatException("Patch offset outside the buffer.");
            BinaryPrimitives.WriteUInt32LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_bytes)[offset..], value);
        }
        public void PatchU64(int offset, ulong value)
        {
            PatchU32(offset, (uint)value);
            PatchU32(offset + 4, (uint)(value >> 32));
        }
        public void Align16()
        {
            var remainder = _bytes.Count % 16;
            if (remainder != 0) _bytes.AddRange(new byte[16 - remainder]);
        }
        public byte[] ToArray() => _bytes.ToArray();
    }
}
