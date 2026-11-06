using HD2Mimnt.ModelConversion.Source;
using HD2Mimnt.ModelConversion.Source.Mdl;
using HD2Mimnt.ModelConversion.Source.Vtx;
using HD2Mimnt.ModelConversion.Source.Vvd;

namespace HD2Mimnt.ModelConversion.Tests;

public sealed class SourceParsingTests
{
    [Fact]
    public void Load_ValidTriple_PreservesGeometryBlankAndSkinAlternatives()
    {
        var model = SourceModel.Load("fixture.mdl", SourceFixture.Mdl(), SourceFixture.Vvd(), SourceFixture.Vtx());
        var all = model.GetAllAlternatives();
        Assert.Equal(2, all.Count);
        Assert.Equal([0, 1, 2], Assert.Single(all[0].SubMeshes).Indices);
        Assert.Equal("fixture/base", Assert.Single(all[0].SubMeshes).MaterialPath);
        Assert.Equal("fixture/variant", Assert.Single(model.GetModelGeometry(0, 0, skin: 1).SubMeshes).MaterialPath);
        Assert.Empty(all[1].SubMeshes);
        Assert.True(model.Mdl.BodyParts[0].Models[1].IsBlank);
        Assert.Equal(3, all[0].ReferencedVertexCount());
        Assert.All(all[0].Vertices, vertex => { Assert.Equal(1f, vertex.Weight0); Assert.NotNull(vertex.Tangent); });
        Assert.Equal("hips", Assert.Single(ValveBipedMapping.Resolve(model.Mdl.Bones, ["hips"])).TargetName);
    }

    [Theory]
    [InlineData(47)]
    [InlineData(50)]
    public void Mdl_UnverifiedVersion_Rejects(int version)
    {
        var data = SourceFixture.Mdl(); SourceFixture.Int(data, 4, version);
        Assert.Throws<SourceFormatException>(() => MdlFile.Parse(data));
    }

    [Fact]
    public void Mdl_V49Layout_ParsesAndSkipsJiggleBoing()
    {
        // v49（Alien Swarm/L4D2 系）与 v48 的表布局一致，但 jiggle bone 没有 boing 数据块：
        // boing 位置放垃圾字节也必须能解析，且 boing 参数归零而不是读进垃圾。
        var data = SourceFixture.Mdl(); SourceFixture.Int(data, 4, 49);
        SourceFixture.Int(data, 408 + 164, 5); SourceFixture.Int(data, 408 + 168, 1300 - 408);
        SourceFixture.Int(data, 1300, 0xE1);
        for (var i = 0; i < 29; i++) SourceFixture.Float(data, 1304 + i * 4, i + 0.25f);
        SourceFixture.Float(data, 1300 + 120, float.NaN);
        SourceFixture.Float(data, 1300 + 124, float.PositiveInfinity);
        var parsed = MdlFile.Parse(data);
        Assert.Equal(49, parsed.Header.Version);
        var jiggle = Assert.IsType<MdlJiggleBone>(Assert.Single(parsed.Bones).Jiggle);
        Assert.Equal(0.25f, jiggle.Length);
        Assert.Equal(28.25f, jiggle.BaseForwardFriction);
        Assert.Equal(new MdlJiggleBoing(0, 0, 0, 0, 0), jiggle.Boing);
    }

    [Fact]
    public void Load_ChecksumMismatch_Rejects()
    {
        var vtx = SourceFixture.Vtx(); SourceFixture.Int(vtx, 16, 456);
        Assert.Throws<SourceFormatException>(() => SourceModel.Load("x.mdl", SourceFixture.Mdl(), SourceFixture.Vvd(), vtx));
    }

    [Fact]
    public void Geometry_MissingVtxMesh_DoesNotReturnPartialModel()
    {
        var vtx = SourceFixture.Vtx(); SourceFixture.Int(vtx, 60, 0);
        var model = SourceModel.Load("x.mdl", SourceFixture.Mdl(), SourceFixture.Vvd(), vtx);
        Assert.Throws<SourceFormatException>(() => model.GetAllAlternatives());
    }

    [Fact]
    public void Geometry_UnverifiedLodAndUnknownChoice_Rejects()
    {
        var model = SourceModel.Load("x.mdl", SourceFixture.Mdl(), SourceFixture.Vvd(), SourceFixture.Vtx());
        Assert.Throws<SourceFormatException>(() => model.GetGeometry(lod: 1));
        Assert.Throws<SourceFormatException>(() => model.GetGeometry(new Dictionary<int, int> { [8] = 1 }));
        Assert.Throws<SourceFormatException>(() => model.GetGeometry(new Dictionary<int, int> { [0] = 2 }));
        Assert.Throws<SourceFormatException>(() => model.GetGeometry(skin: 2));
    }

    [Fact]
    public void Mdl_JiggleLayout_PreservesEveryFieldAndCorrectFlagBits()
    {
        var data = SourceFixture.Mdl();
        SourceFixture.Int(data, 408 + 164, 5); SourceFixture.Int(data, 408 + 168, 1300 - 408);
        SourceFixture.Int(data, 1300, 0xE1);
        for (var i = 0; i < 34; i++) SourceFixture.Float(data, 1304 + i * 4, i + 0.25f);
        var jiggle = Assert.IsType<MdlJiggleBone>(Assert.Single(MdlFile.Parse(data).Bones).Jiggle);
        Assert.True(jiggle.Flags.HasFlag(MdlJiggleFlags.HasLengthConstraint));
        Assert.True(jiggle.Flags.HasFlag(MdlJiggleFlags.HasBaseSpring));
        Assert.True(jiggle.Flags.HasFlag(MdlJiggleFlags.IsBoing));
        Assert.Equal(0.25f, jiggle.Length); Assert.Equal(28.25f, jiggle.BaseForwardFriction);
        Assert.Equal(29.25f, jiggle.Boing.ImpactSpeed); Assert.Equal(33.25f, jiggle.Boing.Amplitude);
    }

    [Fact]
    public void Mdl_BadParent_RejectsWithoutReparenting()
    {
        var data = SourceFixture.Mdl(); SourceFixture.Int(data, 412, 0);
        Assert.Throws<SourceFormatException>(() => MdlFile.Parse(data));
    }

    [Fact]
    public void Vvd_Fixups_ReordersPoolWithoutChangingWeights()
    {
        var data = new byte[128 + 3 * 48];
        SourceFixture.Text(data, 0, "IDSV"); SourceFixture.Int(data, 4, 4); SourceFixture.Int(data, 12, 2);
        SourceFixture.Int(data, 16, 3); SourceFixture.Int(data, 20, 2);
        SourceFixture.Int(data, 48, 2); SourceFixture.Int(data, 52, 64); SourceFixture.Int(data, 56, 128);
        SourceFixture.Int(data, 64, 1); SourceFixture.Int(data, 68, 1); SourceFixture.Int(data, 72, 2);
        SourceFixture.Int(data, 76, 0); SourceFixture.Int(data, 80, 0); SourceFixture.Int(data, 84, 1);
        for (var i = 0; i < 3; i++)
        {
            SourceFixture.Float(data, 128 + i * 48 + 16, i);
            SourceFixture.Float(data, 128 + i * 48, 0.7f); data[128 + i * 48 + 15] = 1;
        }
        var vvd = VvdFile.Parse(data);
        Assert.Equal([1f, 2f, 0f], vvd.GetLodVertices(0).Select(v => v.Position.X));
        Assert.Equal([1f, 2f], vvd.GetLodVertices(1).Select(v => v.Position.X));
        Assert.All(vvd.GetLodVertices(0), v => Assert.Equal(0.7f, v.Weight0));
        SourceFixture.Int(data, 20, 1);
        Assert.Throws<SourceFormatException>(() => VvdFile.Parse(data));
    }

    [Fact]
    public void Vvd_TruncatedTangentsAndNegativeLodCount_Rejects()
    {
        Assert.Throws<SourceFormatException>(() => VvdFile.Parse(SourceFixture.Vvd()[..^1]));
        var bytes = SourceFixture.Vvd(); SourceFixture.Int(bytes, 16, -1);
        Assert.Throws<SourceFormatException>(() => VvdFile.Parse(bytes));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Vvd_NonFiniteVertex_Rejects(float value)
    {
        var bytes = SourceFixture.Vvd(); SourceFixture.Float(bytes, 80, value);
        Assert.Throws<SourceFormatException>(() => VvdFile.Parse(bytes));
    }

    [Fact]
    public void Vtx_TriangleStrip_AlternatesWinding()
    {
        var vertices = Enumerable.Range(0, 4).Select(i => new VtxVertex(0, 0, 0, 1, (ushort)i, 0, 0, 0)).ToArray();
        var mesh = new VtxMesh([new VtxStripGroup(vertices, [0, 1, 2, 3],
            [new VtxStrip(4, 0, 4, 0, 1, VtxStripFlags.TriangleStrip)], 0)], 0);
        Assert.Equal([(0, 1, 2), (2, 1, 3)], mesh.EnumerateTriangles().ToArray());
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 2)]
    public void Vtx_UnknownPrimitiveOrIncompleteTriangle_Rejects(byte flag, int count)
    {
        var bytes = SourceFixture.Vtx(); bytes[169] = flag; SourceFixture.Int(bytes, 151, count);
        var model = SourceModel.Load("x.mdl", SourceFixture.Mdl(), SourceFixture.Vvd(), bytes);
        Assert.Throws<SourceFormatException>(() => model.GetAllAlternatives());
    }

    [Fact]
    public void Load_Cancelled_StopsBeforeParsing()
    {
        Assert.Throws<OperationCanceledException>(() => SourceModel.Load("x.mdl", [], [], [], cancellationToken: new CancellationToken(true)));
    }

    [Fact]
    public void Mapping_UnmappedAndMissingBones_AreNotMergedOrDropped()
    {
        var bone = Assert.Single(MdlFile.Parse(SourceFixture.Mdl()).Bones);
        var source = new[] { bone, bone with { Index = 1, Name = "hair" } };
        var result = ValveBipedMapping.Resolve(source, ["hips"]);
        Assert.Equal(BoneMappingStatus.Candidate, result[0].Status);
        Assert.Equal(BoneMappingStatus.PreserveSourceBone, result[1].Status);
        Assert.Equal(BoneMappingStatus.MissingTarget, ValveBipedMapping.Resolve(source, [])[0].Status);
        var collision = ValveBipedMapping.Resolve(source, ["hips"], new Dictionary<string, string?> { ["hair"] = "hips" });
        Assert.All(collision, row => Assert.Equal(BoneMappingStatus.AmbiguousTarget, row.Status));
        Assert.Equal(52, ValveBipedMapping.Defaults.Count);
    }
}
