namespace HD2Mimnt.ModelConversion.Source.Vtx;

[Flags]
public enum VtxStripFlags : byte
{
    None = 0,
    TriangleList = 0x01,
    TriangleStrip = 0x02,
}

[Flags]
public enum VtxMeshFlags : byte
{
    None = 0,
    IsTeeth = 0x01,
    IsEyes = 0x02,
}

public readonly record struct VtxVertex(byte BoneWeightIndex0, byte BoneWeightIndex1, byte BoneWeightIndex2, byte BoneCount, ushort OriginalMeshVertexId, sbyte Bone0, sbyte Bone1, sbyte Bone2);

public sealed record VtxStrip(int IndexCount, int IndexOffset, int VertexCount, int VertexOffset, short BoneCount, VtxStripFlags Flags);

public sealed record VtxStripGroup(IReadOnlyList<VtxVertex> Vertices, ushort[] Indices, IReadOnlyList<VtxStrip> Strips, byte Flags);

public sealed record VtxMesh(IReadOnlyList<VtxStripGroup> StripGroups, VtxMeshFlags Flags)
{
    /// <summary>Triangles as indices into the owning MDL mesh's vertex range (<c>origMeshVertID</c>), with degenerate triangles removed.</summary>
    public IEnumerable<(int A, int B, int C)> EnumerateTriangles(CancellationToken cancellationToken = default)
    {
        foreach (var group in StripGroups)
        {
            foreach (var strip in group.Strips)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (strip.IndexCount < 0 || strip.IndexOffset < 0 || (long)strip.IndexOffset + strip.IndexCount > group.Indices.Length)
                {
                    throw new SourceFormatException("VTX strip index range exceeds its strip group");
                }

                int Map(int k)
                {
                    var local = group.Indices[strip.IndexOffset + k];
                    if (local >= group.Vertices.Count)
                    {
                        throw new SourceFormatException($"VTX index {local} exceeds strip group vertex count {group.Vertices.Count}");
                    }

                    return group.Vertices[local].OriginalMeshVertexId;
                }

                if ((strip.Flags & VtxStripFlags.TriangleList) != 0)
                {
                    if (strip.IndexCount % 3 != 0)
                        throw new SourceFormatException("VTX triangle list has an incomplete triangle");
                    var usable = strip.IndexCount;
                    for (var k = 0; k < usable; k += 3)
                    {
                        var (a, b, c) = (Map(k), Map(k + 1), Map(k + 2));
                        if (a != b && b != c && a != c)
                        {
                            yield return (a, b, c);
                        }
                    }
                }
                else if ((strip.Flags & VtxStripFlags.TriangleStrip) != 0)
                {
                    for (var k = 0; k + 2 < strip.IndexCount; k++)
                    {
                        var (a, b, c) = (Map(k), Map(k + 1), Map(k + 2));
                        if ((k & 1) != 0)
                        {
                            (a, b) = (b, a);
                        }

                        if (a != b && b != c && a != c)
                        {
                            yield return (a, b, c);
                        }
                    }
                }
                else
                {
                    throw new SourceFormatException($"Unsupported VTX primitive flags 0x{(byte)strip.Flags:x2}");
                }
            }
        }
    }
}

public sealed record VtxLod(IReadOnlyList<VtxMesh> Meshes, float SwitchPoint);

public sealed record VtxModel(IReadOnlyList<VtxLod> Lods);

public sealed record VtxBodyPart(IReadOnlyList<VtxModel> Models);

/// <summary>
/// Reads the optimized triangle data (<c>.dx90.vtx</c>, version 7). The hierarchy mirrors the MDL
/// body part / model / LOD / mesh tree. Structures are byte-packed per the Source SDK 2013 <c>optimize.h</c>.
/// </summary>
public sealed class VtxFile
{
    public const int HeaderSize = 36;
    public const int BodyPartSize = 8;
    public const int ModelSize = 8;
    public const int LodSize = 12;
    public const int MeshSize = 9;
    public const int StripGroupSize = 25;
    public const int StripSize = 27;
    public const int VertexSize = 9;

    private readonly CancellationToken _cancellationToken;

    private VtxFile(BinaryBlock block, CancellationToken cancellationToken)
    {
        Block = block;
        _cancellationToken = cancellationToken;
    }

    public BinaryBlock Block { get; }
    public int Version { get; private set; }
    public int VertexCacheSize { get; private set; }
    public ushort MaxBonesPerStrip { get; private set; }
    public ushort MaxBonesPerTriangle { get; private set; }
    public int MaxBonesPerVertex { get; private set; }
    public uint Checksum { get; private set; }
    public int LodCount { get; private set; }
    public IReadOnlyList<VtxBodyPart> BodyParts { get; private set; } = Array.Empty<VtxBodyPart>();

    public static VtxFile Parse(byte[] data, string label = "VTX", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var block = new BinaryBlock(data, label);
        if (block.Length < HeaderSize)
        {
            throw new SourceFormatException("VTX header is truncated");
        }

        var file = new VtxFile(block, cancellationToken)
        {
            Version = block.I32(0),
            VertexCacheSize = block.I32(4),
            MaxBonesPerStrip = block.U16(8),
            MaxBonesPerTriangle = block.U16(10),
            MaxBonesPerVertex = block.I32(12),
            Checksum = block.U32(16),
            LodCount = block.I32(20),
        };
        if (file.Version != 7)
        {
            throw new SourceFormatException($"Unsupported VTX version {file.Version}; only v7 is supported");
        }

        if (file.LodCount < 1 || file.LodCount > 8)
        {
            throw new SourceFormatException($"Invalid VTX LOD count {file.LodCount}");
        }

        var bodyPartCount = block.I32(28);
        var bodyPartStart = block.I32(32);
        if (bodyPartCount < 0 || bodyPartCount > 1024)
        {
            throw new SourceFormatException($"Implausible VTX body part count {bodyPartCount}");
        }

        block.Check(bodyPartStart, (long)bodyPartCount * BodyPartSize, "VTX body part table");
        var bodyParts = new List<VtxBodyPart>(bodyPartCount);
        for (var i = 0; i < bodyPartCount; i++)
        {
            var offset = bodyPartStart + i * BodyPartSize;
            var modelCount = block.I32(offset);
            var modelStart = offset + block.I32(offset + 4);
            if (modelCount < 0 || modelCount > 4096)
            {
                throw new SourceFormatException($"VTX body part {i} has implausible model count {modelCount}");
            }

            block.Check(modelStart, (long)modelCount * ModelSize, "VTX model table");
            var models = new List<VtxModel>(modelCount);
            for (var m = 0; m < modelCount; m++)
            {
                models.Add(file.ParseModel(modelStart + m * ModelSize));
            }

            bodyParts.Add(new VtxBodyPart(models));
        }

        file.BodyParts = bodyParts;
        return file;
    }

    private VtxModel ParseModel(int offset)
    {
        var lodCount = Block.I32(offset);
        var lodStart = offset + Block.I32(offset + 4);
        if (lodCount < 0 || lodCount > 8)
        {
            throw new SourceFormatException($"VTX model has implausible LOD count {lodCount}");
        }

        Block.Check(lodStart, (long)lodCount * LodSize, "VTX LOD table");
        var lods = new List<VtxLod>(lodCount);
        for (var i = 0; i < lodCount; i++)
        {
            var lodOffset = lodStart + i * LodSize;
            var meshCount = Block.I32(lodOffset);
            var meshStart = lodOffset + Block.I32(lodOffset + 4);
            if (meshCount < 0 || meshCount > 100_000)
            {
                throw new SourceFormatException($"VTX LOD has implausible mesh count {meshCount}");
            }

            Block.Check(meshStart, (long)meshCount * MeshSize, "VTX mesh table");
            var meshes = new List<VtxMesh>(meshCount);
            for (var j = 0; j < meshCount; j++)
            {
                meshes.Add(ParseMesh(meshStart + j * MeshSize));
            }

            lods.Add(new VtxLod(meshes, Block.F32(lodOffset + 8)));
        }

        return new VtxModel(lods);
    }

    private VtxMesh ParseMesh(int offset)
    {
        var groupCount = Block.I32(offset);
        var groupStart = offset + Block.I32(offset + 4);
        var flags = (VtxMeshFlags)Block.U8(offset + 8);
        if (groupCount < 0 || groupCount > 100_000)
        {
            throw new SourceFormatException($"VTX mesh has implausible strip group count {groupCount}");
        }

        Block.Check(groupStart, (long)groupCount * StripGroupSize, "VTX strip group table");
        var groups = new List<VtxStripGroup>(groupCount);
        for (var g = 0; g < groupCount; g++)
        {
            groups.Add(ParseStripGroup(groupStart + g * StripGroupSize));
        }

        return new VtxMesh(groups, flags);
    }

    private VtxStripGroup ParseStripGroup(int offset)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var vertexCount = Block.I32(offset);
        var vertexStart = offset + Block.I32(offset + 4);
        var indexCount = Block.I32(offset + 8);
        var indexStart = offset + Block.I32(offset + 12);
        var stripCount = Block.I32(offset + 16);
        var stripStart = offset + Block.I32(offset + 20);
        var flags = Block.U8(offset + 24);
        if (vertexCount < 0 || indexCount < 0 || stripCount < 0)
        {
            throw new SourceFormatException("VTX strip group has negative counts");
        }

        Block.Check(vertexStart, (long)vertexCount * VertexSize, "VTX strip group vertices");
        Block.Check(indexStart, indexCount * 2L, "VTX strip group indices");
        Block.Check(stripStart, (long)stripCount * StripSize, "VTX strips");

        var vertices = new VtxVertex[vertexCount];
        for (var i = 0; i < vertexCount; i++)
        {
            var v = vertexStart + i * VertexSize;
            vertices[i] = new VtxVertex(
                Block.U8(v), Block.U8(v + 1), Block.U8(v + 2), Block.U8(v + 3),
                Block.U16(v + 4),
                Block.I8(v + 6), Block.I8(v + 7), Block.I8(v + 8));
        }

        var indices = new ushort[indexCount];
        for (var i = 0; i < indexCount; i++)
        {
            indices[i] = Block.U16(indexStart + i * 2);
        }

        var strips = new List<VtxStrip>(stripCount);
        for (var s = 0; s < stripCount; s++)
        {
            var st = stripStart + s * StripSize;
            strips.Add(new VtxStrip(
                Block.I32(st), Block.I32(st + 4), Block.I32(st + 8), Block.I32(st + 12),
                Block.I16(st + 16), (VtxStripFlags)Block.U8(st + 18)));
        }

        return new VtxStripGroup(vertices, indices, strips, flags);
    }
}
