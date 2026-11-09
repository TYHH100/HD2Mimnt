namespace HD2Mimnt.Core.ModelPreview;

/// <summary>
/// Selects one material variant for a shared section without conflating the Unit's
/// index-buffer storage offset with its triangle geometry.
/// </summary>
public static class ModelPreviewMaterialVariantSelector
{
    public static IReadOnlyList<ModelPreviewMesh> SelectPreferredVariants(
        IReadOnlyList<ModelPreviewMesh> meshes,
        Func<ulong, long> getColorTexturePixels)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentNullException.ThrowIfNull(getColorTexturePixels);

        var selected = new List<ModelPreviewMesh>(meshes.Count);
        foreach (var group in meshes
                     .Select(static (mesh, index) => (mesh, index))
                     .GroupBy(static item => new VariantGeometryKey(
                         item.mesh.PatchFile,
                         item.mesh.UnitId,
                         item.mesh.StreamIndex,
                         item.mesh.MeshInfoIndex,
                         item.mesh.SourceVertexOffset,
                         item.mesh.SourceVertexCount,
                         item.mesh.SourceIndexCount)))
        {
            if (group.Key.MeshInfoIndex < 0 || group.Count() == 1)
            {
                selected.AddRange(group.Select(static item => item.mesh));
                continue;
            }

            // Material variants share MeshInfo/vertex range/index count but frequently
            // point at different index-buffer offsets. IndexOffset is deliberately not
            // part of VariantGeometryKey: it identifies storage, not geometry.
            selected.Add(group
                .OrderByDescending(item => item.mesh.ColorTextureId is ulong textureId
                    ? getColorTexturePixels(textureId)
                    : 0)
                .ThenByDescending(static item => item.mesh.ColorTextureId.HasValue)
                .ThenBy(static item => item.index)
                .First()
                .mesh);
        }

        return selected;
    }

    public static IReadOnlyList<ModelPreviewMesh> FilterPureBlackPlaceholders(
        IReadOnlyList<ModelPreviewMesh> meshes,
        IReadOnlySet<ulong> pureBlackBaseColorTextureIds)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentNullException.ThrowIfNull(pureBlackBaseColorTextureIds);
        if (pureBlackBaseColorTextureIds.Count == 0)
            return meshes;

        var selected = new List<ModelPreviewMesh>(meshes.Count);
        foreach (var stream in meshes.GroupBy(static mesh => (mesh.PatchFile, mesh.UnitId, mesh.StreamIndex)))
        {
            // A black material remains valid when it is this stream's only color source;
            // otherwise it is the known low-resolution placeholder companion.
            var hasNonBlackSection = stream.Any(mesh =>
                mesh.MaterialId.HasValue &&
                mesh.ColorTextureId is ulong colorTextureId &&
                !pureBlackBaseColorTextureIds.Contains(colorTextureId));
            foreach (var mesh in stream)
            {
                var isPlaceholder = mesh.MaterialId.HasValue &&
                    mesh.ColorTextureId is ulong colorTextureId &&
                    pureBlackBaseColorTextureIds.Contains(colorTextureId);
                if (!hasNonBlackSection || !isPlaceholder)
                    selected.Add(mesh);
            }
        }

        return selected;
    }

    /// <summary>
    /// Decoded-BGRA check for pure-black placeholder textures (duplicate variant sections
    /// some mod exports ship with a black color map). RGB must be exactly zero; the alpha
    /// check tolerates BCn endpoint quantization because re-encoded placeholders decode to
    /// 231–254 instead of 255 (measured), while real transparency/cutout planes keep low or
    /// mixed alpha values and never pass.
    /// </summary>
    public static bool IsOpaqueBgraPureBlack(ReadOnlySpan<byte> pixels)
    {
        const byte opaqueAlphaThreshold = 230;
        if (pixels.Length < 4 || pixels.Length % 4 != 0)
            return false;

        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset] != 0 || pixels[offset + 1] != 0 || pixels[offset + 2] != 0 ||
                pixels[offset + 3] < opaqueAlphaThreshold)
                return false;
        }

        return true;
    }

    private readonly record struct VariantGeometryKey(
        string PatchFile,
        ulong UnitId,
        int StreamIndex,
        int MeshInfoIndex,
        uint VertexOffset,
        uint VertexCount,
        uint IndexCount);
}
