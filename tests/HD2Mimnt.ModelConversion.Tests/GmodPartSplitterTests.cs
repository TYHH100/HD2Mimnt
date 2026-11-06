using System.Numerics;
using HD2Mimnt.ModelConversion.Gmod;
using HD2Mimnt.ModelConversion.Gmod.Geometry;
using HD2Mimnt.ModelConversion.Gmod.Parts;

namespace HD2Mimnt.ModelConversion.Tests;

/// <summary>HD2 把护甲拆成身体与头盔两套 Unit，而 GMOD 的 MDL 把头与身体放在一起。</summary>
public sealed class GmodPartSplitterTests
{
    [Fact]
    public void Split_HeadSubtreeMeshesGoToHelmet_RestGoesToBody()
    {
        var meshSet = Build(
            new MeshSpec("g0m0s0x0", 0, 0, [0]),   // 身体护甲，权重在骨盆
            new MeshSpec("g1m0s0x0", 1, 0, [3]),   // 头饰，权重在头发（头部子树最深处）
            new MeshSpec("g0m1s0x0", 0, 1, [2]));  // bodygroup:0 的另一个备选落在头骨上

        var split = GmodPartSplitter.Split(meshSet);

        Assert.Equal([1, 2, 3], split.HeadBones.Order());
        Assert.Equal(GmodPartKind.Body, split.MeshParts["g0m0s0x0"]);
        Assert.Equal(GmodPartKind.Helmet, split.MeshParts["g1m0s0x0"]);
        Assert.Equal(GmodPartKind.Helmet, split.MeshParts["g0m1s0x0"]);
        Assert.True(split.HasBody);
        Assert.True(split.HasHelmet);

        // bodygroup:0 的备选分别落在身体和头盔，因此两边的包都要发布这个开关。
        Assert.Equal(GmodPartMask.Both, split.BodyGroupMasks[0]);
        Assert.Equal(GmodPartMask.Helmet, split.BodyGroupMasks[1]);
        Assert.Equal(["bodygroup:0", "skin"], split.AxisKeys(GmodPartKind.Body, includeSkin: true).Order());
        Assert.Equal(["bodygroup:0", "bodygroup:1", "skin"], split.AxisKeys(GmodPartKind.Helmet, includeSkin: true).Order());
        Assert.Equal(["bodygroup:0"], split.AxisKeys(GmodPartKind.Body, includeSkin: false).Order());
    }

    [Fact]
    public void Split_DominantBoneDecides_WhenWeightsAreMixed()
    {
        // 一个颈部顶点 + 两个头骨顶点 → 主导骨骼是头部，整块网格归头盔。
        var meshSet = Build(new MeshSpec("mixed", 0, 0, [1, 2, 2]));

        var split = GmodPartSplitter.Split(meshSet);

        Assert.Equal(GmodPartKind.Helmet, split.MeshParts["mixed"]);
    }

    [Fact]
    public void Split_NoHeadBone_ProducesBodyOnlyWithDiagnostic()
    {
        var bones = new[] { Bone(0, "ValveBiped.Bip01_Pelvis", -1), Bone(1, "ValveBiped.Bip01_Spine", 0) };
        var meshSet = Build(bones, new MeshSpec("g0m0s0x0", 0, 0, [0]));

        var split = GmodPartSplitter.Split(meshSet);

        Assert.Empty(split.HeadBones);
        Assert.False(split.HasHelmet);
        Assert.Contains("HeadBoneNotFound", split.Issues.Select(issue => issue.Code));
    }

    [Fact]
    public void DetectHeadBones_MatchesSeparatorVariantsAndExpandsChildren()
    {
        var bones = new[]
        {
            Bone(0, "bip01 pelvis", -1), Bone(1, "bip01 neck1", 0), Bone(2, "bip01 head1", 1),
            Bone(3, "hair_01", 2), Bone(4, "bip01 spine3", 0)
        };

        var head = GmodPartSplitter.DetectHeadBones(bones);

        // neck 是 head 的祖先，命中后包含整棵头部子树（含头发）；spine 分支不属于头部。
        Assert.Equal([1, 2, 3], head.Order());
    }

    private sealed record MeshSpec(string Id, int BodyPart, int Model, int[] Bones);

    private static GmodBone Bone(int index, string name, int parent) =>
        new(index, name, parent, new Vector3(0, 0, index), Quaternion.Identity,
            [1, 0, 0, 0, 1, 0, 0, 0, 1], 0, 0, -1, "", null);

    private static GmodBone[] DefaultBones() =>
    [
        Bone(0, "ValveBiped.Bip01_Pelvis", -1),
        Bone(1, "ValveBiped.Bip01_Neck1", 0),
        Bone(2, "ValveBiped.Bip01_Head1", 1),
        Bone(3, "Hair_01", 2)
    ];

    private static GmodMeshSet Build(params MeshSpec[] meshes) => Build(DefaultBones(), meshes);

    private static GmodMeshSet Build(GmodBone[] bones, params MeshSpec[] meshes)
    {
        var list = new List<GmodMesh>();
        var bySlot = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var spec in meshes)
        {
            var vertices = spec.Bones.Select((bone, index) => new GmodVertex(
                new Vector3(index, 0, 0), Vector3.UnitZ, Vector2.Zero, Vector4.Zero,
                [1f], [(byte)bone])).ToArray();
            list.Add(new GmodMesh(spec.Id, spec.BodyPart, spec.Model, 0, 0, 0,
                "materials/fixture/base.vmt", $"model_{spec.Model}", vertices, [0]));
            var key = $"{spec.BodyPart}:{spec.Model}:0";
            bySlot[key] = bySlot.TryGetValue(key, out var existing) ? [.. existing, spec.Id] : [spec.Id];
        }

        return new GmodMeshSet("fixture.mdl", ".dx90.vtx", bones, list, [], 1, 1, [], bySlot);
    }
}
