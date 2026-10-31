using HD2Mimnt.Core.ModelPreview;

namespace HD2Mimnt.Tests.ModelPreview;

public class ModelPreviewCharacterOrientationTests
{
    private static ModelPreviewMesh Mesh(ModelPreviewCustomizationSlot slot, float[] positions) => new()
    {
        PatchFile = "test.patch_0",
        UnitId = 1,
        StreamIndex = 0,
        CustomizationSlot = slot,
        Positions = positions,
        TriangleIndices = []
    };

    /// <summary>在区间内均匀生成 n 个顶点（x/y 固定摆动），z 线性取值。</summary>
    private static float[] Column(int count, float minZ, float maxZ, float xExtent = 0.2f)
    {
        var positions = new float[count * 3];
        for (var i = 0; i < count; i++)
        {
            var t = (float)i / Math.Max(1, count - 1);
            positions[i * 3] = xExtent * (i % 2 == 0 ? 1 : -1);
            positions[i * 3 + 1] = xExtent * (i % 3 == 0 ? 1 : -1);
            positions[i * 3 + 2] = minZ + (maxZ - minZ) * t;
        }
        return positions;
    }

    /// <summary>水平张开的 T-pose 手臂：X 跨度大、Z 集中。</summary>
    private static float[] Arms(int count, float minZ, float maxZ)
    {
        var positions = new float[count * 3];
        for (var i = 0; i < count; i++)
        {
            var t = (float)i / Math.Max(1, count - 1);
            positions[i * 3] = -2f + 4f * t;
            positions[i * 3 + 1] = 0;
            positions[i * 3 + 2] = minZ + (maxZ - minZ) * (i % 7) / 7f;
        }
        return positions;
    }

    [Fact]
    public void JunkLegLabelsAboveTorso_FallBackToVertexDistribution()
    {
        // 回归（VRC Shinano Frilletta）：转换工具把头发/披风件标成 LeftLeg/RightLeg，
        // "腿"质心（z≈1.9）高于"躯干"（z≈1.3），旧逻辑推导出"上方向 = -Z"，
        // 人物头着地。标注与解剖矛盾时必须整条作废，交给顶点分布路径。
        var meshes = new List<ModelPreviewMesh>
        {
            Mesh(ModelPreviewCustomizationSlot.Torso, Column(200, 0.9f, 1.7f)),
            Mesh(ModelPreviewCustomizationSlot.LeftLeg, Column(100, 1.8f, 2.0f)),
            Mesh(ModelPreviewCustomizationSlot.RightLeg, Column(100, 1.8f, 2.0f)),
            Mesh(ModelPreviewCustomizationSlot.Unknown, Column(300, 0.0f, 0.9f)),
        };
        Assert.Equal(ModelPreviewPresentationRotation.PositiveZToPositiveY,
            ModelPreviewCharacterOrientation.GetRequiredRotation(meshes));
    }

    [Fact]
    public void ConsistentLegLabelsBelowTorso_StillDecideRotation()
    {
        // 可信标注（腿在躯干下方）仍是"最强证据"：即使张开的 T-pose 手臂让 X
        // 标准差压过 Z、顶点分布路径会拒绝旋转，地标路径也应正常给出 Z-up。
        var meshes = new List<ModelPreviewMesh>
        {
            Mesh(ModelPreviewCustomizationSlot.Torso, Column(200, 0.9f, 1.7f)),
            Mesh(ModelPreviewCustomizationSlot.LeftLeg, Column(100, 0.0f, 0.4f)),
            Mesh(ModelPreviewCustomizationSlot.RightLeg, Column(100, 0.0f, 0.4f)),
            Mesh(ModelPreviewCustomizationSlot.Unknown, Arms(300, 1.2f, 1.5f)),
        };
        Assert.Equal(ModelPreviewPresentationRotation.PositiveZToPositiveY,
            ModelPreviewCharacterOrientation.GetRequiredRotation(meshes));
    }
}
