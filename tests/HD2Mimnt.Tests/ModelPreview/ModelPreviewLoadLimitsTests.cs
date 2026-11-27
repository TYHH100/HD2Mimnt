using HD2Mimnt.Core.ModelPreview;

namespace HD2Mimnt.Tests.ModelPreview;

public sealed class ModelPreviewLoadLimitsTests
{
    [Fact]
    public void TryAddMesh_OversizedIndexMeshRequiresExplicitOptIn()
    {
        var mesh = new ModelPreviewMesh
        {
            PatchFile = "test.patch_0",
            UnitId = 1,
            StreamIndex = 0,
            Positions = [0, 0, 0],
            TriangleIndices = new int[3_000_001]
        };
        var standard = new ModelPreviewResult();
        var forced = new ModelPreviewResult { ForceDecodeOversizedStreams = true };

        Assert.False(standard.TryAddMesh(mesh));
        Assert.Empty(standard.Meshes);
        Assert.True(forced.TryAddMesh(mesh));
        Assert.Same(mesh, Assert.Single(forced.Meshes));
    }

    [Fact]
    public void TryAddMesh_ForcedModeStillEnforcesMeshSafetyLimit()
    {
        var result = new ModelPreviewResult { ForceDecodeOversizedStreams = true };
        for (var index = 0; index < 4_096; index++)
        {
            Assert.True(result.TryAddMesh(new ModelPreviewMesh
            {
                PatchFile = "test.patch_0",
                UnitId = (ulong)index,
                StreamIndex = 0,
                Positions = [0, 0, 0],
                TriangleIndices = []
            }));
        }

        Assert.True(result.IsAtCapacity);
        Assert.False(result.TryAddMesh(new ModelPreviewMesh
        {
            PatchFile = "test.patch_0",
            UnitId = 4_096,
            StreamIndex = 0,
            Positions = [0, 0, 0],
            TriangleIndices = []
        }));
        Assert.Equal(4_096, result.Meshes.Count);
    }
}
