using System.Text;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Infrastructure.Archive;
using Xunit;

namespace HD2Mimnt.Tests.Services;

public sealed class Hd2ClArchiveExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-Export-{Guid.NewGuid():N}");

    public Hd2ClArchiveExportTests() => Directory.CreateDirectory(_root);

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void SanitizeFileName_ReplacesInvalidCharactersAndTrims()
    {
        Assert.Equal("VR_chat_AIRI__", Hd2ClArchiveExport.SanitizeFileName("VR:chat/AIRI*?"));
        Assert.Equal("带空格的名字", Hd2ClArchiveExport.SanitizeFileName(" 带空格的名字. "));
    }

    [Theory]
    [InlineData("", "HD2CL")]
    [InlineData("CON", "_CON")]
    [InlineData("con", "_con")]
    [InlineData("  ..  ", "HD2CL")]
    public void SanitizeFileName_FallsBackForEmptyOrReservedNames(string name, string expected)
        => Assert.Equal(expected, Hd2ClArchiveExport.SanitizeFileName(name));

    [Fact]
    public void ReservePath_AppendsSequenceInsteadOfOverwriting()
    {
        File.WriteAllText(Path.Combine(_root, "模型.7z"), "existing");
        var path = Hd2ClArchiveExport.ReservePath(_root, "模型");
        Assert.Equal(Path.Combine(_root, "模型 (2).7z"), path);
    }

    [Fact]
    public async Task WriteAsync_PacksDirectoryContentsAtArchiveRootAndLeavesNoTempFiles()
    {
        var artifact = Path.Combine(_root, "package");
        Directory.CreateDirectory(Path.Combine(artifact, "Material", "1", "1"));
        Directory.CreateDirectory(Path.Combine(artifact, "空目录"));
        await File.WriteAllTextAsync(Path.Combine(artifact, "manifest.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(artifact, "Material", "1", "1", "9ba626afa44a3aa3.patch_2"),
            "material patch");
        var output = Path.Combine(_root, "导出 目录");

        var archive = await Hd2ClArchiveExport.WriteAsync(output, "测试:模组", artifact, null, default);

        Assert.Equal(Path.Combine(output, "测试_模组.7z"), archive);
        Assert.Equal(new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c },
            (await File.ReadAllBytesAsync(archive))[..6]);
        using (var entries = SharpCompress.Archives.ArchiveFactory.OpenArchive(archive))
        {
            Assert.Single(entries.Entries, entry => entry.Key == "manifest.json");
            Assert.Single(entries.Entries, entry => entry.Key == "Material/1/1/9ba626afa44a3aa3.patch_2");
            Assert.Contains(entries.Entries, entry =>
                entry.IsDirectory && entry.Key!.TrimEnd('/', '\\') == "空目录");
            Assert.DoesNotContain(entries.Entries, entry => entry.Key!.StartsWith("package/"));
        }
        var extracted = Path.Combine(_root, "unpacked");
        await new SharpCompressArchiveExtractor().ExtractToDirectoryAsync(archive, extracted);
        Assert.Equal("{}", await File.ReadAllTextAsync(Path.Combine(extracted, "manifest.json")));
        Assert.Equal("material patch",
            await File.ReadAllTextAsync(Path.Combine(extracted, "Material", "1", "1", "9ba626afa44a3aa3.patch_2")));
        Assert.Empty(Directory.GetFiles(output, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void WithHd2ClMarker_AppendsOnceAndIsIdempotent()
    {
        Assert.Equal("模型 ▲", Hd2ClArchiveExport.WithHd2ClMarker("模型"));
        Assert.Equal("模型 ▲", Hd2ClArchiveExport.WithHd2ClMarker("模型 ▲"));
        Assert.Equal("模型 ▲", Hd2ClArchiveExport.WithHd2ClMarker("模型  "));
        Assert.Equal("HD2CL ▲", Hd2ClArchiveExport.WithHd2ClMarker("   "));
    }

    [Fact]
    public async Task WriteAsync_StampsManifestNameWithHd2ClMarkerAndKeepsOtherFields()
    {
        var artifact = Path.Combine(_root, "package");
        Directory.CreateDirectory(artifact);
        const string manifest = "{\n  \"Name\": \"测试:模组\",\n  \"Guid\": \"abc-123\",\n  \"Options\": [{\"Name\": \"红\"}]\n}";
        await File.WriteAllTextAsync(Path.Combine(artifact, "manifest.json"), manifest, new UTF8Encoding(false));
        var output = Path.Combine(_root, "导出 目录");

        var archive = await Hd2ClArchiveExport.WriteAsync(output,
            Hd2ClArchiveExport.WithHd2ClMarker("测试:模组"), artifact, null, default);

        Assert.Equal(Path.Combine(output, "测试_模组 ▲.7z"), archive);
        var extracted = Path.Combine(_root, "unpacked-stamped");
        await new SharpCompressArchiveExtractor().ExtractToDirectoryAsync(archive, extracted);
        var stamped = await File.ReadAllTextAsync(Path.Combine(extracted, "manifest.json"));
        Assert.Contains("\"Name\": \"测试:模组 ▲\"", stamped);
        Assert.Contains("\"Guid\": \"abc-123\"", stamped);
        Assert.Contains("{\"Name\": \"红\"}", stamped);
    }

    [Fact]
    public async Task StampManifestNameAsync_LeavesOtherManifestBytesUntouched()
    {
        var artifact = Path.Combine(_root, "package-stamp");
        Directory.CreateDirectory(artifact);
        const string manifest = "{\n  \"Name\": \"原名\",\n  \"Guid\": \"g\"\n}";
        var path = Path.Combine(artifact, "manifest.json");
        await File.WriteAllTextAsync(path, manifest, new UTF8Encoding(false));

        await Hd2ClArchiveExport.StampManifestNameAsync(artifact, "新名 ▲", default);

        Assert.Equal("{\n  \"Name\": \"新名 ▲\",\n  \"Guid\": \"g\"\n}",
            await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task StampManifestNameAsync_QuotesEscapesAndLeavesNestedNameAlone()
    {
        var artifact = Path.Combine(_root, "package-quote");
        Directory.CreateDirectory(artifact);
        const string manifest = "{\n  \"Name\": \"原名\",\n  \"Options\": [{\"Name\": \"子名\"}]\n}";
        var path = Path.Combine(artifact, "manifest.json");
        await File.WriteAllTextAsync(path, manifest, new UTF8Encoding(false));

        await Hd2ClArchiveExport.StampManifestNameAsync(artifact, "含\"引号\" ▲", default);

        var updated = await File.ReadAllTextAsync(path);
        Assert.Contains("\"Name\": \"含\\\"引号\\\" ▲\"", updated);
        Assert.Contains("{\"Name\": \"子名\"}", updated);
    }

    [Fact]
    public async Task WriteAsync_Cancelled_LeavesNoArchiveOrTempFiles()
    {
        var artifact = Path.Combine(_root, "package");
        Directory.CreateDirectory(artifact);
        await File.WriteAllTextAsync(Path.Combine(artifact, "manifest.json"), "{}");
        var output = Path.Combine(_root, "out");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Hd2ClArchiveExport.WriteAsync(output, "模型", artifact, null, cancellation.Token));

        Assert.Empty(Directory.GetFiles(output, "*", SearchOption.AllDirectories));
    }
}
