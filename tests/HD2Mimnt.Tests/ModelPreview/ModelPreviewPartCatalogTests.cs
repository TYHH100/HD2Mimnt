using System.Text;
using HD2Mimnt.Core.ModelPreview;
using HD2Mimnt.Infrastructure.ModelConversion;
using HD2Mimnt.Infrastructure.ModelPreview;
using HD2Mimnt.Tests.Services;

namespace HD2Mimnt.Tests.ModelPreview;

public sealed class ModelPreviewPartCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-Preview-Parts-" + Guid.NewGuid().ToString("N"));

    public ModelPreviewPartCatalogTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Reader_IasDifference_ExposesIndependentPartsWithOneBasedDefault()
    {
        const string sidecar = """
            manifest_version=6
            difference_logic=free_parts
            domain=body
            free_part=coat,Coat,on
            free_part_display_name=coat,外套
            free_option=coat,on,0
            free_option_display_name=coat,on,穿外套
            free_route=coat,on,0x1,0x1,0x10
            free_option=coat,off,1
            free_option_display_name=coat,off,脱外套
            free_route=coat,off,0x1,0x1,0x20
            free_part=shoes,Shoes,on
            free_part_display_name=shoes,鞋子
            free_option=shoes,on,0
            free_option_display_name=shoes,on,穿鞋
            free_route=shoes,on,0x2,0x2,0x30
            free_option=shoes,off,1
            free_option_display_name=shoes,off,脱鞋
            free_route=shoes,off,0x2,0x2,0x40
            """;
        var rig = Path.Combine(_root, "components", "0", "HD2IndependentRig");
        Directory.CreateDirectory(rig);
        File.WriteAllText(Path.Combine(rig, "model.difference.ini"), sidecar);
        File.WriteAllText(Path.Combine(rig, "other.difference.ini"), sidecar.Replace("0x10", "0x99", StringComparison.Ordinal));
        var patch = CompatibilityFixture.WritePatch(_root, resources:
        [
            new(0x10, CompatibilityFixture.UnitType, CompatibilityFixture.Unit()),
            new(0x20, CompatibilityFixture.UnitType, CompatibilityFixture.Unit()),
            new(0x30, CompatibilityFixture.UnitType, CompatibilityFixture.Unit()),
            new(0x40, CompatibilityFixture.UnitType, CompatibilityFixture.Unit())
        ]);

        var result = await new PatchResourceInspectionService().PreviewModelAsync(new DirectoryInfo(_root), [new FileInfo(patch)]);

        Assert.Null(result.Error);
        Assert.True(result.ForceDecodeOversizedStreams);
        Assert.Empty(result.CatalogUnitIds);
        Assert.Equal(["外套", "鞋子"], result.Parts.Select(part => part.Name));
        Assert.All(result.Parts, part => Assert.Equal(0, part.SelectedIndex));
        Assert.Equal([0x10UL], result.Parts[0].Options[0].UnitIds);
        Assert.Equal([0x20UL], result.Parts[0].Options[1].UnitIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reader_Hd2ClCatalog_ParsesPartsAndEmptyHideOption(bool compressedProfiles)
    {
        var part = IasValue.Obj(
            ("id", IasValue.Of("hat")),
            ("name", IasValue.Of("帽子")),
            ("selected", IasValue.Of(2L)),
            ("options", IasValue.Of([
                IasValue.Obj(("id", IasValue.Of("on")), ("name", IasValue.Of("戴帽")),
                    ("units", IasValue.Of([IasValue.Of("0000000000000010"), IasValue.Of("000000000000000a")]))),
                IasValue.Obj(("id", IasValue.Of("off")), ("name", IasValue.Of("脱帽")),
                    ("units", IasValue.Of(Array.Empty<IasValue>())))
            ])));
        var model = IasValue.Obj(
            ("id", IasValue.Of("model")),
            ("name", IasValue.Of("Model")),
            ("base", IasValue.Of([IasValue.Of("000000000000000a")])),
            ("display_units", IasValue.Obj(("0000000000000010", IasValue.Of("0000000000000011")))),
            ("head_units", IasValue.Of([IasValue.Of("0000000000000010")])),
            ("views", IasValue.Obj(("body", IasValue.Obj(("display_units", IasValue.Obj(("0000000000000020", IasValue.Of("0000000000000099")))))))),
            ("parts", IasValue.Of([part])));
        var catalog = IasValue.Obj(("version", IasValue.Of(2L)), ("models", IasValue.Of([model])));
        var expression = compressedProfiles
            ? "(function() local _profiles={" + IasLua.Literal(part) + "}; return {[\"version\"]=2,[\"models\"]={{[\"id\"]=\"model\",[\"name\"]=\"Model\",[\"base\"]={\"000000000000000a\"},[\"display_units\"]={[\"0000000000000010\"]=\"0000000000000011\"},[\"head_units\"]={},[\"parts\"]={_profiles[1]}}}} end)()"
            : IasLua.Literal(catalog);
        var code = Encoding.UTF8.GetBytes("-- HD2CL_MODEL_V1 {}\nreturn " + expression + "\n");
        var patch = CompatibilityFixture.WritePatch(_root,
            resources: [new(0x7EA22D7DE46A5490, IasResourceTypes.Lua, code)]);

        var result = await new PatchResourceInspectionService().PreviewModelAsync(new DirectoryInfo(_root), [new FileInfo(patch)]);

        Assert.Null(result.Error);
        var group = Assert.Single(result.Parts);
        Assert.Equal("帽子", group.Name);
        Assert.Equal(1, group.SelectedIndex);
        Assert.Equal([0x11UL], group.Options[0].UnitIds);
        Assert.Empty(group.Options[1].UnitIds);
        Assert.Equal([0x0AUL, 0x11UL], result.CatalogUnitIds.Order());
        Assert.DoesNotContain(0x99UL, result.CatalogUnitIds);
    }

    [Fact]
    public async Task Reader_Hd2ClLaterPatchOverridesEarlierCatalog()
    {
        var catalog = IasValue.Obj(("version", IasValue.Of(2L)), ("models", IasValue.Of([
            IasValue.Obj(("id", IasValue.Of("model")), ("parts", IasValue.Of([
                IasValue.Obj(("id", IasValue.Of("hat")), ("name", IasValue.Of("Hat")), ("selected", IasValue.Of(1L)),
                    ("options", IasValue.Of([
                        IasValue.Obj(("id", IasValue.Of("on")), ("name", IasValue.Of("On")), ("units", IasValue.Of([IasValue.Of("1")]))),
                        IasValue.Obj(("id", IasValue.Of("off")), ("name", IasValue.Of("Off")), ("units", IasValue.Of(Array.Empty<IasValue>())))
                    ])))
            ])))
        ])));
        var code = Encoding.UTF8.GetBytes("-- HD2CL_MODEL_V1 {}\nreturn " + IasLua.Literal(catalog) + "\n");
        var earlier = CompatibilityFixture.WritePatch(_root, "catalog.patch_0",
            resources: [new(0x1234, IasResourceTypes.Lua, code)]);
        var later = CompatibilityFixture.WritePatch(_root, "catalog.patch_1",
            resources: [new(0x1234, IasResourceTypes.Lua, Encoding.UTF8.GetBytes("return {}"))]);

        var result = await new PatchResourceInspectionService().PreviewModelAsync(new DirectoryInfo(_root),
            [new FileInfo(earlier), new FileInfo(later)]);

        Assert.Null(result.Error);
        Assert.Empty(result.Parts);
    }

    [Fact]
    public void Selection_EmptyOptionHidesPartAndUnknownUnitsRemainVisible()
    {
        var groups = new[]
        {
            new ModelPreviewPartGroup
            {
                Id = "coat", Name = "Coat", SelectedIndex = 0,
                Options = [new() { Id = "on", Name = "On", UnitIds = [0x10] }, new() { Id = "off", Name = "Off", UnitIds = [] }]
            },
            new ModelPreviewPartGroup
            {
                Id = "shoes", Name = "Shoes", SelectedIndex = 0,
                Options = [new() { Id = "on", Name = "On", UnitIds = [0x30] }, new() { Id = "off", Name = "Off", UnitIds = [0x40] }]
            }
        };
        ModelPreviewMesh Mesh(ulong id) => new()
        {
            PatchFile = "p", UnitId = id, StreamIndex = 0, Positions = [0, 0, 0], TriangleIndices = [0, 0, 0]
        };
        var meshes = new[] { Mesh(0x10), Mesh(0x20), Mesh(0x30), Mesh(0x99) };

        var visible = ModelPreviewPartSelection.Filter(meshes, groups, [1, 0]);

        Assert.Equal([0x20UL, 0x30UL, 0x99UL], visible.Select(mesh => mesh.UnitId));
    }

    [Fact]
    public void Selector_PartUnitsKeepRealLowPolyButHideCullingCollisionAndBoxHelpers()
    {
        var visible = Mesh(1, [0, 0, 0, 1, 0, 0, 0, 1, 0]);
        var cullingOnly = Mesh(2, [0, 0, 0, 1, 0, 0, 0, 1, 0], isCullingBody: true);
        var point = Mesh(3, [0, 0, 0, 0, 0, 0, 0, 0, 0]);
        var collisionOnly = SphereMesh(4);
        var box = BoxMesh(5);

        var selection = ModelPreviewMeshSelector.Select([visible, cullingOnly, point, collisionOnly, box], new HashSet<ulong> { 1, 2, 3, 4, 5 });

        Assert.Equal(ModelPreviewMeshRenderStatus.Visible, visible.RenderStatus);
        Assert.Equal(ModelPreviewMeshRenderStatus.HiddenCullingBody, cullingOnly.RenderStatus);
        Assert.Equal(ModelPreviewMeshRenderStatus.HiddenProxyGeometry, point.RenderStatus);
        Assert.Equal(ModelPreviewMeshRenderStatus.HiddenCollisionSphere, collisionOnly.RenderStatus);
        Assert.Equal(ModelPreviewMeshRenderStatus.HiddenProxyGeometry, box.RenderStatus);
        Assert.Equal([visible], selection.VisibleMeshes);
    }

    [Fact]
    public void Selector_PartUnitWithVisibleMeshKeepsCullingHelperHidden()
    {
        var visible = Mesh(1, [0, 0, 0, 1, 0, 0, 0, 1, 0]);
        var culling = Mesh(1, [0, 0, 1, 1, 0, 1, 0, 1, 1], isCullingBody: true);

        var selection = ModelPreviewMeshSelector.Select([visible, culling], new HashSet<ulong> { 1 });

        Assert.Equal(ModelPreviewMeshRenderStatus.Visible, visible.RenderStatus);
        Assert.Equal(ModelPreviewMeshRenderStatus.HiddenCullingBody, culling.RenderStatus);
        Assert.Equal([visible], selection.VisibleMeshes);
    }

    private static ModelPreviewMesh BoxMesh(ulong unitId)
    {
        int[][] faces =
        [
            [0, 1, 3, 2], [4, 5, 7, 6], [0, 1, 5, 4],
            [2, 3, 7, 6], [0, 2, 6, 4], [1, 3, 7, 5]
        ];
        var positions = new List<float>();
        var indices = new List<int>();
        foreach (var face in faces)
        {
            var offset = positions.Count / 3;
            foreach (var corner in face)
            {
                positions.Add(corner & 1);
                positions.Add(((corner >> 1) & 1) + ((corner & 4) == 0 ? 0.1f : -0.1f));
                positions.Add((corner >> 2) & 1);
            }
            indices.AddRange([offset, offset + 1, offset + 2, offset, offset + 2, offset + 3]);
        }
        return new ModelPreviewMesh
        {
            PatchFile = "p",
            UnitId = unitId,
            StreamIndex = 0,
            Positions = positions.ToArray(),
            TriangleIndices = indices.ToArray()
        };
    }

    private static ModelPreviewMesh SphereMesh(ulong unitId)
    {
        const int vertexCount = 100;
        var positions = new float[vertexCount * 3];
        for (var index = 0; index < vertexCount; index++)
        {
            var y = 1 - 2 * (index + 0.5) / vertexCount;
            var radius = Math.Sqrt(1 - y * y);
            var angle = index * 2.399963229728653;
            positions[index * 3] = (float)(Math.Cos(angle) * radius);
            positions[index * 3 + 1] = (float)y;
            positions[index * 3 + 2] = (float)(Math.Sin(angle) * radius);
        }
        var indices = new List<int>();
        for (var index = 1; index < vertexCount; index++)
            indices.AddRange([0, index, index % (vertexCount - 1) + 1]);
        indices.AddRange([0, 1, 2]);
        return new ModelPreviewMesh
        {
            PatchFile = "p",
            UnitId = unitId,
            StreamIndex = 0,
            Positions = positions,
            TriangleIndices = indices.ToArray()
        };
    }

    private static ModelPreviewMesh Mesh(ulong unitId, float[] positions, bool isCullingBody = false) => new()
    {
        PatchFile = "p",
        UnitId = unitId,
        StreamIndex = 0,
        Positions = positions,
        TriangleIndices = [0, 1, 2],
        IsCullingBody = isCullingBody
    };

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
