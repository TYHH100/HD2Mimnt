using System.Numerics;
using HD2Mimnt.Core.ModelPreview;

namespace HD2Mimnt.Tests.ModelPreview;

public sealed class ModelPreviewPackTests
{
    [Fact]
    public void RoundTrip_PreservesMeshesTexturesAndSkinning()
    {
        var skeleton = new ModelPreviewSkeleton
        {
            BonesId = 0x1111,
            StateMachineId = 0x2222,
            Bones =
            [
                new ModelPreviewSkeletonBone(-1, 0xAAAA, Matrix4x4.Identity),
                new ModelPreviewSkeletonBone(0, 0xBBBB, Matrix4x4.CreateTranslation(1, 2, 3))
            ]
        };
        var materialTextures = new ModelPreviewMaterialTextureSet(
            new Dictionary<ModelPreviewTextureRole, IReadOnlyList<ulong>>
            {
                [ModelPreviewTextureRole.BaseColor] = [0x10, 0x20],
                [ModelPreviewTextureRole.Emissive] = [0x30]
            },
            [0x10, 0x20, 0x30],
            0x10,
            [new ModelPreviewMaterialInput(7, 0x10, ModelPreviewTextureRole.BaseColor)],
            ColorAlphaIsMetallic: true);

        var mesh = new ModelPreviewMesh
        {
            PatchFile = "body/a.patch_0",
            UnitId = 0x1234,
            StreamIndex = 3,
            MeshInfoIndex = 5,
            SourceVertexOffset = 6,
            SourceVertexCount = 4,
            SourceIndexOffset = 7,
            SourceIndexCount = 6,
            BodyShape = ModelPreviewBodyShape.Stocky,
            CustomizationSlot = ModelPreviewCustomizationSlot.Torso,
            Positions = [0f, 1f, 2f, 3f, 4f, 5f],
            Normals = [0f, 0f, 1f, 0f, 0f, 1f],
            TextureCoordinates = [0f, 0f, 1f, 1f],
            TriangleIndices = [0, 1, 2, 0, 2, 3],
            Skinning = new ModelPreviewSkinningData
            {
                Skeleton = skeleton,
                TransformIndices = [0, 1, 0, 1, 0, 1, 0, 1],
                Weights = [1f, 0f, 1f, 0f, 1f, 0f, 1f, 0f]
            },
            TextureIds = [0x10, 0x20],
            ColorTextureId = 0x10,
            MaterialId = 0x99,
            MaterialTextures = materialTextures,
            RenderClass = ModelPreviewMaterialRenderClass.Emissive,
            ArmorIds = ["archive-a", "archive-b"],
            IsCullingBody = true,
            RenderStatus = ModelPreviewMeshRenderStatus.HiddenProxyGeometry,
            BoundsDiagonal = 12.5,
            UvStatusText = "uv-ok",
            PreviewStatusText = "preview-ok"
        };

        var texture = new TextureInspectionItem
        {
            PatchFile = "body/a.patch_0",
            PatchPath = "body/a.patch_0",
            PatchOrder = 2,
            TocEntryIndex = 9,
            TextureId = 0x10,
            MainOffset = 11,
            MainSize = 12,
            GpuOffset = 13,
            GpuSize = 14,
            StreamOffset = 15,
            StreamSize = 16,
            Width = 128,
            Height = 64,
            MipCount = 7,
            DxgiFormat = 71,
            PayloadKind = "DXGI",
            PayloadSource = "GPU",
            AlphaIsMaterialParameter = true,
            PreviewRole = TexturePreviewRole.LikelyNormalMap,
            PreviewRoleText = "Nar"
        };
        var source = new ModelPreviewResult
        {
            Error = "warn",
            SkippedStreams = 2,
            PatchFileCount = 3
        };
        source.Meshes.Add(mesh);
        source.Textures.Add(texture);

        using var stream = new MemoryStream();
        ModelPreviewPack.Write(stream, source);
        stream.Position = 0;
        var result = ModelPreviewPack.Read(stream);

        Assert.Equal("warn", result.Error);
        Assert.Equal(2, result.SkippedStreams);
        Assert.Equal(3, result.PatchFileCount);

        var restored = Assert.Single(result.Meshes);
        Assert.Equal(mesh.PatchFile, restored.PatchFile);
        Assert.Equal(mesh.UnitId, restored.UnitId);
        Assert.Equal(mesh.StreamIndex, restored.StreamIndex);
        Assert.Equal(mesh.MeshInfoIndex, restored.MeshInfoIndex);
        Assert.Equal(mesh.BodyShape, restored.BodyShape);
        Assert.Equal(mesh.CustomizationSlot, restored.CustomizationSlot);
        Assert.Equal(mesh.Positions, restored.Positions);
        Assert.Equal(mesh.Normals, restored.Normals);
        Assert.Equal(mesh.TextureCoordinates, restored.TextureCoordinates);
        Assert.Equal(mesh.TriangleIndices, restored.TriangleIndices);
        Assert.Equal(mesh.TextureIds, restored.TextureIds);
        Assert.Equal(mesh.ColorTextureId, restored.ColorTextureId);
        Assert.Equal(mesh.MaterialId, restored.MaterialId);
        Assert.Equal(mesh.RenderClass, restored.RenderClass);
        Assert.Equal(mesh.ArmorIds, restored.ArmorIds);
        Assert.Equal(mesh.IsCullingBody, restored.IsCullingBody);
        Assert.Equal(mesh.RenderStatus, restored.RenderStatus);
        Assert.Equal(mesh.BoundsDiagonal, restored.BoundsDiagonal);
        Assert.Equal(mesh.UvStatusText, restored.UvStatusText);
        Assert.Equal(mesh.PreviewStatusText, restored.PreviewStatusText);

        var skinning = Assert.IsType<ModelPreviewSkinningData>(restored.Skinning);
        Assert.Equal(skeleton.BonesId, skinning.Skeleton.BonesId);
        Assert.Equal(skeleton.StateMachineId, skinning.Skeleton.StateMachineId);
        Assert.Equal(skeleton.Bones.Count, skinning.Skeleton.Bones.Count);
        Assert.Equal(skeleton.Bones[1].ParentIndex, skinning.Skeleton.Bones[1].ParentIndex);
        Assert.Equal(skeleton.Bones[1].NameHash, skinning.Skeleton.Bones[1].NameHash);
        Assert.Equal(skeleton.Bones[1].BindTransform, skinning.Skeleton.Bones[1].BindTransform);
        Assert.Equal(mesh.Skinning!.TransformIndices, skinning.TransformIndices);
        Assert.Equal(mesh.Skinning.Weights, skinning.Weights);

        var textures = restored.MaterialTextures;
        Assert.Equal([0x10ul, 0x20ul], textures.Get(ModelPreviewTextureRole.BaseColor));
        Assert.Equal([0x30ul], textures.Get(ModelPreviewTextureRole.Emissive));
        Assert.Equal([0x10ul, 0x20ul, 0x30ul], textures.AllTextureIds);
        Assert.Equal(0x10ul, textures.ColorTextureId);
        Assert.True(textures.ColorAlphaIsMetallic);
        var input = Assert.Single(textures.Inputs!);
        Assert.Equal(7u, input.SemanticId);
        Assert.Equal(0x10ul, input.TextureId);
        Assert.Equal(ModelPreviewTextureRole.BaseColor, input.Role);

        var restoredTexture = Assert.Single(result.Textures);
        Assert.Equal(texture.TextureId, restoredTexture.TextureId);
        Assert.Equal(texture.PatchPath, restoredTexture.PatchPath);
        Assert.Equal(texture.Width, restoredTexture.Width);
        Assert.Equal(texture.Height, restoredTexture.Height);
        Assert.Equal(texture.PayloadKind, restoredTexture.PayloadKind);
        Assert.Equal(texture.AlphaIsMaterialParameter, restoredTexture.AlphaIsMaterialParameter);
        Assert.Equal(texture.PreviewRole, restoredTexture.PreviewRole);
        Assert.Equal(texture.PreviewRoleText, restoredTexture.PreviewRoleText);
    }

    [Fact]
    public void RoundTrip_NullOptionalsStayNull()
    {
        var source = new ModelPreviewResult();
        source.Meshes.Add(new ModelPreviewMesh
        {
            PatchFile = "p",
            UnitId = 1,
            StreamIndex = 0,
            Positions = [0f, 0f, 0f],
            TriangleIndices = [0, 0, 0]
        });

        using var stream = new MemoryStream();
        ModelPreviewPack.Write(stream, source);
        stream.Position = 0;
        var result = ModelPreviewPack.Read(stream);

        Assert.Null(result.Error);
        var mesh = Assert.Single(result.Meshes);
        Assert.Null(mesh.Normals);
        Assert.Null(mesh.TextureCoordinates);
        Assert.Null(mesh.Skinning);
        Assert.Null(mesh.ColorTextureId);
        Assert.Empty(mesh.ArmorIds);
        Assert.Equal(ModelPreviewMaterialTextureSet.Empty.AllTextureIds, mesh.MaterialTextures.AllTextureIds);
    }

    [Fact]
    public void Read_RejectsForeignPayload()
    {
        using var stream = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Throws<InvalidDataException>(() => ModelPreviewPack.Read(stream));
    }

    [Fact]
    public void Read_RejectsArrayCountLargerThanRemainingStream()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x31564D50);
            writer.Write(1);
            writer.Write(false);
            writer.Write(0);
            writer.Write(0);
            writer.Write(1);
            writer.Write(string.Empty);
            writer.Write(0ul);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0);
            writer.Write(0);
            writer.Write(200_000_000);
        }
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => ModelPreviewPack.Read(stream));
    }
}
