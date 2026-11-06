using System.Numerics;

namespace HD2Mimnt.ModelConversion.Source.Vvd;

public readonly record struct VvdVertex(Vector3 Position, Vector3 Normal, Vector2 TexCoord, Vector4? Tangent,
    float Weight0, float Weight1, float Weight2, byte Bone0, byte Bone1, byte Bone2, byte BoneCount)
{
    public float Weight(int slot) => slot switch { 0 => Weight0, 1 => Weight1, 2 => Weight2, _ => throw new ArgumentOutOfRangeException(nameof(slot)) };
    public byte Bone(int slot) => slot switch { 0 => Bone0, 1 => Bone1, 2 => Bone2, _ => throw new ArgumentOutOfRangeException(nameof(slot)) };
}

public readonly record struct VvdFixup(int Lod, int SourceVertexId, int VertexCount);

public sealed class VvdFile
{
    public const int VertexSize = 48;
    public const int TangentSize = 16;
    public const int FixupSize = 12;
    public const int MaxLods = 8;
    private readonly VvdVertex[] _pool;
    private readonly Dictionary<int, VvdVertex[]> _lodCache = new();

    private VvdFile(BinaryBlock block, VvdVertex[] pool) { Block = block; _pool = pool; }
    public BinaryBlock Block { get; }
    public int Version { get; private init; }
    public uint Checksum { get; private init; }
    public int LodCount { get; private init; }
    public int[] LodVertexCounts { get; private init; } = [];
    public IReadOnlyList<VvdFixup> Fixups { get; private init; } = [];
    public bool HasTangents { get; private init; }
    public ReadOnlySpan<VvdVertex> Pool => _pool;

    public static VvdFile Parse(byte[] data, string label = "VVD", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var block = new BinaryBlock(data, label);
        if (block.Length < 64 || !block.Slice(0, 4, "signature").SequenceEqual("IDSV"u8))
            throw new SourceFormatException("VVD signature is not IDSV");
        if (block.I32(4) != 4) throw new SourceFormatException("Only VVD v4 is supported");
        var lodCount = block.I32(12);
        if (lodCount < 1 || lodCount > MaxLods) throw new SourceFormatException("Invalid VVD LOD count");
        var lodCounts = Enumerable.Range(0, MaxLods).Select(i => block.I32(16 + i * 4)).ToArray();
        for (var lod = 0; lod < lodCount; lod++)
        {
            if (lodCounts[lod] < 0 || lodCounts[lod] > 5_000_000 || (lod > 0 && lodCounts[lod] > lodCounts[lod - 1]))
                throw new SourceFormatException($"Invalid VVD LOD {lod} vertex count {lodCounts[lod]}");
        }
        var fixupCount = block.I32(48);
        var fixupStart = block.I32(52);
        var vertexStart = block.I32(56);
        var tangentStart = block.I32(60);
        if (fixupCount < 0 || fixupCount > 1_000_000) throw new SourceFormatException("Invalid VVD fixup count");
        block.Check(fixupStart, (long)fixupCount * FixupSize, "fixup table");
        var poolCount = lodCounts[0];
        var fixups = new List<VvdFixup>(fixupCount);
        for (var i = 0; i < fixupCount; i++)
        {
            var offset = fixupStart + i * FixupSize;
            var fixup = new VvdFixup(block.I32(offset), block.I32(offset + 4), block.I32(offset + 8));
            if (fixup.Lod < 0 || fixup.Lod >= lodCount || fixup.VertexCount < 0 || fixup.SourceVertexId < 0 ||
                (long)fixup.SourceVertexId + fixup.VertexCount > poolCount)
                throw new SourceFormatException($"Invalid VVD fixup {i}");
            fixups.Add(fixup);
        }
        if (fixupCount > 0)
        {
            var end = 0;
            foreach (var fixup in fixups.OrderBy(f => f.SourceVertexId))
            {
                if (fixup.SourceVertexId != end) throw new SourceFormatException("VVD fixups overlap or leave a gap");
                end += fixup.VertexCount;
            }
            for (var lod = 0; lod < lodCount; lod++)
            {
                if (fixups.Where(f => f.Lod >= lod).Sum(f => (long)f.VertexCount) != lodCounts[lod])
                    throw new SourceFormatException($"VVD fixups do not match LOD {lod} count");
            }
        }
        block.Check(vertexStart, (long)poolCount * VertexSize, "vertex buffer");
        if (vertexStart < 64 || tangentStart < 0) throw new SourceFormatException("Invalid VVD buffer offset");
        var hasTangents = tangentStart != 0;
        if (hasTangents) block.Check(tangentStart, (long)poolCount * TangentSize, "tangent buffer");
        var pool = new VvdVertex[poolCount];
        for (var i = 0; i < poolCount; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var offset = vertexStart + i * VertexSize;
            var boneCount = block.U8(offset + 15);
            if (boneCount > 3) throw new SourceFormatException($"VVD vertex {i} has more than three influences");
            for (var component = 0; component < 12; component++)
            {
                if (component == 3) continue;
                if (!float.IsFinite(block.F32(offset + component * 4)))
                    throw new SourceFormatException($"VVD vertex {i} has non-finite data");
            }
            for (var lane = 0; lane < boneCount; lane++)
            {
                if (block.F32(offset + lane * 4) < 0)
                    throw new SourceFormatException($"VVD vertex {i} has negative weight");
            }
            Vector4? tangent = hasTangents ? block.Vec4(tangentStart + i * TangentSize) : null;
            if (tangent is { } t && (!float.IsFinite(t.X) || !float.IsFinite(t.Y) || !float.IsFinite(t.Z) || !float.IsFinite(t.W)))
                throw new SourceFormatException($"VVD tangent {i} is non-finite");
            pool[i] = new VvdVertex(block.Vec3(offset + 16), block.Vec3(offset + 28), block.Vec2(offset + 40), tangent,
                block.F32(offset), block.F32(offset + 4), block.F32(offset + 8),
                block.U8(offset + 12), block.U8(offset + 13), block.U8(offset + 14), boneCount);
        }
        return new VvdFile(block, pool)
        {
            Version = 4, Checksum = block.U32(8), LodCount = lodCount, LodVertexCounts = lodCounts,
            Fixups = fixups.AsReadOnly(), HasTangents = hasTangents
        };
    }

    public VvdVertex[] GetLodVertices(int lod)
    {
        if (lod < 0 || lod >= LodCount) throw new SourceFormatException($"VVD LOD {lod} is unavailable");
        if (_lodCache.TryGetValue(lod, out var cached)) return cached;
        var result = new VvdVertex[LodVertexCounts[lod]];
        if (Fixups.Count == 0)
            _pool.AsSpan(0, result.Length).CopyTo(result);
        else
        {
            var destination = 0;
            foreach (var fixup in Fixups)
            {
                if (fixup.Lod < lod) continue;
                _pool.AsSpan(fixup.SourceVertexId, fixup.VertexCount).CopyTo(result.AsSpan(destination));
                destination += fixup.VertexCount;
            }
        }
        _lodCache[lod] = result;
        return result;
    }
}
