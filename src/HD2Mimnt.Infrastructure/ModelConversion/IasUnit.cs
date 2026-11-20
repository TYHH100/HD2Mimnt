using System.Buffers.Binary;
using System.Text;

namespace HD2Mimnt.Infrastructure.ModelConversion;

public sealed record IasSceneNode(int Index, uint Hash, string? Name, int Parent, double[] Local, double[] StoredWorld, double[] World, double[] Scale);

public sealed record IasUnitBone(IReadOnlyList<double[]> Matrices, IReadOnlyList<uint> Real, IReadOnlyList<IReadOnlyList<uint>> Remaps);

public sealed record IasUnitStream(
    IReadOnlyList<(uint Kind, uint Format, uint Index, int Offset)> Components,
    int Vertices, int Stride, int VertexBase, int VertexBytes, int IndexBase, int IndexBytes, int Indices, int IndexStride);

/// <summary>网格分区：<c>Group</c> 是骨骼调色板重映射表的下标，多个分区可以共用同一组。</summary>
public sealed record IasUnitSection(int Index, uint Id, ulong? Material, uint VertexOffset, uint VerticesDeclared,
    int IndexOffset, uint Indices, uint Group, IReadOnlyList<(uint A, uint B, uint C)> Faces);

public sealed record IasUnitMesh(int Index, uint Transform, int Lod, uint Stream, IReadOnlyList<IasUnitSection> Sections,
    int VertexStart, int VertexCount, int VertexBytes, bool Rigid, bool Culling, IReadOnlyList<(uint Kind, uint Index)> VertexKeys);

/// <summary>已审阅的 Stingray Unit 结构：场景图、骨骼调色板、流/网格/材质与 GPU 顶点读取。</summary>
public sealed class IasUnit
{
    private static readonly Dictionary<uint, (int Size, string Kind)> Formats = new()
    {
        [0] = (4, "f"), [1] = (8, "2f"), [2] = (12, "3f"), [3] = (16, "4f"), [4] = (4, "4B"),
        [21] = (4, "I"), [22] = (8, "2I"), [23] = (12, "3I"), [24] = (16, "4I"),
        [25] = (1, "B"), [26] = (2, "2B"), [27] = (3, "3B"), [28] = (4, "4B"), [29] = (4, "I"), [30] = (4, "I"),
        [32] = (2, "e"), [33] = (4, "2e"), [34] = (6, "3e"), [35] = (8, "4e")
    };

    public IasUnit(string id, IasResource resource, IReadOnlyDictionary<uint, string> names, IasResource? externalBones = null,
        bool allowUnnamed = false)
    {
        Id = id;
        Resource = resource;
        AllowUnnamed = allowUnnamed;
        var toc = resource.Toc;
        // 顶点解析密集读取同一段载荷：这里取一次连续视图，文件载荷由 IasPayload 自行缓存。
        var gpu = resource.Gpu.Span;
        Refs = [ReadULong(toc, 0), ReadULong(toc, 8), ReadULong(toc, 16), ReadULong(toc, 24), ReadULong(toc, 32)];
        IasBinary.Require(Refs[0] == 0 && Refs[2] == 0, id + ": external Unit/Composite reference needs an adapter");
        if (Refs[1] != 0)
        {
            IasBinary.Require(externalBones is not null && externalBones.Toc.Length >= 16, id + ": external Bones resource is not resolved");
            var table = BoneNames(externalBones!);
            var merged = new Dictionary<uint, string>(names);
            foreach (var (hash, name) in table.Names)
            {
                IasBinary.Require(!merged.TryGetValue(hash, out var existing) || existing == name, "external Bones name hash collision");
                merged[hash] = name;
            }
            names = merged;
        }
        Graph = ReadGraph(toc, names);
        Version = ReadU(toc, 44);
        IasBinary.Require(Version is 10800436 or 10800437 or 10800438 or 1, "unsupported Unit version " + Version);
        Customization = ReadCustomization(toc);
        var start = (int)ReadU(toc, 88);
        var count = (int)ReadU(toc, start);
        IasBinary.Require(count <= 64, "bone-LOD table count");
        var bones = new List<IasUnitBone>(count);
        for (var index = 0; index < count; index++)
        {
            var baseOffset = start + (int)ReadU(toc, start + 4 + 4 * index);
            var paletteCount = (int)ReadU(toc, baseOffset);
            var matrixOffset = (int)ReadU(toc, baseOffset + 4);
            var realOffset = (int)ReadU(toc, baseOffset + 8);
            var remapOffset = (int)ReadU(toc, baseOffset + 12);
            IasBinary.Require(paletteCount <= 8192, "bone palette count");
            var matrices = new List<double[]>(paletteCount);
            for (var matrix = 0; matrix < paletteCount; matrix++)
                matrices.Add(IasAffine.FromColumnMatrix(IasBinary.ReadFloats(toc, baseOffset + matrixOffset + 64 * matrix, 16)));
            var real = new List<uint>(paletteCount);
            for (var item = 0; item < paletteCount; item++) real.Add(ReadU(toc, baseOffset + realOffset + 4 * item));
            var mapping = baseOffset + remapOffset;
            var mapCount = (int)ReadU(toc, mapping);
            IasBinary.Require(mapCount <= 1024, "palette remap sections");
            var maps = new List<IReadOnlyList<uint>>(mapCount);
            for (var item = 0; item < mapCount; item++)
            {
                var offset = (int)ReadU(toc, mapping + 4 + 8 * item);
                var length = (int)ReadU(toc, mapping + 8 + 8 * item);
                IasBinary.Require(length <= 8192, "palette section size");
                var values = new uint[length];
                for (var value = 0; value < length; value++) values[value] = ReadU(toc, mapping + offset + 4 * value);
                maps.Add(values);
            }
            IasBinary.Require(real.All(value => value < Graph.Count), "palette scene index");
            IasBinary.Require(maps.All(map => map.All(value => value < paletteCount)), "palette remap index");
            bones.Add(new IasUnitBone(matrices, real, maps));
        }
        Bones = bones;
        start = (int)ReadU(toc, 92);
        count = (int)ReadU(toc, start);
        IasBinary.Require(count is >= 1 and <= 1024, "stream count");
        Streams = new List<IasUnitStream>(count);
        for (var index = 0; index < count; index++)
        {
            var baseOffset = start + (int)ReadU(toc, start + 4 + 4 * index);
            var componentCount = (int)ReadULong(toc, baseOffset + 328);
            IasBinary.Require(componentCount <= 16, "component count");
            var components = new List<(uint, uint, uint, int)>(componentCount);
            var cursor = 0;
            for (var component = 0; component < componentCount; component++)
            {
                var offset = baseOffset + 8 + 20 * component;
                var kind = ReadU(toc, offset);
                var format = ReadU(toc, offset + 4);
                var componentIndex = ReadU(toc, offset + 8);
                if (Version is 10800436 or 10800437 or 1 && format > 16) format += 4;
                IasBinary.Require(Formats.ContainsKey(format), "unsupported vertex format " + format);
                components.Add((kind, format, componentIndex, cursor));
                cursor += Formats[format].Size;
            }
            var vertexCount = (int)ReadU(toc, baseOffset + 352);
            var stride = (int)ReadU(toc, baseOffset + 356);
            var indexCount = (int)ReadU(toc, baseOffset + 392);
            var indexType = ReadU(toc, baseOffset + 396);
            var vertexBase = (int)ReadU(toc, baseOffset + 416);
            var vertexBytes = (int)ReadU(toc, baseOffset + 420);
            var indexBase = (int)ReadU(toc, baseOffset + 424);
            var indexBytes = (int)ReadU(toc, baseOffset + 428);
            IasBinary.Require(cursor <= stride && indexType is 0 or 1, "vertex/index stream schema");
            IasBinary.Require(vertexBase + vertexBytes <= gpu.Length && indexBase + indexBytes <= gpu.Length, "GPU stream out of bounds");
            Streams.Add(new IasUnitStream(components, vertexCount, stride, vertexBase, vertexBytes, indexBase, indexBytes,
                indexCount, indexType == 1 ? 4 : 2));
        }
        var materialsOffset = (int)ReadU(toc, 112);
        count = (int)ReadU(toc, materialsOffset);
        var sections = new uint[count];
        for (var index = 0; index < count; index++) sections[index] = ReadU(toc, materialsOffset + 4 + 4 * index);
        var materialIds = new ulong[count];
        for (var index = 0; index < count; index++) materialIds[index] = ReadULong(toc, materialsOffset + 4 + 4 * count + 8 * index);
        Materials = sections.Zip(materialIds, (section, material) => (section, material)).ToList();
        MaterialMap = new Dictionary<uint, ulong>();
        foreach (var (section, material) in Materials) MaterialMap.TryAdd(section, material);
        start = (int)ReadU(toc, 100);
        count = (int)ReadU(toc, start);
        IasBinary.Require(count is >= 1 and <= 2048, "mesh count");
        Meshes = new List<IasUnitMesh>(count);
        for (var meshIndex = 0; meshIndex < count; meshIndex++)
        {
            var baseOffset = start + (int)ReadU(toc, start + 4 + 4 * meshIndex);
            var transform = ReadU(toc, baseOffset + 48);
            var lod = ReadI(toc, baseOffset + 56);
            var stream = ReadU(toc, baseOffset + 60);
            var materialCount = (int)ReadU(toc, baseOffset + 104);
            var materialOffset = (int)ReadU(toc, baseOffset + 108);
            var sectionCount = (int)ReadU(toc, baseOffset + 120);
            var sectionOffset = (int)ReadU(toc, baseOffset + 124);
            IasBinary.Require(materialCount <= 1024 && sectionCount <= 1024 && stream < Streams.Count && transform < Graph.Count, "mesh schema");
            var meshMaterials = new uint[materialCount];
            for (var index = 0; index < materialCount; index++) meshMaterials[index] = ReadU(toc, baseOffset + materialOffset + 4 * index);
            var sectionRows = new List<IasUnitSection>(sectionCount);
            var allFaces = new List<(uint, uint, uint)>();
            var descriptor = Streams[(int)stream];
            for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
            {
                var offset = baseOffset + sectionOffset + 24 * sectionIndex;
                var materialIndex = (int)ReadU(toc, offset);
                var vertexOffset = ReadU(toc, offset + 4);
                var verticesDeclared = ReadU(toc, offset + 8);
                var indexOffset = (int)ReadU(toc, offset + 12);
                var indexCount = ReadU(toc, offset + 16);
                var group = ReadU(toc, offset + 20);
                IasBinary.Require(materialIndex < materialCount && indexCount % 3 == 0, "mesh section schema");
                var gpuOffset = descriptor.IndexBase + indexOffset * descriptor.IndexStride;
                IasBinary.Require(indexCount <= int.MaxValue / 3, "mesh section too large");
                var faces = new List<(uint, uint, uint)>((int)indexCount / 3);
                for (var face = 0; face < indexCount / 3; face++)
                {
                    var faceOffset = gpuOffset + face * 3 * descriptor.IndexStride;
                    IasBinary.Require(faceOffset + 3 * descriptor.IndexStride <= gpu.Length, "mesh index stream truncated");
                    faces.Add(descriptor.IndexStride == 4
                        ? (ReadU(gpu, faceOffset), ReadU(gpu, faceOffset + 4), ReadU(gpu, faceOffset + 8))
                        : (ReadU16(gpu, faceOffset), ReadU16(gpu, faceOffset + 2), ReadU16(gpu, faceOffset + 4)));
                }
                sectionRows.Add(new IasUnitSection(sectionIndex, meshMaterials[materialIndex],
                    MaterialMap.TryGetValue(meshMaterials[materialIndex], out var material) ? material : null,
                    vertexOffset, verticesDeclared, gpuOffset, indexCount, group, faces));
                allFaces.AddRange(faces);
            }
            IasBinary.Require(sectionRows.Count > 0, "mesh without sections");
            var vertexCount = (allFaces.Count == 0 ? 0 : (int)allFaces.Max(face => Math.Max(face.Item1, Math.Max(face.Item2, face.Item3)))) + 1;
            var vertexStart = descriptor.VertexBase + (int)sectionRows[0].VertexOffset * descriptor.Stride;
            IasBinary.Require(vertexStart + vertexCount * descriptor.Stride <= descriptor.VertexBase + descriptor.VertexBytes, "mesh vertex range");
            var components = descriptor.Components.Where(component => component.Kind is 0 or 6 or 7).ToList();
            IasBinary.Require(components.Count(component => component.Kind == 0) == 1, "position component schema");
            var culling = sectionRows.All(section => section.Material is null);
            var rigid = !components.Any(component => component.Kind is 6 or 7);
            Meshes.Add(new IasUnitMesh(meshIndex, transform, lod, stream, sectionRows, vertexStart, vertexCount,
                vertexCount * descriptor.Stride, rigid, culling, components.Select(component => (component.Kind, component.Index)).ToList()));
        }
        VertexReader = new IasVertexReader(resource.Gpu, Streams);
    }

    public string Id { get; }
    public IasResource Resource { get; }
    public bool AllowUnnamed { get; }
    public IReadOnlyList<ulong> Refs { get; }
    public IReadOnlyList<IasSceneNode> Graph { get; }
    public uint Version { get; }
    public IReadOnlyDictionary<string, string> Customization { get; }
    public IReadOnlyList<IasUnitBone> Bones { get; }
    public List<IasUnitStream> Streams { get; }
    public IReadOnlyList<(uint Section, ulong Material)> Materials { get; }
    public Dictionary<uint, ulong> MaterialMap { get; }
    public List<IasUnitMesh> Meshes { get; }
    public IasVertexReader VertexReader { get; }
    public Dictionary<int, int> RigidDrivers { get; } = new();

    // 顶点级分类在同一次编译里会被 Geometry 与 PartitionLabels 反复访问；
    // 只缓存布局与判定结果，不缓存坐标/权重数组，避免把整份 GPU 再复制一遍。
    private readonly Dictionary<int, SkinLayout> _skinLayouts = new();
    private readonly Dictionary<int, ZeroSkinCheck> _zeroSkinChecks = new();

    private sealed record SkinLayout(int[] IndexKeys, int[] WeightKeys, (uint Kind, uint Index)[] ExtraIndexKeys);

    private sealed record ZeroSkinCheck(bool Candidate, (uint Kind, uint Index)[] ExtraIndexKeys);

    private SkinLayout SkinLayoutOf(IasUnitMesh mesh)
    {
        if (_skinLayouts.TryGetValue(mesh.Index, out var cached)) return cached;
        var indexKeys = mesh.VertexKeys.Where(key => key.Kind == 6).Select(key => (int)key.Index).ToArray();
        var weightKeys = mesh.VertexKeys.Where(key => key.Kind == 7).Select(key => (int)key.Index).ToArray();
        var extra = mesh.VertexKeys.Where(key => key.Kind == 6 && key.Index != 0).ToArray();
        var layout = new SkinLayout(indexKeys, weightKeys, extra);
        _skinLayouts[mesh.Index] = layout;
        return layout;
    }

    private ZeroSkinCheck ZeroSkinCheckOf(IasUnitMesh mesh)
    {
        if (_zeroSkinChecks.TryGetValue(mesh.Index, out var cached)) return cached;
        var seven = mesh.VertexKeys.Where(key => key.Kind == 7).ToList();
        var candidate = seven is [(7, 0)] && mesh.VertexKeys.Contains((6u, 0u));
        var format = candidate ? VertexReader.FormatOf(mesh, 7, 0) : 0;
        candidate = candidate && format is 3 or 4 or 24 or 28 or 35;
        var result = new ZeroSkinCheck(candidate, candidate ? SkinLayoutOf(mesh).ExtraIndexKeys : []);
        _zeroSkinChecks[mesh.Index] = result;
        return result;
    }

    public static IReadOnlyList<IasSceneNode> ReadGraph(byte[] toc, IReadOnlyDictionary<uint, string> names)
    {
        var start = (int)ReadU(toc, 52);
        var count = (int)ReadU(toc, start);
        IasBinary.Require(count is >= 1 and <= 8192, "scene-node count");
        var localStart = start + 16;
        var worldStart = localStart + 64 * count;
        var parentStart = worldStart + 64 * count;
        var hashStart = parentStart + 4 * count;
        var hashes = new uint[count];
        for (var index = 0; index < count; index++) hashes[index] = ReadU(toc, hashStart + 4 * index);
        IasBinary.Require(hashes[0] == IasHash.Murmur32("StingrayEntityRoot"), "unexpected entity root");
        var rows = new IasSceneNode[count];
        for (var index = 0; index < count; index++)
        {
            var values = IasBinary.ReadFloats(toc, localStart + 64 * index, 16);
            var scale = values[12..15];
            var local = new double[12];
            for (var row = 0; row < 3; row++)
            for (var column = 0; column < 4; column++)
                local[row * 4 + column] = column < 3 ? values[column * 3 + row] * scale[column] : values[9 + row];
            var stored = IasAffine.FromColumnMatrix(IasBinary.ReadFloats(toc, worldStart + 64 * index, 16));
            var parent = ReadU16(toc, parentStart + 4 * index + 2);
            IasBinary.Require(parent < count && (index == 0 || index != parent), "scene parent out of bounds");
            IasBinary.Require(local.Concat(stored).All(double.IsFinite), "non-finite scene matrix");
            rows[index] = new IasSceneNode(index, hashes[index], names.GetValueOrDefault(hashes[index]),
                index == 0 ? 0 : (int)parent, local, stored, [], scale);
        }
        var worlds = new double[count][];
        double[] Visit(int index, HashSet<int> chain)
        {
            if (worlds[index] is not null) return worlds[index];
            IasBinary.Require(chain.Add(index), "scene graph cycle");
            var node = rows[index];
            worlds[index] = index == 0 ? node.Local : IasAffine.Product(Visit(node.Parent, chain), node.Local);
            chain.Remove(index);
            return worlds[index];
        }
        for (var index = 0; index < count; index++) rows[index] = rows[index] with { World = Visit(index, []) };
        return rows;
    }

    public static (IReadOnlyDictionary<uint, string> Names, int Count, IReadOnlyList<uint> Lods) BoneNames(IasResource resource)
    {
        var raw = resource.Toc;
        var count = (int)ReadU(raw, 0);
        var levels = (int)ReadU(raw, 4);
        IasBinary.Require(count is > 0 and <= 8192 && levels <= 64, "external Bones table size");
        var thresholds = IasBinary.ReadFloats(raw, 8, levels);
        var hashes = new uint[count];
        for (var index = 0; index < count; index++) hashes[index] = ReadU(raw, 8 + 4 * levels + 4 * index);
        var lods = new uint[levels];
        for (var index = 0; index < levels; index++) lods[index] = ReadU(raw, 8 + 4 * levels + 4 * count + 4 * index);
        IasBinary.Require(thresholds.All(double.IsFinite) && lods.All(value => value <= count), "external Bones LOD table");
        var namesOffset = 8 + 8 * levels + 4 * count;
        var names = new Dictionary<uint, string>(count);
        var cursor = namesOffset;
        for (var index = 0; index < count; index++)
        {
            var end = Array.IndexOf(raw, (byte)0, cursor);
            IasBinary.Require(end > cursor, "external Bones name count");
            var name = Encoding.UTF8.GetString(raw, cursor, end - cursor);
            cursor = end + 1;
            IasBinary.Require(IasHash.Murmur32(name) == hashes[index], "external Bones name/hash mismatch");
            names[hashes[index]] = name;
        }
        IasBinary.Require(cursor == raw.Length, "external Bones trailing bytes");
        return (names, count, lods.Select(value => value).ToList());
    }

    /// <summary>只统计实际有非退化面的材质，保留零面积但索引不同的面。</summary>
    public static HashSet<ulong> IndexedMaterials(IasResource resource)
    {
        var toc = resource.Toc;
        var gpu = resource.Gpu.Span;
        var materialStart = (int)ReadU(toc, 112);
        var materialCount = (int)ReadU(toc, materialStart);
        var materialSections = new uint[materialCount];
        for (var index = 0; index < materialCount; index++) materialSections[index] = ReadU(toc, materialStart + 4 + 4 * index);
        var materialIds = new ulong[materialCount];
        for (var index = 0; index < materialCount; index++) materialIds[index] = ReadULong(toc, materialStart + 4 + 4 * materialCount + 8 * index);
        var table = new Dictionary<uint, ulong>();
        for (var index = 0; index < materialCount; index++) table.TryAdd(materialSections[index], materialIds[index]);
        var streamStart = (int)ReadU(toc, 92);
        var streamCount = (int)ReadU(toc, streamStart);
        var streams = new (int IndexBase, int Stride)[streamCount];
        for (var index = 0; index < streamCount; index++)
        {
            var baseOffset = streamStart + (int)ReadU(toc, streamStart + 4 + 4 * index);
            var indexType = ReadU(toc, baseOffset + 396);
            IasBinary.Require(indexType is 0 or 1, "index stream schema");
            streams[index] = ((int)ReadU(toc, baseOffset + 424), indexType == 0 ? 2 : 4);
        }
        var meshStart = (int)ReadU(toc, 100);
        var meshCount = (int)ReadU(toc, meshStart);
        var needed = new HashSet<ulong>();
        for (var meshIndex = 0; meshIndex < meshCount; meshIndex++)
        {
            var baseOffset = meshStart + (int)ReadU(toc, meshStart + 4 + 4 * meshIndex);
            var lod = ReadI(toc, baseOffset + 56);
            if (lod < 0) continue;
            var stream = (int)ReadU(toc, baseOffset + 60);
            var materialCount2 = (int)ReadU(toc, baseOffset + 104);
            var materialOffset = (int)ReadU(toc, baseOffset + 108);
            var sectionCount = (int)ReadU(toc, baseOffset + 120);
            var sectionOffset = (int)ReadU(toc, baseOffset + 124);
            var names = new uint[materialCount2];
            for (var index = 0; index < materialCount2; index++) names[index] = ReadU(toc, baseOffset + materialOffset + 4 * index);
            var (indexBase, stride) = streams[stream];
            for (var section = 0; section < sectionCount; section++)
            {
                var offset = baseOffset + sectionOffset + 24 * section;
                var materialIndex = (int)ReadU(toc, offset);
                var indexOffset = (int)ReadU(toc, offset + 12);
                var indexCount = (int)ReadU(toc, offset + 16);
                IasBinary.Require(materialIndex < materialCount2 && indexCount % 3 == 0, "section schema");
                if (!table.TryGetValue(names[materialIndex], out var material)) continue;
                var begin = indexBase + indexOffset * stride;
                var end = begin + indexCount * stride;
                IasBinary.Require(end <= gpu.Length, "section index stream is truncated");
                for (var face = 0; face < indexCount / 3; face++)
                {
                    var faceOffset = begin + face * 3 * stride;
                    var (a, b, c) = stride == 4
                        ? (ReadU(gpu, faceOffset), ReadU(gpu, faceOffset + 4), ReadU(gpu, faceOffset + 8))
                        : (ReadU16(gpu, faceOffset), ReadU16(gpu, faceOffset + 2), ReadU16(gpu, faceOffset + 4));
                    if (a != b && a != c && b != c) { needed.Add(material); break; }
                }
            }
        }
        return needed;
    }

    public static IReadOnlyDictionary<string, string> ReadCustomization(byte[] toc)
    {
        var start = (int)ReadU(toc, 76);
        if (start == 0) return new Dictionary<string, string>();
        var cursor = start + 24;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var name in new[] { "body_type", "slot", "weight", "piece_type" })
            {
                var length = (int)ReadU(toc, cursor);
                cursor += 4;
                IasBinary.Require(cursor + length <= toc.Length, "customization string");
                result[name] = Encoding.UTF8.GetString(toc, cursor, length).Replace("\0", "");
                cursor += length + 12;
            }
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or DecoderFallbackException)
        {
            return new Dictionary<string, string>();
        }
        return result;
    }

    public bool Unskinned(IasUnitMesh mesh) => mesh.Rigid;

    public IReadOnlyList<(int Scene, double Weight, int? Internal, int Palette)> Influences(IasUnitMesh mesh, IasUnitSection section, uint vertex)
    {
        if (Unskinned(mesh))
        {
            var scene = RigidDrivers.GetValueOrDefault((int)mesh.Transform, (int)mesh.Transform);
            IasBinary.Require(Graph[scene].Name is not null || AllowUnnamed,
                Id + ": visible rigid renderer has no declared scene transform name");
            return [(scene, 1.0, null, 0)];
        }
        var layout = SkinLayoutOf(mesh);
        IasBinary.Require(layout.IndexKeys.Length > 0 && layout.IndexKeys[0] == 0, Id + ": unsupported skin-index channels");
        IasBinary.Require(layout.WeightKeys.SequenceEqual([0]), Id + ": unsupported skin-weight channels");
        var weightValues = VertexReader.Read(Resource.Gpu, mesh, vertex, 7, 0);
        var indexValues = VertexReader.Read(Resource.Gpu, mesh, vertex, 6, 0);
        IasBinary.Require(indexValues.Length > 0 && weightValues.Length > 0 && weightValues.Length <= indexValues.Length,
            Id + ": skin weight/index widths do not match");
        foreach (var (kind, index) in layout.ExtraIndexKeys)
        {
            IasBinary.Require(VertexReader.Read(Resource.Gpu, mesh, vertex, kind, index).All(value => value == 0),
                Id + ": nonzero extra skin-index channel " + index);
        }
        IasBinary.Require(mesh.Lod >= 0 && mesh.Lod < Bones.Count, "mesh palette LOD");
        var bones = Bones[mesh.Lod];
        // 分区行末尾的 group 才是骨骼重映射表下标：同一组可以被多个分区共用（例如两种材质共用一套调色板），
        // 用分区序号索引会在分区数大于重映射表数时越界，或静默取到别的调色板。
        IasBinary.Require(section.Group < bones.Remaps.Count,
            $"{Id}: mesh {mesh.Index} section {section.Index} palette group {section.Group} is out of range");
        var remap = bones.Remaps[(int)section.Group];
        var result = new List<(int Scene, double Weight, int? Internal, int Palette)>();
        for (var index = 0; index < Math.Min(indexValues.Length, weightValues.Length); index++)
        {
            var weight = weightValues[index];
            if (weight <= 0) continue;
            var palette = indexValues[index];
            IasBinary.Require(palette == Math.Floor(palette) && (int)palette < remap.Count, "weighted palette index");
            var internalIndex = (int)remap[(int)palette];
            var scene = (int)bones.Real[internalIndex];
            IasBinary.Require(Graph[scene].Name is not null || AllowUnnamed,
                $"{Id}: unknown weighted name hash {Graph[scene].Hash:x8}");
            result.Add((scene, weight, internalIndex, (int)palette));
        }
        IasBinary.Require(result.Count > 0,
            $"{Id}: mesh {mesh.Index}, LOD {mesh.Lod}, vertex {vertex}: indexed render vertex has no positive skin weights");
        return result;
    }

    public IasGeometryStats Geometry()
    {
        var rows = new List<IasMeshGeometry>();
        var weighted = new HashSet<int>();
        var rigidNodes = new HashSet<int>();
        var usedMaterials = new HashSet<ulong>();
        foreach (var mesh in Meshes)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var cache = new Dictionary<(int, uint), (double[] Position, (int Scene, double Weight, int? Internal, int Palette)[] Influences)>();
            var positions = new Dictionary<uint, double[]>();
            var zeroSkin = new Dictionary<uint, bool>();
            var rigid = Unskinned(mesh);
            foreach (var section in mesh.Sections)
            {
                foreach (var face in section.Faces)
                {
                    counts["triangles"] = counts.GetValueOrDefault("triangles") + 1;
                    var distinct = face.A != face.B && face.A != face.C && face.B != face.C;
                    if (distinct) counts["distinct_index_triangles"] = counts.GetValueOrDefault("distinct_index_triangles") + 1;
                    if (Nondegenerate(mesh, face, positions)) counts["nondegenerate_triangles"] = counts.GetValueOrDefault("nondegenerate_triangles") + 1;
                    if (mesh.Culling || mesh.Lod < 0 || !distinct) continue;
                    if (!rigid && ZeroSkinFace(mesh, face, zeroSkin))
                    {
                        counts["collapsed_skin_triangles"] = counts.GetValueOrDefault("collapsed_skin_triangles") + 1;
                        continue;
                    }
                    usedMaterials.Add(section.Material!.Value);
                    if (rigid)
                    {
                        var degenerate = RigidDegenerate(mesh, face, positions);
                    counts["rigid_degenerate_triangles"] = counts.GetValueOrDefault("rigid_degenerate_triangles") + (degenerate ? 1 : 0);
                        if (!degenerate)
                        {
                            counts["animated_triangles"] = counts.GetValueOrDefault("animated_triangles") + 1;
                            rigidNodes.Add((int)mesh.Transform);
                        }
                        else counts["animated_triangles"] = counts.GetValueOrDefault("animated_triangles");
                    }
                    else
                    {
                        var vertices = new[] { face.A, face.B, face.C };
                        var entries = new (double[] Position, (int Scene, double Weight, int? Internal, int Palette)[] Influences)[3];
                        for (var item = 0; item < 3; item++)
                        {
                            var vertex = vertices[item];
                            var key = (section.Index, vertex);
                            if (!cache.TryGetValue(key, out var entry))
                            {
                                var influences = Influences(mesh, section, vertex).ToArray();
                                foreach (var influence in influences) weighted.Add(influence.Scene);
                                if (!positions.TryGetValue(vertex, out var position))
                                {
                                    position = VertexReader.Read(Resource.Gpu, mesh, vertex, 0, 0);
                                    positions[vertex] = position;
                                }
                                entry = (position, influences);
                                cache[key] = entry;
                            }
                            entries[item] = entry;
                        }
                        // 上游签名只比较坐标与 (palette, weight)；这里直接结构比较，避免每个面拼字符串。
                        var sameInfluences = PaletteWeightsEqual(entries[0].Influences, entries[1].Influences)
                                             && PaletteWeightsEqual(entries[1].Influences, entries[2].Influences);
                        var sameComponents = (entries[0].Position[0] == entries[1].Position[0]
                                              && entries[0].Position[0] == entries[2].Position[0] ? 1 : 0)
                                             + (entries[0].Position[1] == entries[1].Position[1]
                                                && entries[0].Position[1] == entries[2].Position[1] ? 1 : 0)
                                             + (entries[0].Position[2] == entries[1].Position[2]
                                                && entries[0].Position[2] == entries[2].Position[2] ? 1 : 0);
                        var sharedLine = sameInfluences && sameComponents >= 2;
                        var allDistinct = !SameVertex(entries[0], entries[1])
                                          && !SameVertex(entries[0], entries[2])
                                          && !SameVertex(entries[1], entries[2]);
                        if (allDistinct && !sharedLine)
                            counts["animated_triangles"] = counts.GetValueOrDefault("animated_triangles") + 1;
                    }
                }
            }
            rows.Add(new IasMeshGeometry(mesh.Lod, mesh.Culling, mesh.VertexCount, counts.GetValueOrDefault("triangles"),
                counts.GetValueOrDefault("distinct_index_triangles"), counts.GetValueOrDefault("nondegenerate_triangles"),
                counts.GetValueOrDefault("animated_triangles"), counts.GetValueOrDefault("collapsed_skin_triangles"),
                counts.GetValueOrDefault("rigid_degenerate_triangles")));
            if (rigid && mesh.Lod >= 0 && !mesh.Culling)
                rows[^1] = rows[^1] with { RigidGeometry = new IasRigidGeometry((int)mesh.Transform,
                    counts.GetValueOrDefault("rigid_degenerate_triangles"), counts.GetValueOrDefault("rigid_degenerate_triangles") == counts.GetValueOrDefault("distinct_index_triangles")) };
        }
        var result = new IasGeometryStats(rows, weighted.Order().ToList(), usedMaterials.Order().ToList(),
            rows.Where(row => !row.CullingBody && row.Lod >= 0).Sum(row => row.AnimatedTriangles));
        if (rigidNodes.Count > 0) result = result with { RigidSceneIndices = rigidNodes.Order().ToList() };
        return result;
    }

    public bool Nondegenerate(IasUnitMesh mesh, (uint A, uint B, uint C) face, Dictionary<uint, double[]>? positions = null)
    {
        if (face.A == face.B || face.A == face.C || face.B == face.C) return false;
        double[] Position(uint index)
        {
            if (positions is not null && positions.TryGetValue(index, out var existing)) return existing;
            var value = VertexReader.Read(Resource.Gpu, mesh, index, 0, 0);
            if (positions is not null) positions[index] = value;
            return value;
        }
        var a = Position(face.A);
        var b = Position(face.B);
        var c = Position(face.C);
        double ab0 = b[0] - a[0], ab1 = b[1] - a[1], ab2 = b[2] - a[2];
        double ac0 = c[0] - a[0], ac1 = c[1] - a[1], ac2 = c[2] - a[2];
        var cross0 = ab1 * ac2 - ab2 * ac1;
        var cross1 = ab2 * ac0 - ab0 * ac2;
        var cross2 = ab0 * ac1 - ab1 * ac0;
        return cross0 * cross0 + cross1 * cross1 + cross2 * cross2 > 4e-24;
    }

    private static bool PaletteWeightsEqual(
        ReadOnlySpan<(int Scene, double Weight, int? Internal, int Palette)> left,
        ReadOnlySpan<(int Scene, double Weight, int? Internal, int Palette)> right)
    {
        if (left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++)
        {
            if (left[index].Palette != right[index].Palette || left[index].Weight != right[index].Weight) return false;
        }
        return true;
    }

    private static bool SameVertex(
        (double[] Position, (int Scene, double Weight, int? Internal, int Palette)[] Influences) left,
        (double[] Position, (int Scene, double Weight, int? Internal, int Palette)[] Influences) right)
        => left.Position[0] == right.Position[0] && left.Position[1] == right.Position[1]
           && left.Position[2] == right.Position[2] && PaletteWeightsEqual(left.Influences, right.Influences);

    public bool RigidDegenerate(IasUnitMesh mesh, (uint A, uint B, uint C) face, Dictionary<uint, double[]> positions)
    {
        if (face.A == face.B || face.A == face.C || face.B == face.C) return true;
        double[] Position(uint index)
        {
            if (!positions.TryGetValue(index, out var value))
            {
                value = VertexReader.Read(Resource.Gpu, mesh, index, 0, 0);
                positions[index] = value;
            }
            return value;
        }
        var a = Position(face.A);
        var b = Position(face.B);
        var c = Position(face.C);
        if (a.Take(3).SequenceEqual(b.Take(3)) || a.Take(3).SequenceEqual(c.Take(3)) || b.Take(3).SequenceEqual(c.Take(3))) return true;
        var ab = new[] { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
        var ac = new[] { c[0] - a[0], c[1] - a[1], c[2] - a[2] };
        return ab[0] * ac[1] == ab[1] * ac[0] && ab[0] * ac[2] == ab[2] * ac[0] && ab[1] * ac[2] == ab[2] * ac[1];
    }

    public bool ZeroSkinFace(IasUnitMesh mesh, (uint A, uint B, uint C) face, Dictionary<uint, bool>? cache = null)
    {
        var check = ZeroSkinCheckOf(mesh);
        if (!check.Candidate) return false;
        foreach (var vertex in new[] { face.A, face.B, face.C })
        {
            if (cache is not null && cache.TryGetValue(vertex, out var known))
            {
                if (!known) return false;
                continue;
            }
            var zero = !VertexReader.Read(Resource.Gpu, mesh, vertex, 7, 0).Any(value => value != 0)
                       && !check.ExtraIndexKeys.Any(key =>
                           VertexReader.Read(Resource.Gpu, mesh, vertex, key.Kind, key.Index).Any(value => value != 0));
            if (cache is not null) cache[vertex] = zero;
            if (!zero) return false;
        }
        return true;
    }

    /// <summary>生成 body/arms 两个 GPU 掩码；未分配给该角色的三角形整体置零。</summary>
    public (IReadOnlyDictionary<string, byte[]> Buffers, IReadOnlyList<Dictionary<string, Dictionary<string, int>>> Stats) PartitionLabels(
        IReadOnlyList<IReadOnlyDictionary<uint, string>?> labelSets, bool omitHead = false, bool buildBuffers = true)
    {
        var original = Graph.ToDictionary(node => node.Hash, node => node.Name);
        var sceneLabels = labelSets.Select(labels => Graph.Select(node => (labels ?? original).GetValueOrDefault(node.Hash)).ToList()).ToList();
        IasBinary.Require(sceneLabels.Count > 0, "partition needs at least one label map");
        var buffers = buildBuffers
            ? new Dictionary<string, byte[]> { ["body"] = Resource.Gpu.Span.ToArray(), ["arms"] = Resource.Gpu.Span.ToArray() }
            : new Dictionary<string, byte[]>();
        var stats = sceneLabels.Select(_ => new Dictionary<string, Dictionary<string, int>>()).ToList();
        var faceRoles = new List<(int Offset, int Stride, string Role)>();
        var selected = new Dictionary<string, int>();
        var arms = new HashSet<string>();
        foreach (var side in new[] { "l", "r" })
        foreach (var name in new[] { "shoulder", "shoulder_twist", "elbow", "hand", "hand_twist" })
            arms.Add($"{side}_{name}");
        foreach (var side in new[] { "l", "r" })
        foreach (var name in new[] { "pinky", "ring", "middle", "index", "thumb" })
        for (var index = 1; index <= 3; index++)
            arms.Add($"{side}_{name}_finger{index}");
        foreach (var mesh in Meshes)
        {
            var counts = stats.Select(stat =>
            {
                var key = mesh.Lod.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!stat.TryGetValue(key, out var counters)) stat[key] = counters = new Dictionary<string, int>(StringComparer.Ordinal);
                return counters;
            }).ToList();
            var cache = new Dictionary<(int, uint), bool[][]>();
            var positions = new Dictionary<uint, double[]>();
            var zeroSkin = new Dictionary<uint, bool>();
            var rigid = Unskinned(mesh);
            foreach (var section in mesh.Sections)
            {
                for (var faceIndex = 0; faceIndex < section.Faces.Count; faceIndex++)
                {
                    var face = section.Faces[faceIndex];
                    var offset = section.IndexOffset + 3 * faceIndex * Streams[(int)mesh.Stream].IndexStride;
                    string role;
                    if (mesh.Culling || mesh.Lod < 0 || face.A == face.B || face.A == face.C || face.B == face.C
                        || (rigid && RigidDegenerate(mesh, face, positions)) || (!rigid && ZeroSkinFace(mesh, face, zeroSkin)))
                    {
                        foreach (var count in counts) count["helper_triangles"] = count.GetValueOrDefault("helper_triangles") + 1;
                        role = "helper";
                    }
                    else
                    {
                        var flags = new List<bool[][]>();
                        foreach (var vertex in new[] { face.A, face.B, face.C })
                        {
                            var key = (section.Index, vertex);
                            if (!cache.TryGetValue(key, out var classified))
                            {
                                var influences = Influences(mesh, section, vertex);
                                classified = new bool[sceneLabels.Count][];
                                for (var labelIndex = 0; labelIndex < sceneLabels.Count; labelIndex++)
                                {
                                    var labels = sceneLabels[labelIndex];
                                    var headNeck = 0.0;
                                    string? dominant = null;
                                    var dominantWeight = double.NegativeInfinity;
                                    for (var item = 0; item < influences.Count; item++)
                                    {
                                        var name = labels[influences[item].Scene];
                                        IasBinary.Require(name is not null, "unresolved partition influence");
                                        if (name is "head" or "neck") headNeck += influences[item].Weight;
                                        // 只对首次出现的名字做主导判定：同名权重求和，且首个最大值优先，与原先字典+稳定排序一致。
                                        var seen = false;
                                        for (var previous = 0; previous < item; previous++)
                                        {
                                            if (labels[influences[previous].Scene] != name) continue;
                                            seen = true;
                                            break;
                                        }
                                        if (seen) continue;
                                        var total = influences[item].Weight;
                                        for (var other = item + 1; other < influences.Count; other++)
                                        {
                                            if (labels[influences[other].Scene] == name) total += influences[other].Weight;
                                        }
                                        if (total > dominantWeight)
                                        {
                                            dominantWeight = total;
                                            dominant = name;
                                        }
                                    }
                                    classified[labelIndex] =
                                    [
                                        headNeck > 0,
                                        dominant is not null && arms.Contains(dominant, StringComparer.Ordinal)
                                    ];
                                }
                                cache[key] = classified;
                            }
                            flags.Add(classified);
                        }
                        var roles = new string[sceneLabels.Count];
                        for (var labelIndex = 0; labelIndex < sceneLabels.Count; labelIndex++)
                        {
                            roles[labelIndex] = omitHead || flags.Any(item => item[labelIndex][0]) ? "head"
                                : flags.All(item => item[labelIndex][1]) ? "arms" : "body";
                        }
                        var visible = Nondegenerate(mesh, face, positions);
                        var distinct = face.A != face.B && face.A != face.C && face.B != face.C;
                        for (var labelIndex = 0; labelIndex < counts.Count; labelIndex++)
                        {
                            var count = counts[labelIndex];
                            var itemRole = roles[labelIndex];
                            count["render_triangles"] = count.GetValueOrDefault("render_triangles") + 1;
                            count["render_nondegenerate"] = count.GetValueOrDefault("render_nondegenerate") + (visible ? 1 : 0);
                            count[itemRole + "_nondegenerate"] = count.GetValueOrDefault(itemRole + "_nondegenerate") + (visible ? 1 : 0);
                            count[itemRole + "_distinct_index"] = count.GetValueOrDefault(itemRole + "_distinct_index") + (distinct ? 1 : 0);
                        }
                        role = roles[0];
                        if (distinct) selected[role] = selected.GetValueOrDefault(role) + 1;
                        if (buildBuffers)
                        {
                            // 未分配给该角色的三角形整体塌缩到首索引；直接从 GPU 读首索引写后两个索引，避免每面复制整面。
                            var stride = Streams[(int)mesh.Stream].IndexStride;
                            var first = Resource.Gpu.Span.Slice(offset, stride);
                            foreach (var (bufferName, buffer) in buffers)
                            {
                                if (bufferName == role) continue;
                                first.CopyTo(buffer.AsSpan(offset + stride, stride));
                                first.CopyTo(buffer.AsSpan(offset + 2 * stride, stride));
                            }
                        }
                    }
                    if (buildBuffers) faceRoles.Add((offset, Streams[(int)mesh.Stream].IndexStride, role));
                }
            }
        }
        var result = buffers.Where(pair => selected.GetValueOrDefault(pair.Key) > 0).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var (role, raw) in result)
        {
            foreach (var stream in Streams)
            {
                IasBinary.Require(raw.AsSpan(stream.VertexBase, stream.VertexBytes).SequenceEqual(Resource.Gpu.Span.Slice(stream.VertexBase, stream.VertexBytes)),
                    "mask changed vertex bytes");
            }
            foreach (var (offset, stride, assigned) in faceRoles)
            {
                var actual = raw.AsSpan(offset, 3 * stride);
                var first = Resource.Gpu.Span.Slice(offset, stride);
                if (assigned == "helper" || assigned == role)
                {
                    IasBinary.Require(actual.SequenceEqual(Resource.Gpu.Span.Slice(offset, 3 * stride)), "mask face mismatch");
                }
                else
                {
                    IasBinary.Require(actual[..stride].SequenceEqual(first)
                                      && actual.Slice(stride, stride).SequenceEqual(first)
                                      && actual.Slice(2 * stride, stride).SequenceEqual(first), "mask face mismatch");
                }
            }
        }
        return (result, stats.Select(stat => stat.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)).ToList());
    }

    private static uint ReadU(byte[] raw, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset));
    private static uint ReadU(ReadOnlySpan<byte> raw, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(raw[offset..]);
    private static ulong ReadULong(byte[] raw, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(offset));
    private static ulong ReadULong(ReadOnlySpan<byte> raw, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(raw[offset..]);
    private static int ReadI(byte[] raw, int offset) => BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(offset));
    private static uint ReadU16(byte[] raw, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(offset));
    private static uint ReadU16(ReadOnlySpan<byte> raw, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(raw[offset..]);
}

public sealed record IasRigidGeometry(int TransformSceneIndex, int AffineDegenerateTriangles, bool AllFacesAffineDegenerate);

public sealed record IasMeshGeometry(int Lod, bool CullingBody, int VertexCount, int Triangles, int DistinctIndexTriangles,
    int NondegenerateTriangles, int AnimatedTriangles, int CollapsedSkinTriangles, int RigidDegenerateTriangles)
{
    public IasRigidGeometry? RigidGeometry { get; init; }
}

public sealed record IasGeometryStats(
    IReadOnlyList<IasMeshGeometry> Meshes,
    IReadOnlyList<int> WeightedSceneIndices,
    IReadOnlyList<ulong> RenderMaterials,
    int AnimatedTriangles)
{
    public IReadOnlyList<int>? RigidSceneIndices { get; init; }
}

/// <summary>按 Unit 流声明读取 GPU 顶点通道；只为几何分类提供只读值。</summary>
public sealed class IasVertexReader(IasPayload gpu, IReadOnlyList<IasUnitStream> streams)
{
    // 同一 Unit 的通道布局固定：顶点级读取非常密集，缓存组件偏移避免每次线性查找。
    private readonly Dictionary<(uint Stream, uint Kind, uint Index), (int Offset, uint Format)> _components = new();

    public uint FormatOf(IasUnitMesh mesh, uint kind, uint index) => Component(mesh, kind, index).Format;

    public double[] Read(IasPayload _, IasUnitMesh mesh, uint vertex, uint kind, uint index) => Read(mesh, vertex, kind, index);

    public double[] ReadStatic(IasUnitMesh mesh, uint vertex, uint kind, uint index) => Read(mesh, vertex, kind, index);

    private (int Offset, uint Format) Component(IasUnitMesh mesh, uint kind, uint index)
    {
        var key = (mesh.Stream, kind, index);
        if (_components.TryGetValue(key, out var found)) return found;
        foreach (var item in streams[(int)mesh.Stream].Components)
        {
            if (item.Kind != kind || item.Index != index) continue;
            found = (item.Offset, item.Format);
            _components[key] = found;
            return found;
        }
        throw new InvalidDataException("模型数据检查：missing vertex component");
    }

    private double[] Read(IasUnitMesh mesh, uint vertex, uint kind, uint index)
    {
        var stream = streams[(int)mesh.Stream];
        var component = Component(mesh, kind, index);
        var offset = mesh.VertexStart + (int)vertex * stream.Stride + component.Offset;
        return Decode(gpu.Span, offset, component.Format);
    }

    private static double[] Decode(ReadOnlySpan<byte> gpu, int offset, uint format)
    {
        switch (format)
        {
            case 0: return [BinaryPrimitives.ReadSingleLittleEndian(gpu.Slice(offset))];
            case 1:
                return
                [
                    BinaryPrimitives.ReadSingleLittleEndian(gpu.Slice(offset)),
                    BinaryPrimitives.ReadSingleLittleEndian(gpu.Slice(offset + 4))
                ];
            case 2:
                return
                [
                    BinaryPrimitives.ReadSingleLittleEndian(gpu.Slice(offset)),
                    BinaryPrimitives.ReadSingleLittleEndian(gpu.Slice(offset + 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(gpu.Slice(offset + 8))
                ];
            case 3:
                return
                [
                    BinaryPrimitives.ReadSingleLittleEndian(gpu.Slice(offset)),
                    BinaryPrimitives.ReadSingleLittleEndian(gpu.Slice(offset + 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(gpu.Slice(offset + 8)),
                    BinaryPrimitives.ReadSingleLittleEndian(gpu.Slice(offset + 12))
                ];
            case 4: return gpu.Slice(offset, 4).ToArray().Select(value => value / 255.0).ToArray();
            case 21: return [BinaryPrimitives.ReadUInt32LittleEndian(gpu.Slice(offset))];
            case 22:
                return
                [
                    BinaryPrimitives.ReadUInt32LittleEndian(gpu.Slice(offset)),
                    BinaryPrimitives.ReadUInt32LittleEndian(gpu.Slice(offset + 4))
                ];
            case 23:
                return
                [
                    BinaryPrimitives.ReadUInt32LittleEndian(gpu.Slice(offset)),
                    BinaryPrimitives.ReadUInt32LittleEndian(gpu.Slice(offset + 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(gpu.Slice(offset + 8))
                ];
            case 24:
                return
                [
                    BinaryPrimitives.ReadUInt32LittleEndian(gpu.Slice(offset)),
                    BinaryPrimitives.ReadUInt32LittleEndian(gpu.Slice(offset + 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(gpu.Slice(offset + 8)),
                    BinaryPrimitives.ReadUInt32LittleEndian(gpu.Slice(offset + 12))
                ];
            case 25: return [gpu[offset]];
            case 26: return [gpu[offset], gpu[offset + 1]];
            case 27: return [gpu[offset], gpu[offset + 1], gpu[offset + 2]];
            case 28: return [gpu[offset], gpu[offset + 1], gpu[offset + 2], gpu[offset + 3]];
            case 29:
            {
                var packed = BinaryPrimitives.ReadUInt32LittleEndian(gpu.Slice(offset));
                return [(packed & 1023) / 1023.0, (packed >> 10 & 1023) / 1023.0, (packed >> 20 & 1023) / 1023.0, 0.0];
            }
            case 30: return [BinaryPrimitives.ReadUInt32LittleEndian(gpu.Slice(offset))];
            case 32: return [(double)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(gpu.Slice(offset)))];
            case 33:
                return
                [
                    (double)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(gpu.Slice(offset))),
                    (double)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(gpu.Slice(offset + 2)))
                ];
            case 34:
                return
                [
                    (double)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(gpu.Slice(offset))),
                    (double)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(gpu.Slice(offset + 2))),
                    (double)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(gpu.Slice(offset + 4)))
                ];
            case 35:
                return
                [
                    (double)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(gpu.Slice(offset))),
                    (double)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(gpu.Slice(offset + 2))),
                    (double)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(gpu.Slice(offset + 4))),
                    (double)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(gpu.Slice(offset + 6)))
                ];
            default: throw new InvalidDataException("模型数据检查：unsupported vertex format " + format);
        }
    }
}
