using HD2Mimnt.ModelConversion.Source.Mdl;
using HD2Mimnt.ModelConversion.Source.Vtx;
using HD2Mimnt.ModelConversion.Source.Vvd;

namespace HD2Mimnt.ModelConversion.Source;

public sealed record SourceSubMesh(int BodyPartIndex, int ModelIndex, int MeshIndex,
    int MaterialSlot, string MaterialPath, int[] Indices)
{
    public int TriangleCount => Indices.Length / 3;
}

public sealed record SourceModelGeometry(int BodyPartIndex, int ModelIndex, int Lod,
    VvdVertex[] Vertices, IReadOnlyList<SourceSubMesh> SubMeshes)
{
    public int TriangleCount => SubMeshes.Sum(mesh => mesh.TriangleCount);
    public int ReferencedVertexCount() => SubMeshes.SelectMany(mesh => mesh.Indices).Distinct().Count();
}

public sealed class SourceModel
{
    private static readonly string[] VtxExtensions = [".dx90.vtx", ".dx80.vtx", ".sw.vtx"];

    private SourceModel(string modelPath, MdlFile mdl, VvdFile vvd, VtxFile vtx, string vtxExtension)
    {
        ModelPath = modelPath;
        Mdl = mdl;
        Vvd = vvd;
        Vtx = vtx;
        VtxExtension = vtxExtension;
    }

    public string ModelPath { get; }
    public MdlFile Mdl { get; }
    public VvdFile Vvd { get; }
    public VtxFile Vtx { get; }
    public string VtxExtension { get; }
    public IReadOnlyList<string> Warnings => Mdl.Warnings;
    public int LodCount => Math.Min(Vvd.LodCount, Vtx.LodCount);

    public static SourceModel Load(ISourceAssetProvider provider, string mdlPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Gma.GmaArchive.NormalizePath(mdlPath);
        if (!normalized.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            throw new SourceFormatException($"Model path must end with .mdl: {mdlPath}");
        var stem = normalized[..^4];
        if (!provider.TryRead(normalized, out var mdlBytes, cancellationToken))
            throw new SourceFormatException($"MDL not found: {normalized}");
        if (!provider.TryRead(stem + ".vvd", out var vvdBytes, cancellationToken))
            throw new SourceFormatException($"VVD not found: {stem}.vvd");
        foreach (var extension in VtxExtensions)
        {
            if (provider.TryRead(stem + extension, out var vtxBytes, cancellationToken))
                return Load(normalized, mdlBytes, vvdBytes, vtxBytes, extension, cancellationToken);
        }
        throw new SourceFormatException($"No VTX sibling found for {stem}");
    }

    public static SourceModel Load(string modelPath, byte[] mdlBytes, byte[] vvdBytes, byte[] vtxBytes,
        string vtxExtension = ".dx90.vtx", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mdl = MdlFile.Parse(mdlBytes);
        var vvd = VvdFile.Parse(vvdBytes, cancellationToken: cancellationToken);
        var vtx = VtxFile.Parse(vtxBytes, cancellationToken: cancellationToken);
        if (mdl.Header.Checksum != vvd.Checksum || mdl.Header.Checksum != vtx.Checksum)
            throw new SourceFormatException($"Checksum mismatch: MDL={mdl.Header.Checksum}, VVD={vvd.Checksum}, VTX={vtx.Checksum}");
        if (mdl.BodyParts.Count != vtx.BodyParts.Count)
            throw new SourceFormatException($"MDL body parts={mdl.BodyParts.Count}, VTX body parts={vtx.BodyParts.Count}");
        for (var i = 0; i < mdl.BodyParts.Count; i++)
        {
            if (mdl.BodyParts[i].Models.Count != vtx.BodyParts[i].Models.Count)
                throw new SourceFormatException($"MDL/VTX alternative counts differ for body part {i}");
        }
        foreach (var vertex in vvd.Pool)
        {
            for (var lane = 0; lane < vertex.BoneCount; lane++)
            {
                if (vertex.Bone(lane) >= mdl.Bones.Count)
                    throw new SourceFormatException($"VVD bone index {vertex.Bone(lane)} is outside the MDL skeleton");
            }
        }
        return new SourceModel(modelPath, mdl, vvd, vtx, vtxExtension);
    }

    public SourceModelGeometry GetModelGeometry(int bodyPartIndex, int modelIndex, int lod = 0, int skin = 0,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (lod != 0)
            throw new SourceFormatException("Geometry reconstruction supports LOD0 only; lower LOD mesh offsets are not verified");
        if (bodyPartIndex < 0 || bodyPartIndex >= Mdl.BodyParts.Count)
            throw new SourceFormatException($"Body part {bodyPartIndex} is out of range");
        if (skin < 0 || skin >= Math.Max(1, Mdl.SkinFamilies.Count))
            throw new SourceFormatException($"Skin {skin} is unavailable");
        var bodyPart = Mdl.BodyParts[bodyPartIndex];
        if (modelIndex < 0 || modelIndex >= bodyPart.Models.Count)
            throw new SourceFormatException($"Body group {bodyPart.Name} has no alternative {modelIndex}");
        var vertices = Vvd.GetLodVertices(lod);
        var model = bodyPart.Models[modelIndex];
        var subMeshes = new List<SourceSubMesh>();
        var vtxModel = Vtx.BodyParts[bodyPartIndex].Models[modelIndex];
        if (vtxModel.Lods.Count == 0)
        {
            if (model.IsBlank)
                return new SourceModelGeometry(bodyPartIndex, modelIndex, lod, vertices, subMeshes);
            throw new SourceFormatException($"Body group {bodyPart.Name} alternative {model.Name} has no LOD0");
        }
        if (model.VertexByteOffset < 0 || model.VertexByteOffset % VvdFile.VertexSize != 0)
            throw new SourceFormatException($"Model {model.Name} has an invalid VVD byte offset");
        var modelBase = model.VertexByteOffset / VvdFile.VertexSize;
        var vtxLod = vtxModel.Lods[lod];
        if (model.Meshes.Count != vtxLod.Meshes.Count)
            throw new SourceFormatException($"MDL/VTX mesh counts differ for {bodyPart.Name}/{model.Name}");
        for (var meshIndex = 0; meshIndex < model.Meshes.Count; meshIndex++)
        {
            var mesh = model.Meshes[meshIndex];
            if (mesh.VertexCount < 0 || mesh.VertexOffset < 0 ||
                (long)mesh.VertexOffset + mesh.VertexCount > model.VertexCount)
                throw new SourceFormatException($"Mesh {meshIndex} exceeds its model vertex range");
            var indices = new List<int>();
            foreach (var (a, b, c) in vtxLod.Meshes[meshIndex].EnumerateTriangles(cancellationToken))
            {
                indices.Add(MapVertex(a));
                indices.Add(MapVertex(b));
                indices.Add(MapVertex(c));
            }
            subMeshes.Add(new SourceSubMesh(bodyPartIndex, modelIndex, meshIndex,
                mesh.MaterialIndex, Mdl.ResolveMaterialPath(mesh.MaterialIndex, skin), indices.ToArray()));

            int MapVertex(int local)
            {
                var global = (long)modelBase + mesh.VertexOffset + local;
                if (local >= mesh.VertexCount || global < 0 || global >= vertices.Length)
                    throw new SourceFormatException($"VTX vertex {local} maps outside mesh/VVD range");
                return (int)global;
            }
        }
        return new SourceModelGeometry(bodyPartIndex, modelIndex, lod, vertices, subMeshes.AsReadOnly());
    }

    public IReadOnlyList<SourceModelGeometry> GetGeometry(IReadOnlyDictionary<int, int>? bodyGroupChoices = null,
        int lod = 0, int skin = 0, CancellationToken cancellationToken = default)
    {
        if (bodyGroupChoices?.Keys.Any(index => index < 0 || index >= Mdl.BodyParts.Count) == true)
            throw new SourceFormatException("Unknown body group selection");
        var result = new List<SourceModelGeometry>();
        foreach (var part in Mdl.BodyParts)
        {
            var choice = bodyGroupChoices is not null && bodyGroupChoices.TryGetValue(part.Index, out var value) ? value : 0;
            if (part.Models.Count == 0) continue;
            result.Add(GetModelGeometry(part.Index, choice, lod, skin, cancellationToken));
        }
        return result.AsReadOnly();
    }

    public IReadOnlyList<SourceModelGeometry> GetAllAlternatives(CancellationToken cancellationToken = default)
    {
        var result = new List<SourceModelGeometry>();
        foreach (var part in Mdl.BodyParts)
        foreach (var model in part.Models)
            result.Add(GetModelGeometry(part.Index, model.Index, cancellationToken: cancellationToken));
        return result.AsReadOnly();
    }
}
