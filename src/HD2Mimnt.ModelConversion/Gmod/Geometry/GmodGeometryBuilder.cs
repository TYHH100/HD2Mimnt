using System.Numerics;
using HD2Mimnt.ModelConversion.Gmod.Materials;
using HD2Mimnt.ModelConversion.Source;
using HD2Mimnt.ModelConversion.Source.Mdl;
using HD2Mimnt.ModelConversion.Source.Vvd;

namespace HD2Mimnt.ModelConversion.Gmod.Geometry;

/// <summary>
/// 重建得到的全部网格（尚未按 IAS 选项组装）。拆分身体/头盔时先建一次网格，
/// 再按部位过滤组装成各自的 <see cref="GmodGeometryBundle"/>，避免重复解码 VVD。
/// </summary>
public sealed record GmodMeshSet(
    string ModelPath,
    string VtxExtension,
    IReadOnlyList<GmodBone> Bones,
    IReadOnlyList<GmodMesh> Meshes,
    IReadOnlyList<GmodAttachment> Attachments,
    int LodCount,
    int SkinFamilyCount,
    IReadOnlyList<GmodConversionIssue> Issues,
    IReadOnlyDictionary<string, string[]> MeshIdsBySlot);

/// <summary>把 Source MDL/VVD/VTX 重建为与选项一一对应的完整网格集合。</summary>
public static class GmodGeometryBuilder
{
    /// <summary>重建全部 bodygroup 备选 × skin 家族的网格与骨骼，不生成选项。</summary>
    public static GmodMeshSet BuildMeshes(SourceModel model, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(model);
        var issues = new List<GmodConversionIssue>();
        var bones = model.Mdl.Bones.Select(bone => new GmodBone(bone.Index, bone.Name, bone.ParentIndex,
            bone.Position, bone.Rotation, bone.PoseToBone, (uint)bone.Flags, (int)bone.ProceduralType,
            bone.PhysicsBone, bone.SurfaceProp, bone.Jiggle)).ToArray();
        var attachments = model.Mdl.Attachments.Select(attachment => new GmodAttachment(
            attachment.Index, attachment.Name, attachment.Flags, attachment.LocalBone, attachment.LocalMatrix)).ToArray();

        var skinCount = Math.Max(1, model.Mdl.SkinFamilies.Count);
        var meshes = new List<GmodMesh>();
        // Cache one geometry set per (body part, alternative, skin); options reference them by id.
        var meshIds = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var part in model.Mdl.BodyParts)
        {
            foreach (var alternative in part.Models)
            {
                for (var skin = 0; skin < skinCount; skin++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = $"{part.Index}:{alternative.Index}:{skin}";
                    var ids = new List<string>();
                    try
                    {
                        var geometry = model.GetModelGeometry(part.Index, alternative.Index, 0, skin, cancellationToken);
                        foreach (var subMesh in geometry.SubMeshes)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var (vertices, indices) = Compact(geometry.Vertices, subMesh.Indices);
                            var id = $"g{part.Index}m{alternative.Index}s{skin}x{subMesh.MeshIndex}";
                            meshes.Add(new GmodMesh(id, part.Index, alternative.Index, skin, subMesh.MeshIndex,
                                subMesh.MaterialSlot, GmodMaterialBuilder.VmtPath(subMesh.MaterialPath),
                                alternative.Name, vertices, indices));
                            ids.Add(id);
                        }
                    }
                    catch (SourceFormatException error)
                    {
                        issues.Add(new GmodConversionIssue("GeometryReconstructionSkipped", key,
                            $"{error.Message} The original MDL/VVD/VTX bytes are retained verbatim.", GmodIssueSeverity.Warning));
                    }
                    meshIds[key] = ids.ToArray();
                }
            }
        }

        if (model.LodCount > 1)
            issues.Add(new GmodConversionIssue("LodNotReconstructed", model.ModelPath,
                $"LOD0 is reconstructed; LOD1..{model.LodCount - 1} mesh offsets are not verified. All original VVD/VTX bytes are retained verbatim.",
                GmodIssueSeverity.Info));
        if (model.Mdl.SkinFamilies.Count == 0)
            issues.Add(new GmodConversionIssue("NoSkinFamilies", model.ModelPath,
                "The model declares no skin families; a single default skin is emitted.", GmodIssueSeverity.Info));

        return new GmodMeshSet(model.ModelPath, model.VtxExtension, bones, meshes, attachments, model.LodCount,
            skinCount, issues.AsReadOnly(), meshIds.AsReadOnly());
    }

    /// <summary>按选项组装几何包；<paramref name="meshFilter"/> 非空时只保留该范围内的网格（身体/头盔拆分）。</summary>
    public static GmodGeometryBundle Assemble(SourceModel model, GmodMeshSet meshSet,
        IReadOnlyList<GmodSwitchOption> options, IReadOnlySet<string>? meshFilter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(meshSet);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<GmodMesh> meshes = meshFilter is null
            ? meshSet.Meshes
            : meshSet.Meshes.Where(mesh => meshFilter.Contains(mesh.Id)).ToArray();

        var optionGeometry = new List<GmodOptionGeometry>(options.Count);
        foreach (var option in options)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = new List<string>();
            foreach (var part in model.Mdl.BodyParts)
            {
                var choice = option.Choices.TryGetValue($"bodygroup:{part.Index}", out var value) ? value : 0;
                var skin = option.Choices.TryGetValue("skin", out var skinValue) ? skinValue : 0;
                if (choice < 0 || choice >= part.Models.Count) continue;
                foreach (var id in meshSet.MeshIdsBySlot.GetValueOrDefault($"{part.Index}:{choice}:{skin}") ?? [])
                {
                    if (meshFilter is null || meshFilter.Contains(id)) ids.Add(id);
                }
            }
            optionGeometry.Add(new GmodOptionGeometry(option.Id, option.Choices, ids.AsReadOnly()));
        }

        return new GmodGeometryBundle(meshSet.ModelPath, meshSet.VtxExtension, meshSet.Bones,
            meshes, meshSet.Attachments, optionGeometry.AsReadOnly(), meshSet.LodCount,
            meshSet.SkinFamilyCount, meshSet.Issues);
    }

    public static GmodGeometryBundle Build(SourceModel model, IReadOnlyList<GmodSwitchOption> options,
        CancellationToken cancellationToken = default) =>
        Assemble(model, BuildMeshes(model, cancellationToken), options, cancellationToken: cancellationToken);

    private static (GmodVertex[] Vertices, int[] Indices) Compact(VvdVertex[] pool, int[] indices)
    {
        var map = new Dictionary<int, int>();
        var vertices = new List<GmodVertex>();
        var remapped = new int[indices.Length];
        for (var i = 0; i < indices.Length; i++)
        {
            var global = indices[i];
            if (!map.TryGetValue(global, out var local))
            {
                local = vertices.Count;
                map.Add(global, local);
                vertices.Add(Convert(pool[global]));
            }
            remapped[i] = local;
        }
        return (vertices.ToArray(), remapped);
    }

    private static GmodVertex Convert(VvdVertex vertex)
    {
        var count = Math.Min(3, (int)vertex.BoneCount);
        var weights = new float[count];
        var boneIds = new byte[count];
        for (var lane = 0; lane < count; lane++)
        {
            weights[lane] = vertex.Weight(lane);
            boneIds[lane] = vertex.Bone(lane);
        }
        return new GmodVertex(vertex.Position, vertex.Normal, vertex.TexCoord,
            vertex.Tangent ?? Vector4.Zero, weights, boneIds);
    }
}
