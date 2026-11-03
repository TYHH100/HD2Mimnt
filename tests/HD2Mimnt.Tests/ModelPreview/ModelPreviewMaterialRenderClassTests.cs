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

    [Fact]
    public async Task Reader_PackedNarBinding_MarksTextureAsMaterialParameter()
    {
        // Nar 的 Alpha 是粗糙度：材质绑定后资源条目必须带上"Alpha 是材质参数"标记，
        // 否则解码按假 alpha 回填，低粗糙度材质会被抹成完全粗糙。
        const ulong albedoTextureId = 0x1111;
        const ulong narTextureId = 0x2222;
        const ulong materialId = 0x3333;
        var reader = new PatchResourceInspectionService();
        var path = CompatibilityFixture.WritePatch(_root, resources:
        [
            UnitWithMaterialSlot(1, materialId),
            new CompatibilityFixture.Resource(materialId, PatchResourceTypeIds.Material, Material(
                0x8F669F365F24594E, // 现代角色/护甲族
                (0xE67AC0C7, albedoTextureId), (0x4C567810, narTextureId))),
            TextureResource(albedoTextureId),
            TextureResource(narTextureId)
        ]);

        var result = await reader.PreviewModelAsync(new DirectoryInfo(_root), [new FileInfo(path)]);
        Assert.Null(result.Error);
        var mesh = Assert.Single(result.Meshes);
        Assert.Equal(albedoTextureId, mesh.ColorTextureId);
        Assert.Equal([narTextureId], mesh.MaterialTextures.Get(ModelPreviewTextureRole.NormalOcclusionRoughness));
        Assert.True(result.Textures.Single(texture => texture.TextureId == narTextureId).AlphaIsMaterialParameter);
        Assert.False(result.Textures.Single(texture => texture.TextureId == albedoTextureId).AlphaIsMaterialParameter);
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

    [Fact]
    public void Reader_FlowingTemplate_ResolvesAlbedoArrayColorAndEmissive()
    {
        var textures = ReadMaterialTextures(0xE2CC05A4988B951D,
            (0x1DB1694E, 0x11), (0xF5C97D31, 0x12), (0x495C3189, 0x13),
            (0x12A0F5C0, 0x14), (0xC2048121, 0x15));

        Assert.Equal(0x11UL, textures.ColorTextureId);
        Assert.Equal([0x14UL], textures.Get(ModelPreviewTextureRole.Emissive));
        Assert.Equal([0x12UL, 0x13UL], textures.Get(ModelPreviewTextureRole.Normal));
        Assert.Equal([0x15UL], textures.Get(ModelPreviewTextureRole.Mask));
        Assert.Equal(ModelPreviewMaterialRenderClass.Translucent,
            ModelPreviewMaterialClassifier.Classify(0xE2CC05A4988B951D, textures));
    }

    [Fact]
    public void Reader_ReticleTemplate_ResolvesReticleColorAndLensEmissive()
    {
        var textures = ReadMaterialTextures(0x2D488B210A54DF9D,
            (0xBDC07044, 0x21), (0x1C8C3930, 0x22), (0x080B4D6F, 0x23), (0x1C121028, 0x24));

        Assert.Equal(0x21UL, textures.ColorTextureId);
        Assert.Equal([0x24UL], textures.Get(ModelPreviewTextureRole.Emissive));
        Assert.Equal([0x22UL], textures.Get(ModelPreviewTextureRole.Mask));
        Assert.Equal([0x23UL], textures.Get(ModelPreviewTextureRole.Normal));
        Assert.Equal(ModelPreviewMaterialRenderClass.Emissive,
            ModelPreviewMaterialClassifier.Classify(0x2D488B210A54DF9D, textures));
    }

    [Fact]
    public void Reader_CutoutScopeTemplate_ResolvesCompositeArrayColor()
    {
        var textures = ReadMaterialTextures(0x6B9F33361B025760,
            (0x632A8B80, 0x31), (0xA17B45A8, 0x32), (0x81D4C49D, 0x33), (0xC2EB8D6E, 0x34),
            (0x30E2D136, 0x35), (0xD2F99D38, 0x36), (0x05A27DD5, 0x37), (0xB281E5F2, 0x38),
            (0x37831285, 0x39), (0x89BBCEC2, 0x3A), (0x7E662968, 0x3B), (0x6834AA9B, 0x3C));

        Assert.Equal(0x32UL, textures.ColorTextureId);
        Assert.Contains(0x3AUL, textures.Get(ModelPreviewTextureRole.Mask));
        Assert.Equal(ModelPreviewMaterialRenderClass.Unlit,
            ModelPreviewMaterialClassifier.Classify(0x6B9F33361B025760, textures));
    }

    [Fact]
    public void Reader_ArmorLutTemplate_ResolvesCamoTilerArrayColor()
    {
        var textures = ReadMaterialTextures(0xF5EBD93181FDF00C,
            (0x0F5FF78D, 0x41), (0xA17B45A8, 0x42), (0x81D4C49D, 0x43), (0xC2EB8D6E, 0x44),
            (0x632A8B80, 0x45), (0x7E662968, 0x46), (0x6834AA9B, 0x47));

        Assert.Equal(0x41UL, textures.ColorTextureId);
        Assert.Contains(0x46UL, textures.Get(ModelPreviewTextureRole.Mask));
        Assert.Equal(ModelPreviewMaterialRenderClass.Opaque,
            ModelPreviewMaterialClassifier.Classify(0xF5EBD93181FDF00C, textures));
    }

    [Fact]
    public void Reader_GlassAndTranslucentFlowingTemplates_HaveNoColorTexture()
    {
        var glass = ReadMaterialTextures(0x84E5DE1CC6B74ADA, (0xDAB4D0EE, 0x51));
        Assert.Null(glass.ColorTextureId);
        Assert.Equal([0x51UL], glass.Get(ModelPreviewTextureRole.Normal));

        var flowing = ReadMaterialTextures(0x110199A9C3401242,
            (0x55C87149, 0x61), (0x001FF6D4, 0x62), (0xFBEF0214, 0x63),
            (0x5A3BC7C0, 0x64), (0x2B33D35F, 0x65));
        Assert.Null(flowing.ColorTextureId);
        Assert.Equal([0x61UL, 0x63UL], flowing.Get(ModelPreviewTextureRole.Mask));
        Assert.Equal([0x64UL, 0x65UL], flowing.Get(ModelPreviewTextureRole.Normal));
    }

    [Fact]
    public void Reader_CharacterArmorTemplate_KeepsPackedNormalOutOfColorSlots()
    {
        // 本体 216 个材质里 137 个用的主模板：AlbedoEmissive + Nar + IlluminateData。
        var textures = ReadMaterialTextures(0x8F669F365F24594E,
            (0xE67AC0C7, 0x71), (0x4C567810, 0x72), (0x90B84A53, 0x73));

        Assert.Equal(0x71UL, textures.ColorTextureId);
        Assert.Equal([0x72UL], textures.Get(ModelPreviewTextureRole.NormalOcclusionRoughness));
        // Nar 的 Alpha 是粗糙度：一旦进入颜色槽，预览会把它当不透明颜色图上色并参与
        // 透明度剔除，低粗糙度区域会被整体抹平。
        Assert.DoesNotContain(0x72UL, textures.Get(ModelPreviewTextureRole.BaseColor));
        // IlluminateData 是灰度蒙版（实测 R=G=B），留在 Unknown：颜色槽只有一张。
        Assert.Equal([0x71UL], textures.Get(ModelPreviewTextureRole.BaseColor));
        Assert.Empty(textures.Get(ModelPreviewTextureRole.Emissive));
    }

    [Theory]
    [InlineData(0x7CA0D044, ModelPreviewTextureRole.Mask)]    // ClearcoatRoughnessAoSpec
    [InlineData(0xA72CB013, ModelPreviewTextureRole.Mask)]    // BloodTiler
    [InlineData(0xE7BD9019, ModelPreviewTextureRole.Mask)]    // SubsurfaceOpacity
    [InlineData(0xDF3EE984, ModelPreviewTextureRole.Normal)]  // DetailNormal1
    [InlineData(0x736A0029, ModelPreviewTextureRole.Normal)]  // WoundNormal
    // Nar 与 NormalXyAoRoughMap：R、G 法线 xy，B 遮蔽，A 粗糙度。
    [InlineData(0x4C567810, ModelPreviewTextureRole.NormalOcclusionRoughness)]
    [InlineData(0x1D57DCF3, ModelPreviewTextureRole.NormalOcclusionRoughness)]
    // NormalXyRoughnessOpacity 的 B 是粗糙度、A 是透明度，与上者布局不同，仍按 Normal 处理。
    [InlineData(0xDAB4D0EE, ModelPreviewTextureRole.Normal)]
    [InlineData(0x90B84A53, ModelPreviewTextureRole.Unknown)] // IlluminateData：数据图，不叠加自发光
    [InlineData(0xC985395A, ModelPreviewTextureRole.Unknown)] // EmissiveColor：疑似调色 LUT，避免整面发亮
    public void Reader_SdkSemanticHashes_MapToExpectedRole(
        uint semanticId, ModelPreviewTextureRole expected)
    {
        var textures = ReadMaterialTextures(0x1234, (semanticId, 0x71));

        Assert.Equal(expected, Assert.Single(textures.Inputs!).Role);
    }

    private static ModelPreviewMaterialTextureSet ReadMaterialTextures(
        ulong parentMaterialId, params (uint Semantic, ulong TextureId)[] inputs)
    {
        var textures = PatchResourceInspectionService.TryReadMaterialTextures(
            Material(parentMaterialId, inputs),
            inputs.Select(static input => input.TextureId).ToHashSet());
        Assert.NotNull(textures);
        return textures!.ToTextureSet();
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
