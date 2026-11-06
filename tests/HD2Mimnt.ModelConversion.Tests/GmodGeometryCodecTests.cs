using System.Numerics;
using HD2Mimnt.ModelConversion.Gmod;
using HD2Mimnt.ModelConversion.Gmod.Geometry;
using HD2Mimnt.ModelConversion.Source;
using HD2Mimnt.ModelConversion.Source.Mdl;

namespace HD2Mimnt.ModelConversion.Tests;

public sealed class GmodGeometryCodecTests
{
    private static GmodGeometryBundle Bundle()
    {
        var jiggle = new MdlJiggleBone(MdlJiggleFlags.IsFlexible | MdlJiggleFlags.HasAngleConstraint,
            12f, 2f, 30f, 0.4f, 28f, 0.35f, 25f, 0.3f, 45f, -20f, 20f, 0.2f, 0.1f,
            -15f, 15f, 0.2f, 0.1f, 3f, 20f, 0.5f, -5f, 5f, 0.1f, -5f, 5f, 0.1f,
            -5f, 5f, 0.1f, new MdlJiggleBoing(1f, 2f, 3f, 4f, 5f));
        var bones = new[]
        {
            new GmodBone(0, "root", -1, Vector3.Zero, Quaternion.Identity,
                [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0], (uint)MdlBoneFlags.UsedByVertexLod0, 0, -1, "default", null),
            new GmodBone(1, "hair", 0, new Vector3(0, 0, 10), Quaternion.Identity,
                [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 10], (uint)MdlBoneFlags.PhysicallySimulated, 5, 0, "cloth", jiggle)
        };
        var vertices = new[]
        {
            new GmodVertex(new Vector3(0, 0, 0), Vector3.UnitZ, new Vector2(0, 0),
                new Vector4(1, 0, 0, 1), [1f], [0]),
            new GmodVertex(new Vector3(1, 0, 0), Vector3.UnitZ, new Vector2(1, 0),
                new Vector4(1, 0, 0, 1), [0.5f, 0.5f], [0, 1]),
            new GmodVertex(new Vector3(0, 1, 0), Vector3.UnitZ, new Vector2(0, 1),
                new Vector4(1, 0, 0, 1), [1f], [1])
        };
        var meshes = new[]
        {
            new GmodMesh("g0m0s0x0", 0, 0, 0, 0, 0, "materials/fixture/base.vmt", "visible",
                vertices, [0, 1, 2])
        };
        var attachments = new[] { new GmodAttachment(0, "eyes", 0, 0, new float[12]) };
        var options = new[]
        {
            new GmodOptionGeometry("opt0", new Dictionary<string, int> { ["bodygroup:0"] = 0, ["skin"] = 0 },
                ["g0m0s0x0"]),
            new GmodOptionGeometry("opt1", new Dictionary<string, int> { ["bodygroup:0"] = 1, ["skin"] = 0 },
                Array.Empty<string>())
        };
        return new GmodGeometryBundle("models/fixture.mdl", ".dx90.vtx", bones, meshes, attachments,
            options, 3, 2, [new GmodConversionIssue("LodNotReconstructed", "models/fixture.mdl", "LOD0 only")]);
    }

    [Fact]
    public void WriteRead_RoundTrip_PreservesCompleteGeometryAndJiggleParameters()
    {
        var bundle = Bundle();
        var bytes = GmodGeometryCodec.Write(bundle);
        Assert.Equal("HD2GEOM1"u8.ToArray(), bytes[..8]);
        var actual = GmodGeometryCodec.Read(bytes);

        Assert.Equal(bundle.ModelPath, actual.ModelPath);
        Assert.Equal(bundle.VtxExtension, actual.VtxExtension);
        Assert.Equal(bundle.LodCount, actual.LodCount);
        Assert.Equal(bundle.SkinFamilyCount, actual.SkinFamilyCount);
        Assert.Equal(bundle.Bones.Count, actual.Bones.Count);
        Assert.Equal(bundle.Meshes.Count, actual.Meshes.Count);
        Assert.Equal(bundle.Attachments.Count, actual.Attachments.Count);
        Assert.Equal(bundle.Options.Count, actual.Options.Count);
        Assert.Single(actual.Issues);
        Assert.Equal("LodNotReconstructed", actual.Issues[0].Code);

        var source = bundle.Bones[1];
        var restored = actual.Bones[1];
        Assert.Equal(source.Name, restored.Name);
        Assert.Equal(source.Parent, restored.Parent);
        Assert.Equal(source.Position, restored.Position);
        Assert.Equal(source.PoseToBone, restored.PoseToBone);
        Assert.Equal(source.Flags, restored.Flags);
        Assert.Equal(source.ProceduralType, restored.ProceduralType);
        Assert.Equal(source.SurfaceProp, restored.SurfaceProp);
        Assert.NotNull(restored.Jiggle);
        Assert.Equal(source.Jiggle!.Flags, restored.Jiggle.Flags);
        Assert.Equal(source.Jiggle.Length, restored.Jiggle.Length);
        Assert.Equal(source.Jiggle.BaseForwardFriction, restored.Jiggle.BaseForwardFriction);
        Assert.Equal(source.Jiggle.Boing.Amplitude, restored.Jiggle.Boing.Amplitude);

        var mesh = Assert.Single(actual.Meshes);
        Assert.Equal("g0m0s0x0", mesh.Id);
        Assert.Equal("materials/fixture/base.vmt", mesh.MaterialPath);
        Assert.Equal(new[] { 0, 1, 2 }, mesh.Indices);
        Assert.Equal(3, mesh.Vertices.Length);
        Assert.Equal(new Vector2(1, 0), mesh.Vertices[1].TexCoord);
        Assert.Equal(new float[] { 0.5f, 0.5f }, mesh.Vertices[1].Weights);
        Assert.Equal(new byte[] { 0, 1 }, mesh.Vertices[1].Bones);
        Assert.Equal(new float[] { 1f }, mesh.Vertices[2].Weights);

        Assert.Equal("opt1", actual.Options[1].OptionId);
        Assert.Equal(1, actual.Options[1].Choices["bodygroup:0"]);
        Assert.Empty(actual.Options[1].MeshIds);
    }

    [Fact]
    public void Read_CorruptedPayload_RejectsCrcMismatch()
    {
        var bytes = GmodGeometryCodec.Write(Bundle());
        bytes[64] ^= 0xFF;
        Assert.Contains("CRC32", Assert.Throws<SourceFormatException>(() => GmodGeometryCodec.Read(bytes)).Message);
    }

    [Fact]
    public void Write_PreCancelled_Throws()
    {
        Assert.Throws<OperationCanceledException>(() =>
            GmodGeometryCodec.Write(Bundle(), new CancellationToken(true)));
    }
}
