using System.Buffers.Binary;
using HD2Mimnt.Core.ModelPreview;
using HD2Mimnt.Infrastructure.ModelPreview;
using HD2Mimnt.Tests.Services;

namespace HD2Mimnt.Tests.ModelPreview;

public sealed class ModelPreviewMaterialRenderClassTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-PreviewMaterial-" + Guid.NewGuid().ToString("N"));

    public ModelPreviewMaterialRenderClassTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(0xD3701FC725106C09, ModelPreviewMaterialRenderClass.Emissive)]   // CE emissive / AQ bloom
    [InlineData(0x2D488B210A54DF9D, ModelPreviewMaterialRenderClass.Emissive)]   // AQ reticle
    [InlineData(0x84E5DE1CC6B74ADA, ModelPreviewMaterialRenderClass.Translucent)] // CE translucent / AQ glass
    [InlineData(0xE2CC05A4988B951D, ModelPreviewMaterialRenderClass.Translucent)] // AQ flowing
    [InlineData(0x110199A9C3401242, ModelPreviewMaterialRenderClass.Translucent)] // AQ translucent_flowing
    [InlineData(0x4CE07F2F0F432035, ModelPreviewMaterialRenderClass.Unlit)]      // CE scope
    [InlineData(0x6B9F33361B025760, ModelPreviewMaterialRenderClass.Unlit)]      // AQ cutout_scope
    [InlineData(0xEF9B3FC2FC1BA61E, ModelPreviewMaterialRenderClass.Opaque)]     // advanced 族
    [InlineData(0xD51D2A873865D4E5, ModelPreviewMaterialRenderClass.Opaque)]     // basic
    [InlineData(0x7712EE7808E0EC50, ModelPreviewMaterialRenderClass.Opaque)]     // 现代 basic+
    [InlineData(0x2F9C50D9129837FE, ModelPreviewMaterialRenderClass.Opaque)]     // 旧 basic+
    [InlineData(0xD84D04634A1E2F60, ModelPreviewMaterialRenderClass.Opaque)]     // alphaclip
    [InlineData(0xC6042E3403385D40, ModelPreviewMaterialRenderClass.Opaque)]     // alphaclip+（现代护甲）
    [InlineData(0xF5EBD93181FDF00C, ModelPreviewMaterialRenderClass.Opaque)]     // armorlut
    [InlineData(0x54AE9CE1A8FAFE8B, ModelPreviewMaterialRenderClass.Opaque)]     // original（CE）
    [InlineData(0x8F669F365F24594E, ModelPreviewMaterialRenderClass.Opaque)]     // original（AQ）
    public void Classify_KnownParentMaterialFingerprints_MapToRenderClass(
        ulong parentMaterialId, ModelPreviewMaterialRenderClass expected)
    {
        Assert.Equal(expected, ModelPreviewMaterialClassifier.Classify(parentMaterialId, null));
    }

    [Fact]
    public void Classify_UnknownParentWithEmissiveOnlyInput_IsEmissive()
    {
        var textures = TexturesByRole((ModelPreviewTextureRole.Emissive, 0x2));
        Assert.Equal(ModelPreviewMaterialRenderClass.Emissive,
            ModelPreviewMaterialClassifier.Classify(0x1234, textures));
    }

    [Fact]
    public void Classify_UnknownParentWithBaseColor_StaysOpaqueEvenWithEmissiveInput()
    {
        var textures = TexturesByRole(
            (ModelPreviewTextureRole.BaseColor, 0x1), (ModelPreviewTextureRole.Emissive, 0x2));
        Assert.Equal(ModelPreviewMaterialRenderClass.Opaque,
            ModelPreviewMaterialClassifier.Classify(0x1234, textures));
        Assert.Equal(ModelPreviewMaterialRenderClass.Opaque,
            ModelPreviewMaterialClassifier.Classify(null, textures));
    }

    [Fact]
    public void Classify_MissingParentAndNoTextures_FallsBackToOpaque()
    {
        Assert.Equal(ModelPreviewMaterialRenderClass.Opaque,
            ModelPreviewMaterialClassifier.Classify(null, null));
        Assert.Equal(ModelPreviewMaterialRenderClass.Opaque,
            ModelPreviewMaterialClassifier.Classify(null, ModelPreviewMaterialTextureSet.Empty));
    }

    [Fact]
    public async Task Reader_MaterialParentFingerprintAssignsRenderClassAndEmissiveInput()
    {
        const ulong albedoTextureId = 0x1111;
        const ulong emissiveTextureId = 0x2222;
        const ulong materialId = 0x3333;
        var reader = new PatchResourceInspectionService();
        var path = CompatibilityFixture.WritePatch(_root, resources:
        [
            UnitWithMaterialSlot(1, materialId),
            new CompatibilityFixture.Resource(materialId, PatchResourceTypeIds.Material, Material(
                0x84E5DE1CC6B74ADA, // glass/translucent 族
                (0xAC652E43, albedoTextureId), (0xCA6F2CF1, emissiveTextureId))),
            TextureResource(albedoTextureId),
            TextureResource(emissiveTextureId)
        ]);

        var result = await reader.PreviewModelAsync(new DirectoryInfo(_root), [new FileInfo(path)]);
        Assert.Null(result.Error);
        var mesh = Assert.Single(result.Meshes);
        Assert.Equal(ModelPreviewMaterialRenderClass.Translucent, mesh.RenderClass);
        Assert.Equal(materialId, mesh.MaterialId);
        Assert.Equal(albedoTextureId, mesh.ColorTextureId);
        Assert.Equal([emissiveTextureId], mesh.MaterialTextures.Get(ModelPreviewTextureRole.Emissive));
    }

    [Theory]
    [InlineData(0xD3701FC725106C09, ModelPreviewMaterialRenderClass.Emissive)]
    [InlineData(0xC6042E3403385D40, ModelPreviewMaterialRenderClass.Opaque)] // 现代护甲带自发光输入仍按受光照渲染
    public async Task Reader_EmissiveFamilyParents_DriveSectionRenderClass(
        ulong parentMaterialId, ModelPreviewMaterialRenderClass expected)
    {
        const ulong albedoTextureId = 0x1111;
        const ulong materialId = 0x3333;
        var reader = new PatchResourceInspectionService();
        var path = CompatibilityFixture.WritePatch(_root, resources:
        [
            UnitWithMaterialSlot(1, materialId),
            new CompatibilityFixture.Resource(materialId, PatchResourceTypeIds.Material,
                Material(parentMaterialId, (0xAC652E43, albedoTextureId))),
            TextureResource(albedoTextureId)
        ]);

        var result = await reader.PreviewModelAsync(new DirectoryInfo(_root), [new FileInfo(path)]);
        Assert.Null(result.Error);
        Assert.Equal(expected, Assert.Single(result.Meshes).RenderClass);
    }

    [Fact]
    public async Task Reader_UnitWithoutMaterialTables_KeepsOpaqueFallback()
    {
        var reader = new PatchResourceInspectionService();
        var path = CompatibilityFixture.WritePatch(_root, resources: [TriangleUnit(1, 0)]);
        var result = await reader.PreviewModelAsync(new DirectoryInfo(_root), [new FileInfo(path)]);
        Assert.Null(result.Error);
        Assert.Equal(ModelPreviewMaterialRenderClass.Opaque, Assert.Single(result.Meshes).RenderClass);
    }

    private static ModelPreviewMaterialTextureSet TexturesByRole(
        params (ModelPreviewTextureRole Role, ulong TextureId)[] inputs)
    {
        var byRole = inputs.GroupBy(static input => input.Role).ToDictionary(
            static group => group.Key,
            static group => (IReadOnlyList<ulong>)group.Select(static input => input.TextureId).ToArray());
        return new ModelPreviewMaterialTextureSet(
            byRole, inputs.Select(static input => input.TextureId).ToArray(),
            inputs.FirstOrDefault(static input => input.Role == ModelPreviewTextureRole.BaseColor).TextureId is { } color
                ? color
                : null);
    }

    /// <summary>与 SDK material 资源布局一致：0x18 父材质 ID，0x40 纹理数，0x88 语义表后接纹理 ID 平行表。</summary>
    private static byte[] Material(ulong parentMaterialId, params (uint Semantic, ulong TextureId)[] inputs)
    {
        var data = new byte[0x88 + inputs.Length * 4 + inputs.Length * 8];
        CompatibilityFixture.Put64(data, 0x18, parentMaterialId);
        CompatibilityFixture.Put32(data, 0x40, (uint)inputs.Length);
        for (var index = 0; index < inputs.Length; index++)
            CompatibilityFixture.Put32(data, 0x88 + index * 4, inputs[index].Semantic);
        var textureIdsOffset = 0x88 + inputs.Length * 4;
        for (var index = 0; index < inputs.Length; index++)
            CompatibilityFixture.Put64(data, textureIdsOffset + index * 8, inputs[index].TextureId);
        return data;
    }

    /// <summary>嵌入 PNG 头（仅 24 字节签名+IHDR 尺寸）的纹理资源，满足 TOC 纹理元数据收集。</summary>
    private static CompatibilityFixture.Resource TextureResource(ulong textureId)
    {
        var payload = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(payload, 0);
        CompatibilityFixture.Put32(payload, 8, 13);
        new byte[] { 0x49, 0x48, 0x44, 0x52 }.CopyTo(payload, 12);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(16), 1);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(20), 1);
        return new(textureId, PatchResourceTypeIds.Texture, [], payload);
    }

    /// <summary>在 GpuUnit 基础上追加 MeshInfo 与 Unit 材质表，使三角形 section 绑定材质槽。</summary>
    private static CompatibilityFixture.Resource UnitWithMaterialSlot(ulong unitId, ulong materialId)
    {
        var resource = TriangleUnit(unitId, 0);
        var data = resource.Data;
        var materialsOffset = data.Length;
        var meshInfoOffset = materialsOffset + 16; // count(4) + slot(4) + materialId(8)
        var meshEntryOffset = meshInfoOffset + 8;  // count(4) + 1 个相对偏移(4)
        var slotsOffset = meshEntryOffset + 128;
        var sectionOffset = slotsOffset + 4;

        var extended = new byte[sectionOffset + 24];
        data.CopyTo(extended, 0);
        CompatibilityFixture.Put32(extended, 0x60, (uint)extended.Length - 8);
        CompatibilityFixture.Put32(extended, 0x64, (uint)meshInfoOffset);
        CompatibilityFixture.Put32(extended, 0x70, (uint)materialsOffset);

        // Unit 材质表：槽位 0 → materialId。
        CompatibilityFixture.Put32(extended, materialsOffset, 1);
        CompatibilityFixture.Put32(extended, materialsOffset + 4, 0);
        CompatibilityFixture.Put64(extended, materialsOffset + 8, materialId);

        // MeshInfo 表：1 个 mesh，相对偏移 8 指向 mesh 条目。
        CompatibilityFixture.Put32(extended, meshInfoOffset, 1);
        CompatibilityFixture.Put32(extended, meshInfoOffset + 4, 8);
        CompatibilityFixture.Put32(extended, meshEntryOffset + 48, unchecked((uint)-1)); // transformIndex
        CompatibilityFixture.Put32(extended, meshEntryOffset + 56, 0);             // lodIndex
        CompatibilityFixture.Put32(extended, meshEntryOffset + 60, 0);             // streamIndex
        CompatibilityFixture.Put32(extended, meshEntryOffset + 104, 1);            // materialCount
        CompatibilityFixture.Put32(extended, meshEntryOffset + 108, (uint)(slotsOffset - meshEntryOffset));
        CompatibilityFixture.Put32(extended, meshEntryOffset + 120, 1);            // sectionCount
        CompatibilityFixture.Put32(extended, meshEntryOffset + 124, (uint)(sectionOffset - meshEntryOffset));

        // 材质槽数组与 section：materialIndex 0，覆盖整段三角形（顶点 0-2，索引 0-2）。
        CompatibilityFixture.Put32(extended, slotsOffset, 0);
        CompatibilityFixture.Put32(extended, sectionOffset + 0, 0); // materialIndex
        CompatibilityFixture.Put32(extended, sectionOffset + 4, 0); // vertexOffset
        CompatibilityFixture.Put32(extended, sectionOffset + 8, 3); // vertexCount
        CompatibilityFixture.Put32(extended, sectionOffset + 12, 0); // indexOffset
        CompatibilityFixture.Put32(extended, sectionOffset + 16, 3); // indexCount

        return resource with { Data = extended };
    }

    private static CompatibilityFixture.Resource TriangleUnit(ulong id, float offsetX)
    {
        var resource = CompatibilityFixture.GpuUnit();
        const int streamInfo = 0x98;
        const int vertexBytes = 3 * 3 * sizeof(float);
        CompatibilityFixture.Put32(resource.Data, streamInfo + 0x160, 3);
        CompatibilityFixture.Put32(resource.Data, streamInfo + 0x1a4, vertexBytes);
        CompatibilityFixture.Put32(resource.Data, streamInfo + 0x1a8, vertexBytes);
        var gpu = new byte[vertexBytes + 6];
        float[] positions = [offsetX, 0, 0, offsetX + 1, 0, 0, offsetX, 1, 0];
        for (var index = 0; index < positions.Length; index++)
            CompatibilityFixture.Put32(gpu, index * sizeof(float), BitConverter.SingleToUInt32Bits(positions[index]));
        new byte[] { 0, 0, 1, 0, 2, 0 }.CopyTo(gpu, vertexBytes);
        return resource with { Id = id, Gpu = gpu };
    }

    public void Dispose() => Directory.Delete(_root, true);
}
