using System.Numerics;
using HD2Mimnt.ModelConversion.Gmod;
using HD2Mimnt.ModelConversion.Gmod.Geometry;
using HD2Mimnt.ModelConversion.Gmod.Physics;
using HD2Mimnt.ModelConversion.Source.Mdl;

namespace HD2Mimnt.ModelConversion.Tests;

public sealed class JiggleBoneConverterTests
{
    private static MdlJiggleBone Jiggle(float length = 12f) => new(
        MdlJiggleFlags.IsFlexible | MdlJiggleFlags.HasAngleConstraint | MdlJiggleFlags.HasBaseSpring,
        length, 2f, 30f, 0.4f, 28f, 0.35f, 25f, 0.3f, 45f, -20f, 20f, 0.2f, 0.1f,
        -15f, 15f, 0.2f, 0.1f, 3f, 20f, 0.5f, -5f, 5f, 0.1f, -5f, 5f, 0.1f,
        -5f, 5f, 0.1f, new MdlJiggleBoing(1f, 2f, 3f, 4f, 5f));

    private static IReadOnlyList<GmodBone> Skeleton() =>
    [
        new GmodBone(0, "root", -1, Vector3.Zero, Quaternion.Identity, new float[12],
            (uint)MdlBoneFlags.UsedByVertexLod0, 0, -1, "default", null),
        new GmodBone(1, "hair_1", 0, new Vector3(0, 0, 10), Quaternion.Identity, new float[12],
            (uint)MdlBoneFlags.PhysicallySimulated, 5, 0, "cloth", Jiggle()),
        new GmodBone(2, "hair_2", 1, new Vector3(0, 0, 20), Quaternion.Identity, new float[12],
            (uint)MdlBoneFlags.PhysicallySimulated, 5, 1, "cloth", Jiggle(8f))
    ];

    [Fact]
    public void Convert_JiggleBoneChain_ProducesWritableHd2PhysProfile()
    {
        var bones = Skeleton();
        var result = JiggleBoneConverter.Convert(bones, null);
        Assert.Equal(3, result.Bones.Count);
        Assert.Equal(3u, result.PaletteSlots);
        var chain = Assert.Single(result.Chains);
        Assert.Equal(3, chain.Joints.Count); // fixed driver + two simulated joints
        Assert.Equal("root", chain.Joints[0].Bone);
        Assert.Equal("hair_1", chain.Joints[1].Bone);
        Assert.Equal("hair_2", chain.Joints[2].Bone);
        Assert.All(chain.Joints.Skip(1), joint => Assert.Equal(Hd2PhysLimitKind.Cone, joint.LimitKind));
        Assert.True(chain.Stiffness > 0 && chain.Damping >= 0);

        var document = new Hd2PhysDocument(0x1101, result.PaletteSlots, result.Bones, result.Chains);
        var (bytes, summary) = Hd2PhysCodec.Write(document);
        Assert.Equal(1, summary.Chains);
        Assert.Equal(3, summary.Joints);
        Assert.Equal("HD2PHY1\0"u8.ToArray(), bytes[..8]);
        Assert.Equal(3, Hd2PhysCodec.Read(bytes).JointCount);
    }

    [Fact]
    public void Convert_PhyData_RetainsRagdollNoticeWithoutGuessingLayout()
    {
        var phy = new byte[16];
        SourceFixture.Int(phy, 0, 16);
        SourceFixture.Text(phy, 4, "VPHY");
        SourceFixture.Int(phy, 12, 2);
        var result = JiggleBoneConverter.Convert(Skeleton(), phy);
        Assert.Contains(result.Issues, issue => issue.Code == "RagdollCollisionRetainedRaw" &&
            issue.Message.Contains("2 declared solids"));
    }

    [Fact]
    public void Convert_JiggleParameters_SurviveGeometryBundleRoundTrip()
    {
        var bundle = new GmodGeometryBundle("models/hair.mdl", ".dx90.vtx", Skeleton(), [], [], [], 1, 1, []);
        var restored = GmodGeometryCodec.Read(GmodGeometryCodec.Write(bundle));
        Assert.NotNull(restored.Bones[1].Jiggle);
        Assert.Equal(20f, restored.Bones[1].Jiggle!.BaseStiffness);
        Assert.Equal(5f, restored.Bones[1].Jiggle.Boing.Amplitude);
        Assert.Equal((uint)MdlJiggleFlags.HasBaseSpring,
            (uint)restored.Bones[1].Jiggle.Flags & (uint)MdlJiggleFlags.HasBaseSpring);
    }
}
