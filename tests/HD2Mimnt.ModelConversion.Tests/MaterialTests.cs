using System.Text;
using HD2Mimnt.ModelConversion.Source;
using HD2Mimnt.ModelConversion.Source.Vmt;
using HD2Mimnt.ModelConversion.Source.Vtf;

namespace HD2Mimnt.ModelConversion.Tests;

public sealed class MaterialTests
{
    [Fact]
    public void Vmt_ParametersConditionsAndProxies_PreserveStructureAndBackslashes()
    {
        var bytes = Encoding.UTF8.GetBytes("""
            "VertexLitGeneric" {
                "$basetexture" "some\texture"
                "$basetexture" "other" [$WIN32]
                "$translucent" "1" // not an alpha clip material
                "$alphatest" "0"
                "$color" [1 0.5 0]
                "$custom" "unknown" /* keep unknown fields */
                "Proxies" { "AnimatedTexture" { "animatedtexturevar" "$basetexture" } }
            }
            """);
        var vmt = VmtFile.Parse(bytes);
        Assert.Equal(bytes, vmt.RawData.ToArray());
        Assert.Equal("some\\texture", vmt.GetParameter("$basetexture"));
        Assert.Equal("1", vmt.GetParameter("$translucent"));
        Assert.Equal("0", vmt.GetParameter("$alphatest"));
        Assert.Equal("unknown", vmt.GetParameter("$custom"));
        Assert.Equal("[1 0.5 0]", vmt.GetParameter("$color"));
        Assert.Equal(2, vmt.TextureReferences.Count());
        Assert.Equal("[$WIN32]", vmt.TextureReferences.Last().Condition);
        Assert.Equal("AnimatedTexture", Assert.Single(vmt.Root.Children.Single(n => n.Key == "Proxies").Children).Key);
    }

    [Theory]
    [InlineData("\"shader\" { \"key\" }")]
    [InlineData("\"shader\" { \"key\" \"unterminated")]
    [InlineData("\"shader\" { /* missing terminator")]
    [InlineData("\"shader\" { key value ")]
    public void Vmt_TruncatedSyntax_Rejects(string text) =>
        Assert.Throws<SourceFormatException>(() => VmtFile.Parse(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Vtf_MultipleFramesAndMips_UsesSmallestMipFirstAndRetainsBytes()
    {
        var bytes = Texture(3, 2);
        var vtf = VtfFile.Parse(bytes);
        Assert.Equal(bytes, vtf.RawData.ToArray());
        Assert.Equal(6, vtf.Images.Count);
        Assert.Equal(new[] { 2, 2, 1, 1, 0, 0 }, vtf.Images.Select(image => image.Mip));
        Assert.Equal(new[] { 0, 1, 0, 1, 0, 1 }, vtf.Images.Select(image => image.Frame));
        Assert.Equal(new[] { 96, 100, 104, 120, 136, 200 }, vtf.Images.Select(image => image.Offset));
        Assert.Equal(bytes[200..264], vtf.GetImageData(vtf.Images[5]).ToArray());
        Assert.Equal(2, vtf.Resources.Count);
        Assert.Empty(vtf.Warnings);
    }

    [Fact]
    public void Vtf_BlockCompression_KeepsMinimumBlockSizeForSmallMips()
    {
        var data = new byte[96];
        Header(data, 2, 80, 2, 2, 1, 2, 13);
        var vtf = VtfFile.Parse(data);
        Assert.Equal(new[] { 8, 8 }, vtf.Images.Select(image => image.Length));
        Assert.Equal(new[] { 80, 88 }, vtf.Images.Select(image => image.Offset));
    }

    [Fact]
    public void Vtf_TruncatedLastMipAndInvalidResourceOffset_Rejects()
    {
        Assert.Throws<SourceFormatException>(() => VtfFile.Parse(Texture(3, 2)[..^1]));
        var bytes = Texture(3, 1); SourceFixture.Int(bytes, 84, int.MaxValue);
        Assert.Throws<SourceFormatException>(() => VtfFile.Parse(bytes));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(500)]
    public void Vtf_UnverifiedFormat_RetainsRawWithoutInventedImageLayout(int format)
    {
        var bytes = Texture(2, 1); SourceFixture.Int(bytes, 52, format);
        var vtf = VtfFile.Parse(bytes);
        Assert.Equal(bytes, vtf.RawData.ToArray()); Assert.Empty(vtf.Images); Assert.Single(vtf.Warnings);
    }

    [Fact]
    public void Vtf_Cubemap_RetainsRawAndExplicitlyMarksLayoutUnverified()
    {
        var bytes = Texture(2, 1); SourceFixture.Int(bytes, 20, 0x4000);
        var vtf = VtfFile.Parse(bytes);
        Assert.True(vtf.IsCubeMap); Assert.False(vtf.HasImageLayout); Assert.Single(vtf.Warnings);
        Assert.Equal(bytes, vtf.RawData.ToArray());
    }

    [Fact]
    public void Package_AllAlternativesMaterialsIncludesAndTextures_AreRead()
    {
        using var provider = Assets();
        provider.Files["materials/fixture/base.vmt"] = Encoding.UTF8.GetBytes("patch { include \"materials/shared.vmt\" replace { $basetexture replacement } }");
        provider.Files["materials/shared.vmt"] = Encoding.UTF8.GetBytes("VertexLitGeneric { $basetexture original $bumpmap missing_normal }");
        provider.Files["materials/fixture/variant.vmt"] = Encoding.UTF8.GetBytes("UnlitGeneric { $basetexture replacement $translucent 1 }");
        provider.Files["materials/original.vtf"] = Texture(2, 1);
        provider.Files["materials/replacement.vtf"] = Texture(3, 1);
        var package = SourceModelPackage.Load(provider, "models/test.mdl");
        Assert.Equal(2, package.Alternatives.Count);
        Assert.Equal(3, package.Materials.Count);
        Assert.Equal(2, package.Textures.Count);
        Assert.Contains(package.Issues, issue => issue.Code == "MissingTexture" && issue.AssetPath == "materials/missing_normal.vtf");
        Assert.Contains(package.Issues, issue => issue.Code == "MaterialEvaluationRequired");
        Assert.Null(package.PhysicsData);
    }

    [Fact]
    public void Package_RootSearchAndUnknownTexture_KeepActualPathsAndRawData()
    {
        using var provider = Assets();
        provider.Files["materials/base.vmt"] = Encoding.UTF8.GetBytes("shader { $basetexture unsupported }");
        provider.Files["materials/unsupported.vtf"] = [1, 2, 3];
        var package = SourceModelPackage.Load(provider, "models/test.mdl");
        Assert.Equal("materials/base.vmt", package.MaterialPaths[0]);
        Assert.Null(package.MaterialPaths[1]);
        Assert.Equal(new byte[] { 1, 2, 3 }, package.Textures["materials/unsupported.vtf"].RawData.ToArray());
        Assert.Contains(package.Issues, issue => issue.Code == "MissingMaterial");
        Assert.Contains(package.Issues, issue => issue.Code == "UnsupportedTexture");
    }

    [Fact]
    public void Package_IncludeCycle_Rejects()
    {
        using var provider = Assets();
        provider.Files["materials/fixture/base.vmt"] = Encoding.UTF8.GetBytes("patch { include \"fixture/base\" }");
        Assert.Throws<SourceFormatException>(() => SourceModelPackage.Load(provider, "models/test.mdl"));
    }

    [Fact]
    public async Task Package_Cancellation_IsNotConvertedToWarning()
    {
        using var provider = Assets();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SourceModelPackage.LoadAsync(provider, "models/test.mdl", new CancellationToken(true)));
    }

    internal static byte[] Texture(int minor, int frames)
    {
        var start = minor >= 3 ? 96 : 80;
        var bytes = new byte[start + 84 * frames];
        Header(bytes, minor, start, 4, 4, frames, 3, 0);
        if (minor >= 3)
        {
            SourceFixture.Int(bytes, 68, 2);
            SourceFixture.Int(bytes, 80, 0x30); SourceFixture.Int(bytes, 84, start);
            SourceFixture.Int(bytes, 88, 0x02001234); SourceFixture.Int(bytes, 92, 123);
        }
        for (var i = start; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }

    private static void Header(byte[] bytes, int minor, int headerSize, int width, int height, int frames, int mips, int format)
    {
        SourceFixture.Text(bytes, 0, "VTF\0"); SourceFixture.Int(bytes, 4, 7); SourceFixture.Int(bytes, 8, minor);
        SourceFixture.Int(bytes, 12, headerSize); SourceFixture.Short(bytes, 16, (ushort)width); SourceFixture.Short(bytes, 18, (ushort)height);
        SourceFixture.Short(bytes, 24, (ushort)frames); SourceFixture.Int(bytes, 52, format); bytes[56] = (byte)mips;
        SourceFixture.Int(bytes, 57, -1); SourceFixture.Short(bytes, 63, 1);
    }

    private static MemoryAssets Assets() => new(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["models/test.mdl"] = SourceFixture.Mdl(), ["models/test.vvd"] = SourceFixture.Vvd(), ["models/test.dx90.vtx"] = SourceFixture.Vtx()
    });

    private sealed class MemoryAssets(Dictionary<string, byte[]> files) : ISourceAssetProvider
    {
        public Dictionary<string, byte[]> Files { get; } = files;
        public IEnumerable<string> EnumeratePaths(CancellationToken cancellationToken = default) => Files.Keys;
        public bool TryRead(string path, out byte[] data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Files.TryGetValue(path, out data!);
        }
        public void Dispose() { }
    }
}
