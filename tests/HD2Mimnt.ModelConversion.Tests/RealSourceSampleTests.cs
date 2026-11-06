using System.Buffers.Binary;
using System.Security.Cryptography;
using HD2Mimnt.ModelConversion.Source;
using Xunit.Abstractions;

namespace HD2Mimnt.ModelConversion.Tests;

public sealed class RealSourceSampleTests(RealSourceSample sample, ITestOutputHelper output) : IClassFixture<RealSourceSample>
{
    [SampleFact]
    public void Gma_RealModel_PreservesAllAlternativesAndJiggleBones()
    {
        var package = sample.Package!;
        var model = package.Model;
        Assert.Equal(48, model.Mdl.Header.Version);
        Assert.Equal(43233072u, model.Mdl.Header.Checksum);
        Assert.Equal(255, model.Mdl.Bones.Count);
        Assert.Equal(94_858, model.Vvd.Pool.Length);
        Assert.Equal(20, model.Mdl.BodyParts.Count);
        Assert.Equal(42, package.Alternatives.Count);
        Assert.Equal(186, model.Mdl.Bones.Count(bone => bone.Jiggle is not null));
        Assert.Equal(17, model.Mdl.Textures.Count);
        Assert.Equal(17, package.MaterialPaths.Count);
        Assert.All(package.MaterialPaths.Values, Assert.NotNull);
        Assert.DoesNotContain(package.Issues, issue => issue.Code is "UnsupportedTexture" or "UnsupportedMaterial");
        Assert.All(package.Textures.Values, asset => Assert.True(asset.Texture?.HasImageLayout));
        output.WriteLine($"materials={package.Materials.Count}, textures={package.Textures.Count}, alternatives={package.Alternatives.Count}");
        foreach (var issue in package.Issues) output.WriteLine($"{issue.Code}: {issue.AssetPath}: {issue.Message}");
    }

    [SampleFact]
    public void DefaultGeometry_MatchesPythonTriangleOrderAndCounts()
    {
        VerifyGeometry(sample.Package!.Model.GetGeometry(), 106_663, 64_301,
            "BCB4B95BEAF79450FDC8DFCF47A4EA0085B58FF998E69629CD3A05765957E592");
    }

    [SampleFact]
    public void SwitchedGeometry_MatchesPythonTriangleOrderAndCounts()
    {
        var choices = new[] { 1, 2, 3, 4, 16, 17, 18, 19 }.ToDictionary(i => i, _ => 1);
        VerifyGeometry(sample.Package!.Model.GetGeometry(choices), 114_353, 69_307,
            "E7C47CB9188E7AEB0D80DBE95D240A6E81BFC195BB843E6CE2D6CEF7960CDC1A");
    }

    [SampleFact]
    public void TextureSubresources_AccountForEveryMipByte()
    {
        foreach (var asset in sample.Package!.Textures.Values)
        {
            var texture = asset.Texture!;
            Assert.Equal(0, texture.Format);
            Assert.Equal(1, texture.Frames);
            Assert.Equal(texture.MipCount, texture.Images.Count);
            var cursor = texture.Images[0].Offset;
            foreach (var image in texture.Images)
            {
                Assert.Equal(cursor, image.Offset);
                Assert.Equal(image.Width * image.Height * 4, image.Length);
                cursor += image.Length;
            }
            Assert.Equal(asset.RawData.Length, cursor);
        }
    }

    private static void VerifyGeometry(IReadOnlyList<SourceModelGeometry> geometry, int triangles, int vertices, string sha256)
    {
        var indices = geometry.SelectMany(part => part.SubMeshes).SelectMany(mesh => mesh.Indices).ToArray();
        Assert.Equal(triangles * 3, indices.Length);
        Assert.Equal(vertices, indices.Distinct().Count());
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[4];
        foreach (var index in indices)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, index);
            hash.AppendData(buffer);
        }
        Assert.Equal(sha256, Convert.ToHexString(hash.GetHashAndReset()));
    }
}

public sealed class RealSourceSample
{
    public SourceModelPackage? Package { get; }
    public RealSourceSample()
    {
        var path = FindSample();
        if (path is null) return;
        using var provider = new GmaAssetProvider(path);
        Package = SourceModelPackage.Load(provider, "models/lc_84/aes_yz_pm.mdl");
    }

    public static string? FindSample()
    {
        var configured = Environment.GetEnvironmentVariable("HD2_SOURCE_SAMPLE");
        if (!string.IsNullOrEmpty(configured)) return File.Exists(configured) ? configured : null;
        for (var root = new DirectoryInfo(AppContext.BaseDirectory); root is not null; root = root.Parent)
        {
            var path = Path.Combine(root.FullName, "tools", "SourceModelReader", "gmpublisher.gma");
            if (File.Exists(path)) return path;
        }
        return null;
    }
}

public sealed class SampleFactAttribute : FactAttribute
{
    public SampleFactAttribute()
    {
        if (RealSourceSample.FindSample() is null)
            Skip = "Local aes_yz_pm GMA fixture is absent; set HD2_SOURCE_SAMPLE. This is not a synthetic substitute for the real-model check.";
    }
}
