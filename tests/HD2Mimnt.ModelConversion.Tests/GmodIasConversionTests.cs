using System.Text;
using HD2Mimnt.ModelConversion.Gmod;
using HD2Mimnt.ModelConversion.Gmod.Geometry;
using HD2Mimnt.ModelConversion.Gmod.Parts;
using HD2Mimnt.ModelConversion.Ias;
using HD2Mimnt.ModelConversion.Source;
using HD2Mimnt.ModelConversion.Source.Gma;

namespace HD2Mimnt.ModelConversion.Tests;

public sealed class GmodIasConversionTests
{
    private static IasArchiveBinding Binding() => new(0xabcdef, IasContentDomain.Body,
        [new IasEquipmentBinding(1, [0x101])]);

    /// <summary>单包模式（不做身体/头盔拆分），保持对完整模型产物的逐项断言。</summary>
    private static GmodConversionOptions Options(GmodSwitchMode mode = GmodSwitchMode.Auto) =>
        new("gmod-fixture", Binding(), SwitchMode: mode, PartSplit: GmodPartSplit.None);

    private static MemoryAssetProvider Provider(int mdlVersion = 48)
    {
        const string material = "\"VertexLitGeneric\"\n{\n\"$basetexture\" \"textures/base\"\n" +
            "\"$bumpmap\" \"textures/normal\"\n\"$color\" \"[1 1 1]\"\n}\n";
        var mdl = SourceFixture.Mdl(); SourceFixture.Int(mdl, 4, mdlVersion);
        return new MemoryAssetProvider(new Dictionary<string, byte[]>
        {
            ["fixture.mdl"] = mdl,
            ["fixture.vvd"] = SourceFixture.Vvd(),
            ["fixture.dx90.vtx"] = SourceFixture.Vtx(),
            ["fixture.phy"] = Phy(),
            ["materials/fixture/base.vmt"] = Encoding.UTF8.GetBytes(material),
            ["materials/fixture/variant.vmt"] = Encoding.UTF8.GetBytes(material),
            ["materials/textures/base.vtf"] = Vtf(),
            ["addon.txt"] = Encoding.UTF8.GetBytes("fixture addon")
        });
    }

    [Fact]
    public void Convert_V49Model_EmitsTheSameBundleAsV48()
    {
        // v49（L4D2/Alien Swarm 系）是 Gmod addon 里常见的“头身一体”新版模型，转换行为必须与 v48 一致。
        using var provider = Provider(49);
        var package = SourceModelPackage.Load(provider, "fixture.mdl");
        Assert.Equal(49, package.Model.Mdl.Header.Version);
        var result = new GmodIasConverter().Convert(package, Options(), provider);

        Assert.Contains(result.Files, file => file.Path == "geometry/model.hd2geom");
        Assert.Equal(2, result.Report.MeshCount);
        Assert.Equal(4, result.Report.OptionCount);
        Assert.Equal(2, result.Report.MaterialCount);
    }

    [Fact]
    public void Convert_FullModel_EmitsCompleteBundleWithAllAlternativesSkinsTexturesAndSource()
    {
        using var provider = Provider();
        var package = SourceModelPackage.Load(provider, "fixture.mdl");
        var result = new GmodIasConverter().Convert(package, Options(), provider);

        // IAS companion files are byte-valid and cross-consistent.
        var difference = Assert.Single(result.Files.Where(file => file.Path.EndsWith(".difference.ini", StringComparison.Ordinal)));
        var isolation = Assert.Single(result.Files.Where(file => file.Path.EndsWith(".isolation.ini", StringComparison.Ordinal)));
        var rigbin = Assert.Single(result.Files.Where(file => file.Path.EndsWith(".rigbin", StringComparison.Ordinal)));
        var differences = IasFreePartsCodec.Read(difference.Data.Span, Binding());
        Assert.Single(differences.Parts);
        Assert.Equal(4, Assert.Single(differences.Parts).Options.Count); // 2 bodygroup alternatives x 2 skin families
        Assert.All(differences.Parts.SelectMany(part => part.Options).SelectMany(option => option.Routes),
            route => Assert.True(route.HasRigProfile));
        Assert.Contains("unit=", Encoding.UTF8.GetString(isolation.Data.Span));
        Assert.Equal(4, IasRigCodec.Read(rigbin.Data.Span).Profiles.Count);
        Assert.Single(result.Switches);
        Assert.Equal(GmodSwitchKind.Composite, result.Switches[0].Kind);
        Assert.Contains("CompositeSwitch", result.Report.Issues.Select(issue => issue.Code));

        // The geometry bundle keeps both alternatives x both skins, all vertices and bone weights.
        var geom = Assert.Single(result.Files.Where(file => file.Path == "geometry/model.hd2geom"));
        var geometry = GmodGeometryCodec.Read(geom.Data.Span);
        Assert.Equal("fixture.mdl", geometry.ModelPath);
        Assert.Single(geometry.Bones);
        Assert.Equal(2, geometry.Meshes.Count); // the second bodygroup alternative is blank
        Assert.Equal(2, geometry.SkinFamilyCount);
        Assert.Equal(4, geometry.Options.Count);
        Assert.All(geometry.Meshes, mesh => Assert.Equal(3, mesh.Vertices.Length));
        Assert.All(geometry.Meshes, mesh => Assert.Equal(new[] { 0, 1, 2 }, mesh.Indices));
        Assert.Equal("materials/fixture/base.vmt", geometry.Meshes[0].MaterialPath);
        Assert.Equal("materials/fixture/variant.vmt", geometry.Meshes[1].MaterialPath);

        // Materials + textures: decoded PNG, verbatim VTF/VMT and every addon source file.
        Assert.Contains(result.Files, file => file.Path == "textures/materials/textures/base.png" && file.Kind == "texture");
        Assert.Contains(result.Files, file => file.Path.EndsWith("base.vtf", StringComparison.Ordinal) && file.Kind == "texture-raw");
        Assert.Contains(result.Files, file => file.Kind == "material-raw");
        Assert.Contains(result.Files, file => file.Path == "source/index.json");
        Assert.Contains(result.Files, file => file.Path == "source/addon.txt");
        Assert.Equal(1, result.Report.DecodedTextureCount);
        Assert.Equal(2, result.Report.MaterialCount);
        Assert.Equal(4, result.Report.RigProfileCount);
        Assert.Equal(8, result.Report.RawSourceFileCount);

        // Reports and checksum manifest are self-consistent.
        Assert.Contains(result.Files, file => file.Path == "conversion-report.json");
        var manifest = Assert.Single(result.Files.Where(file => file.Path == "manifest.json"));
        Assert.Contains("HD2GmodIasConversion1", Encoding.UTF8.GetString(manifest.Data.Span));
        Assert.All(result.Files, file => Assert.Equal(64, file.Sha256.Length));

        // 每个已发布 Unit 都写进游戏原生补丁，并给出可直接导入的 data 模组包。
        var patch = Assert.Single(result.Files.Where(file => file.Kind == "patch"));
        Assert.Equal("package/data/9ba626afa44a3aa3.patch_0", patch.Path);
        Assert.Contains(result.Files, file => file.Path == "package/data/9ba626afa44a3aa3.patch_0.gpu_resources");
        // stream 为空也必须落盘：官方 PACK_SUFFIXES 三个文件缺一不可。
        var streamFile = Assert.Single(result.Files.Where(file => file.Kind == "patch-stream"));
        Assert.Equal("package/data/9ba626afa44a3aa3.patch_0.stream", streamFile.Path);
        Assert.True(streamFile.Data.IsEmpty);
        var modManifest = Assert.Single(result.Files.Where(file => file.Kind == "mod-manifest"));
        Assert.Contains("\"Version\": 1", Encoding.UTF8.GetString(modManifest.Data.Span));
        Assert.Contains("\"Include\"", Encoding.UTF8.GetString(modManifest.Data.Span));
        Assert.Contains(result.Files, file => file.Path == "runnable.json");
        Assert.Equal(4, result.Report.Parts[0].UnitCount);
        Assert.Equal(2, result.Report.Parts[0].MaterialResourceCount);
        Assert.DoesNotContain("UnitMeshAuthoringRequired", result.Report.Issues.Select(issue => issue.Code));
    }

    [Fact]
    public void Convert_SingleSwitch_PublishesIndependentIasPart()
    {
        using var provider = Provider();
        var package = SourceModelPackage.Load(provider, "fixture.mdl");
        var options = Options() with { IncludeSkinSwitch = false };
        var result = new GmodIasConverter().Convert(package, options, provider);

        Assert.Equal(GmodSwitchKind.BodyGroup, Assert.Single(result.Switches).Kind);
        Assert.Equal(2, result.Report.OptionCount);
        var difference = Assert.Single(result.Files.Where(file => file.Path.EndsWith(".difference.ini", StringComparison.Ordinal)));
        Assert.Equal(2, Assert.Single(IasFreePartsCodec.Read(difference.Data.Span, Binding()).Parts).Options.Count);
    }

    [Fact]
    public void Convert_MultipleIndependentSwitchesWithoutAllocation_RejectsOverlappingRoutes()
    {
        using var provider = Provider();
        var package = SourceModelPackage.Load(provider, "fixture.mdl");
        Assert.Throws<IasFormatException>(() =>
            new GmodIasConverter().Convert(package, Options(GmodSwitchMode.Independent), provider));
    }

    [Fact]
    public void Convert_PreCancelled_Throws()
    {
        using var provider = Provider();
        var package = SourceModelPackage.Load(provider, "fixture.mdl");
        Assert.Throws<OperationCanceledException>(() =>
            new GmodIasConverter().Convert(package, Options(), provider, new CancellationToken(true)));
    }

    [Fact]
    public async Task ConvertAsync_LoadsProviderAndProducesSameResultShape()
    {
        using var provider = Provider();
        var result = await new GmodIasConverter().ConvertAsync(provider, "fixture.mdl", Options());
        Assert.Equal("fixture.mdl", result.Report.ModelPath);
        Assert.NotEmpty(result.Files);
    }

    [Fact]
    public void Convert_DefaultSplit_EmitsBodyOnlyWhenNoHeadGeometryExists()
    {
        using var provider = Provider();
        var package = SourceModelPackage.Load(provider, "fixture.mdl");
        var options = new GmodConversionOptions("gmod-fixture", Binding());

        var result = new GmodIasConverter().Convert(package, options, provider);

        var part = Assert.Single(result.Parts);
        Assert.Equal(GmodPartKind.Body, part.Kind);
        Assert.Equal("body/", part.Prefix);
        Assert.Contains(result.Files, file => file.Path == "body/part-manifest.json");
        Assert.Contains(result.Files, file => file.Path == "body/geometry/model.hd2geom");
        Assert.DoesNotContain(result.Files, file => file.Path.StartsWith("helmet/", StringComparison.Ordinal));
        Assert.Contains("HeadBoneNotFound", result.Report.Issues.Select(issue => issue.Code));
        // 材料/贴图/源文件属于全局共享资源，仍写在根目录，避免两包重复。
        Assert.Contains(result.Files, file => file.Path == "manifest.json");
        Assert.Contains(result.Files, file => file.Path == "materials/materials.json");
    }

    [Fact]
    public void Convert_ExplicitBodySplit_FiltersToBodyMeshesOnly()
    {
        using var provider = Provider();
        var package = SourceModelPackage.Load(provider, "fixture.mdl");
        var options = new GmodConversionOptions("gmod-fixture", Binding(), PartSplit: GmodPartSplit.Body);

        var result = new GmodIasConverter().Convert(package, options, provider);

        var part = Assert.Single(result.Parts);
        Assert.Equal(GmodPartKind.Body, part.Kind);
        Assert.Empty(part.Prefix);
        Assert.Equal(2, part.Geometry.Meshes.Count);
    }

    [Fact]
    public void Convert_SwitchCap_KeepsFittingAxesAndReportsTheRest()
    {
        using var provider = Provider();
        var package = SourceModelPackage.Load(provider, "fixture.mdl");
        var options = Options() with { MaxSwitchOptions = 2 };

        var result = new GmodIasConverter().Convert(package, options, provider);

        // 2 bodygroup 备选 × 2 skin = 4 种组合，上限 2 时只保留其中一个开关并给出提示。
        Assert.Contains("SwitchOptionsCapped", result.Report.Issues.Select(issue => issue.Code));
        Assert.InRange(result.Report.OptionCount, 1, 2);
        var geometry = GmodGeometryCodec.Read(
            Assert.Single(result.Files.Where(file => file.Path == "geometry/model.hd2geom")).Data.Span);
        Assert.Equal(2, geometry.Meshes.Count);
    }

    private static byte[] Vtf()
    {
        var data = new byte[80];
        SourceFixture.Text(data, 0, "VTF\0");
        SourceFixture.Int(data, 4, 7);   // major
        SourceFixture.Int(data, 8, 1);   // minor
        SourceFixture.Int(data, 12, 64); // header size
        SourceFixture.Short(data, 16, 2);
        SourceFixture.Short(data, 18, 2);
        SourceFixture.Int(data, 20, 0);  // flags
        SourceFixture.Short(data, 24, 1);
        SourceFixture.Int(data, 52, 0);  // RGBA8888
        data[56] = 1;                    // one mip
        SourceFixture.Int(data, 57, 0);  // low-res format
        for (var i = 0; i < 16; i++) data[64 + i] = (byte)(i * 17);
        return data;
    }

    private static byte[] Phy()
    {
        var data = new byte[16];
        SourceFixture.Int(data, 0, 16);
        SourceFixture.Text(data, 4, "VPHY");
        SourceFixture.Short(data, 8, 1);
        SourceFixture.Int(data, 12, 2); // two declared solids
        return data;
    }

    private sealed class MemoryAssetProvider : ISourceAssetProvider
    {
        private readonly Dictionary<string, byte[]> _files;

        public MemoryAssetProvider(IEnumerable<KeyValuePair<string, byte[]>> files) =>
            _files = files.ToDictionary(pair => GmaArchive.NormalizePath(pair.Key), pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);

        public IEnumerable<string> EnumeratePaths(CancellationToken cancellationToken = default)
        {
            foreach (var path in _files.Keys.OrderBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return path;
            }
        }

        public bool TryRead(string relativePath, out byte[] data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _files.TryGetValue(GmaArchive.NormalizePath(relativePath), out data!);
        }

        public void Dispose() { }
    }
}
