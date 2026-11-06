using System.Buffers.Binary;
using System.Numerics;
using HD2Mimnt.ModelConversion.Gmod;
using HD2Mimnt.ModelConversion.Gmod.Packaging;
using HD2Mimnt.ModelConversion.Gmod.Parts;
using HD2Mimnt.ModelConversion.Hd2;
using HD2Mimnt.ModelConversion.Ias;
using HD2Mimnt.ModelConversion.Source;
using HD2Mimnt.ModelConversion.Source.Gma;

namespace HD2Mimnt.ModelConversion.Tests;

/// <summary>
/// GMod 转换结果 → 游戏原生 Stingray 资源 → 可运行 data 模组包。
/// 断言到字节层：补丁 TOC 的每个 Unit 都通过读取侧自检，材质/贴图条目与清单齐备。
/// </summary>
public sealed class GmodStingrayPackageTests
{
    private const ulong ArchiveId = 0x0b0d1e5;
    private const ulong NativeUnit = 0x101;
    private static readonly uint BasicMra = Hd2MaterialWriter.Semantics(Hd2MaterialFamily.Basic)[0];

    [Fact]
    public void Build_SyntheticPart_WritesOneUnitPerPublishedIdIntoThePatch()
    {
        var part = Part(("opt0", "First", 0xAAA, ["mesh0"]), ("opt1", "Second", 0xBBB, ["mesh0", "mesh1"]));

        var package = GmodStingrayPackager.Build(part, [Material()], [Texture()]);

        Assert.Equal(2, package.Units.Count);
        Assert.Equal(new[] { 0xAAAUL, 0xBBBUL }, package.Units.Select(unit => unit.UnitId).Order().ToArray());
        Assert.Equal(1, package.Summary.MaterialCount);
        Assert.Equal(3, package.Summary.TextureCount); // albedo + normal + mra
        Assert.Equal(2, package.Summary.UnitCount);

        // 补丁三件套必须齐：官方 PACK_SUFFIXES 是 ("", ".gpu_resources", ".stream")，
        // 读取侧无条件打开三个伴随文件，缺一个会让加载线程拿到空句柄。
        var patch = Assert.Single(package.Files.Where(file => file.Kind == "patch"));
        Assert.Equal("package/data/9ba626afa44a3aa3.patch_0", patch.Path);
        Assert.Contains(package.Files, file => file.Path.EndsWith(".gpu_resources", StringComparison.Ordinal));
        var stream = Assert.Single(package.Files.Where(file => file.Kind == "patch-stream"));
        Assert.True(stream.Data.IsEmpty);

        var gpu = Assert.Single(package.Files.Where(file => file.Kind == "patch-gpu")).Data;
        foreach (var entry in ReadToc(patch.Data))
        {
            if (entry.TypeId != StingrayTypeIds.Unit) continue;
            // 补丁里的每个 Unit 都必须通过与游戏读取侧同构的自检，否则宁可失败也不产出半成品。
            var main = patch.Data.Span[(int)entry.MainOffset..(int)(entry.MainOffset + entry.MainSize)];
            var unitGpu = gpu.Span[(int)entry.GpuOffset..(int)(entry.GpuOffset + entry.GpuSize)];
            Assert.Empty(Hd2UnitInspector.Validate(main, unitGpu));
        }
    }

    [Fact]
    public void Build_PatchToc_GroupsTypesAndCarriesEveryEntry()
    {
        var part = Part(("opt0", "First", 0xAAA, ["mesh0"]));

        var package = GmodStingrayPackager.Build(part, [Material()], [Texture()]);
        var patch = Assert.Single(package.Files.Where(file => file.Kind == "patch"));
        var entries = ReadToc(patch.Data);

        Assert.Equal(0xF0000011u, BinaryPrimitives.ReadUInt32LittleEndian(patch.Data.Span));
        Assert.Contains(entries, entry => entry.TypeId == StingrayTypeIds.Unit && entry.FileId == 0xAAA);
        Assert.Contains(entries, entry => entry.TypeId == StingrayTypeIds.Material);
        Assert.Equal(3, entries.Count(entry => entry.TypeId == StingrayTypeIds.Texture));
        Assert.All(entries, entry =>
        {
            Assert.True(entry.MainOffset >= 72 + 32 + 80);
            Assert.True(entry.MainSize > 0);
            Assert.InRange(entry.Index, 1, entries.Count);
        });
    }

    [Fact]
    public void Build_AlphaTestedMaterial_UsesAlphaClipFamilyAndKeepsClipAlpha()
    {
        var material = Material(("$alphatest", "1"));
        var part = Part(("opt0", "First", 0xAAA, ["mesh0"]));

        var package = GmodStingrayPackager.Build(part, [material], [Texture()]);

        var resource = Assert.Single(package.Materials);
        Assert.Equal("alphaclip", resource.Family);
        Assert.Equal(3, resource.TextureCount);
        var clip = Assert.Single(package.Textures.Where(texture => texture.Role == "opacity"));
        var colorMetal = Assert.Single(package.Textures.Where(texture => texture.Role == "base_color_metal"));
        Assert.Equal("BC1", clip.Format);       // 裁切通道是灰度图，不需要 alpha
        Assert.Equal("BC3", colorMetal.Format); // 颜色图的 A 承载金属度，必须保留 alpha
    }

    [Fact]
    public void Build_MissingSourceTextures_FallsBackToNeutralMaps()
    {
        var part = Part(("opt0", "First", 0xAAA, ["mesh0"]));

        var package = GmodStingrayPackager.Build(part, [Material()], []);

        Assert.Equal(3, package.Summary.TextureCount);
        // 缺失贴图必须报出来并用中性通道兜底，而不是静默产出一张黑图。
        Assert.Equal(2, package.Issues.Count(issue => issue.Code == "TextureNotConverted"));
        Assert.Equal("basic", Assert.Single(package.Materials).Family);
        Assert.Contains("TextureChannelApproximation", package.Issues.Select(issue => issue.Code));
    }

    [Fact]
    public void Build_OptionWithoutMeshes_WritesValidPlaceholderUnit()
    {
        var part = Part(("opt0", "First", 0xAAA, ["mesh0"]), ("opt1", "Hidden", 0xBBB, []));

        var package = GmodStingrayPackager.Build(part, [Material()], [Texture()]);

        var placeholder = Assert.Single(package.Units.Where(unit => unit.Placeholder));
        Assert.Equal(0xBBBUL, placeholder.UnitId);
        Assert.Equal(1, placeholder.MeshCount);
        Assert.Contains("EmptySwitchOption", package.Issues.Select(issue => issue.Code));
    }

    [Fact]
    public void Build_IdenticalOptions_ShareOnePayload()
    {
        var part = Part(("opt0", "First", 0xAAA, ["mesh0"]), ("opt1", "Same", 0xBBB, ["mesh0"]));

        var package = GmodStingrayPackager.Build(part, [Material()], [Texture()]);

        Assert.Equal(2, package.Summary.UnitCount);
        Assert.Equal(1, package.Summary.RelocatedPayloads);
        var patch = Assert.Single(package.Files.Where(file => file.Kind == "patch"));
        var unitEntries = ReadToc(patch.Data).Where(entry => entry.TypeId == StingrayTypeIds.Unit).ToArray();
        Assert.Equal(2, unitEntries.Length);
        // 两个 Unit 各自是独立资源（IAS 路由需要），但可以指向同一份主数据载荷。
        Assert.Equal(unitEntries[0].MainOffset, unitEntries[1].MainOffset);
    }

    [Fact]
    public void Build_EmitsStandardManifestAndExtension()
    {
        var part = Part(("opt0", "First", 0xAAA, ["mesh0"]));

        var package = GmodStingrayPackager.Build(part, [Material()], [Texture()]);

        var manifest = Assert.Single(package.Files.Where(file => file.Kind == "mod-manifest"));
        var json = System.Text.Encoding.UTF8.GetString(manifest.Data.Span);
        Assert.Equal("package/manifest.json", manifest.Path);
        Assert.Contains("\"Version\": 1", json);
        Assert.Contains("\"Name\": \"data\"", json);
        Assert.Contains("\"Include\": [", json);
        Assert.Contains("\"Guid\": \"", json);

        var metadata = Assert.Single(package.Files.Where(file => file.Kind == "mod-metadata"));
        var metadataJson = System.Text.Encoding.UTF8.GetString(metadata.Data.Span);
        Assert.Contains("\"SchemaVersion\": 1", metadataJson);
        Assert.Contains("\"GeneratedManifest\": true", metadataJson);
        Assert.Contains(package.Summary.Guid, metadataJson);

        // 同一输入必须得到同一个 GUID，重复导入才会更新而不是新建。
        Assert.Equal(package.Summary.Guid,
            GmodStingrayPackager.Build(part, [Material()], [Texture()]).Summary.Guid);
    }

    [Fact]
    public void Packager_BasicSemantics_MatchVerifiedTemplateOrder()
    {
        // basic.material 的槽序是 mra / albedo / normal；顺序错了会把法线当颜色贴图。
        Assert.Equal(BasicMra, StingrayHash.Murmur32("mra"));
        Assert.Equal(0xAC652E43u, StingrayHash.Murmur32("albedo"));
        Assert.Equal(0xCAED6CD6u, StingrayHash.Murmur32("normal"));
        Assert.Equal(0xCBDE381Bu, StingrayHash.Murmur32("opacity_clip_map"));
        Assert.Equal(0x848BA63Bu, StingrayHash.Murmur32("base_color_metal_map"));
    }

    [Fact]
    public void TextureComposer_PadsToBlockMultipleAndKeepsChannels()
    {
        // 6×3 的图要补齐到 8×4，BC 编码器才能整块压缩。
        var pixels = new byte[6 * 3 * 4];
        for (var i = 0; i < 6 * 3; i++)
        {
            pixels[i * 4] = 10; pixels[i * 4 + 1] = 20; pixels[i * 4 + 2] = 30; pixels[i * 4 + 3] = 40;
        }
        var padded = Hd2TextureComposer.PadToBlocks(new Hd2TextureImage(6, 3, pixels));

        Assert.Equal(8, padded.Width);
        Assert.Equal(4, padded.Height);
        Assert.Equal(10, padded.Rgba[0]);
        Assert.Equal(40, padded.Rgba[3]);
        Assert.Equal(40, padded.Rgba[(7 + 3 * 8) * 4 + 3]); // 右下角按边缘复制补齐
        Assert.Equal(10, Hd2TextureComposer.SampleChannel(null, 0, 0, 0, 10));
        Assert.Equal(30, Hd2TextureComposer.SampleChannel(padded, 0, 0, 2, 0));
    }

    [Fact]
    public void ResolveFamily_OnlyFlagsTransparencyWhenTheShaderDeclaresIt()
    {
        Assert.Equal(Hd2MaterialFamily.Basic, GmodStingrayPackager.ResolveFamily(Material()));
        Assert.Equal(Hd2MaterialFamily.AlphaClip,
            GmodStingrayPackager.ResolveFamily(Material(("$alphatest", "1"))));
        Assert.Equal(Hd2MaterialFamily.AlphaClip,
            GmodStingrayPackager.ResolveFamily(Material(("$translucent", "\"1\""))));
        Assert.Equal(Hd2MaterialFamily.Basic, GmodStingrayPackager.ResolveFamily(Material(("$alphatest", "0"))));
    }

    [SampleFact]
    public void RealModel_EveryPublishedUnitIsWrittenAndPassesTheReaderChecks()
    {
        // 真实玩家模型（255 骨骼 / 19 组开关 / 186 个动骨）端到端跑一遍：
        // 断言 IAS 路由里的每个 Published Unit 都在补丁里，且都能通过读取侧自检。
        var sample = RealSourceSample.FindSample()!;
        using var provider = new GmaAssetProvider(sample);
        var package = SourceModelPackage.Load(provider, "models/shoubi/aes_yz_sb.mdl");
        var binding = new IasArchiveBinding(0x1122334455667788, IasContentDomain.Body,
            [new IasEquipmentBinding(1, [0xDEADBEEF])]);
        var result = new GmodIasConverter().Convert(package, new GmodConversionOptions("real-sample", binding,
            PartSplit: GmodPartSplit.None, IncludeSkinSwitch: false, IncludeRawSource: false), provider);

        var difference = IasFreePartsCodec.Read(
            Assert.Single(result.Files.Where(file => file.Path.EndsWith(".difference.ini", StringComparison.Ordinal))).Data.Span,
            binding);
        var published = difference.Parts.SelectMany(part => part.Options).SelectMany(option => option.Routes)
            .Select(route => route.PublishedUnitId).ToHashSet();

        var patch = Assert.Single(result.Files.Where(file => file.Kind == "patch")).Data;
        var gpu = Assert.Single(result.Files.Where(file => file.Kind == "patch-gpu")).Data;
        var units = ReadToc(patch).Where(entry => entry.TypeId == StingrayTypeIds.Unit).ToArray();
        Assert.Equal(published.Order().ToArray(), units.Select(entry => entry.FileId).Order().ToArray());
        Assert.All(units, entry =>
        {
            var main = patch.Span[(int)entry.MainOffset..(int)(entry.MainOffset + entry.MainSize)];
            Assert.Equal(Hd2UnitBuilder.UnitVersion, BinaryPrimitives.ReadUInt32LittleEndian(main[0x2C..]));
            var unitGpu = gpu.Span[(int)entry.GpuOffset..(int)(entry.GpuOffset + entry.GpuSize)];
            Assert.Empty(Hd2UnitInspector.Validate(main, unitGpu));
        });
        Assert.Contains(ReadToc(patch), entry => entry.TypeId == StingrayTypeIds.Material);
        Assert.Contains(ReadToc(patch), entry => entry.TypeId == StingrayTypeIds.Texture);
        Assert.Equal(units.Length, result.Report.Parts[0].UnitCount);
    }

    private static GmodMaterial Material(params (string Key, string Value)[] parameters) => new(
        "materials/fixture/base.vmt", "VertexLitGeneric", false, null,
        parameters.Select(parameter => new GmodMaterialParameter(parameter.Key, parameter.Value, null, null)).ToArray(),
        [
            new GmodMaterialTexture("BaseColor", "$basetexture", "textures/base.vtf", "textures/base.png"),
            new GmodMaterialTexture("Normal", "$bumpmap", "textures/normal.vtf", "textures/normal.png")
        ],
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BaseColor"] = "textures/base.vtf",
            ["Normal"] = "textures/normal.vtf"
        });

    private static GmodTexture Texture() => new("textures/base.vtf", 4, 4, 0, 1, false, null, null)
    {
        Rgba = Enumerable.Range(0, 4 * 4 * 4).Select(index => (byte)(index % 4 == 3 ? 128 : index * 3)).ToArray()
    };

    private static GmodConversionPart Part(params (string Id, string Label, ulong UnitId, string[] MeshIds)[] options)
    {
        var bones = new[]
        {
            new GmodBone(0, "ValveBiped.Bip01_Pelvis", -1, Vector3.Zero, Quaternion.Identity, [], 0, 0, 0, "", null),
            new GmodBone(1, "ValveBiped.Bip01_Spine", 0, new Vector3(0f, 2f, 0f), Quaternion.Identity, [], 0, 0, 0, "", null)
        };
        var meshes = new[]
        {
            Mesh("mesh0", 0),
            Mesh("mesh1", 1)
        };
        var geometries = options.Select(option => new GmodOptionGeometry(option.Id,
            new Dictionary<string, int>(StringComparer.Ordinal) { ["bodygroup:0"] = 0 },
            option.MeshIds)).ToArray();
        var geometry = new GmodGeometryBundle("fixture.mdl", ".dx90.vtx", bones, meshes, [], geometries, 1, 1, []);

        var freeOptions = options.Select(option => new IasFreeOption(option.Id, option.Label,
            [new IasUnitRoute(NativeUnit, option.UnitId, true)])).ToArray();
        var differences = new IasFreePartsDocument("fixture", IasContentDomain.Body, ArchiveId, [1],
            [new IasFreePart("composite", IasPartSlot.Torso, "Combined", freeOptions)]);

        return new GmodConversionPart("", GmodPartKind.Body, IasContentDomain.Body, ArchiveId, differences,
            null!, geometry, [new GmodSwitch("composite", "Combined", GmodSwitchKind.Composite,
                IasPartSlot.Torso, [])], []);
    }

    private static GmodMesh Mesh(string id, int bone) => new(id, 0, 0, 0, 0, 0, "materials/fixture/base.vmt",
        "fixture", [
            new GmodVertex(new Vector3(-1f, 0f, 0f), new Vector3(0f, 0f, 1f), new Vector2(0f, 0f), Vector4.Zero,
                [1f], [(byte)bone]),
            new GmodVertex(new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f), new Vector2(1f, 0f), Vector4.Zero,
                [1f], [(byte)bone]),
            new GmodVertex(new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f), new Vector2(0f, 1f), Vector4.Zero,
                [1f], [(byte)bone])
        ], [0, 1, 2]);

    private readonly record struct TocEntry(ulong FileId, ulong TypeId, long MainOffset, uint MainSize,
        long GpuOffset, uint GpuSize, int Index);

    /// <summary>按 Stingray 补丁 TOC 结构逐项读出条目（类型表 32 字节、条目表 80 字节）。</summary>
    private static IReadOnlyList<TocEntry> ReadToc(ReadOnlyMemory<byte> patch)
    {
        var span = patch.Span;
        Assert.Equal(0xF0000011u, BinaryPrimitives.ReadUInt32LittleEndian(span));
        var typeCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        var entryCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
        var entries = new List<TocEntry>(entryCount);
        var start = 72 + 32 * typeCount;
        for (var i = 0; i < entryCount; i++)
        {
            var offset = start + 80 * i;
            entries.Add(new TocEntry(
                BinaryPrimitives.ReadUInt64LittleEndian(span[offset..]),
                BinaryPrimitives.ReadUInt64LittleEndian(span[(offset + 8)..]),
                (long)BinaryPrimitives.ReadUInt64LittleEndian(span[(offset + 16)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(span[(offset + 56)..]),
                (long)BinaryPrimitives.ReadUInt64LittleEndian(span[(offset + 32)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(span[(offset + 64)..]),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(offset + 76)..])));
        }
        return entries;
    }
}
