using HD2Mimnt.Core.Darktide;
using HD2Mimnt.Infrastructure.Darktide;

namespace HD2Mimnt.Tests.Darktide;

public sealed class DarktideReleaseParserTests
{
    private const string FullUrl = "https://github.com/user-attachments/files/29280059/Darktide-Mod-Loader.zip";
    private const string UpdateUrl = "https://github.com/user-attachments/files/29280052/Darktide-Mod-Loader-UpdateOnly.zip";

    [Fact]
    public void Parse_EmptyAssets_ParsesBodyMarkdownLinks()
    {
        var json = $$"""
        {
          "tag_name": "26.06.24",
          "name": "26.06.24",
          "published_at": "2026-06-24T06:05:25Z",
          "html_url": "https://github.com/Darktide-Mod-Framework/Darktide-Mod-Loader/releases/tag/26.06.24",
          "assets": [],
          "body": "### Preserve mod_load_order.txt\n\n[Darktide-Mod-Loader.zip]({{FullUrl}})\n\n[Darktide-Mod-Loader-UpdateOnly.zip]({{UpdateUrl}})\n"
        }
        """;

        var release = DarktideReleaseParser.Parse(json);

        Assert.Null(release.Error);
        Assert.Equal("26.06.24", release.Tag);
        Assert.True(release.HasFullPackage);
        Assert.True(release.HasUpdateOnlyPackage);
        Assert.Equal(DarktideReleasePackageType.UpdateOnly,
            release.Packages.Single(package => package.FileName.Contains("UpdateOnly")).Type);
    }

    [Fact]
    public void Parse_AssetsPresent_UsesAssetsAndIgnoresBodyLinks()
    {
        var json = $$"""
        {
          "tag_name": "v9",
          "assets": [
            { "name": "DML.zip", "browser_download_url": "https://github.com/Darktide-Mod-Framework/Darktide-Mod-Loader/releases/download/v9/DML.zip" }
          ],
          "body": "[evil]({{FullUrl}})"
        }
        """;

        var release = DarktideReleaseParser.Parse(json);

        Assert.Null(release.Error);
        var package = Assert.Single(release.Packages);
        Assert.Equal("asset", package.Source);
        Assert.Equal(DarktideReleasePackageType.Full, package.Type);
    }

    [Fact]
    public void Parse_NonOfficialBodyLink_IsIgnored()
    {
        var json = """
        {
          "tag_name": "v1",
          "assets": [],
          "body": "[DML.zip](https://evil.example.com/Darktide-Mod-Loader.zip)"
        }
        """;

        var release = DarktideReleaseParser.Parse(json);

        Assert.NotNull(release.Error);
        Assert.Empty(release.Packages);
    }

    [Fact]
    public void Parse_TwoDifferentLinksForSameType_Stops()
    {
        var json = $$"""
        {
          "tag_name": "v1",
          "assets": [],
          "body": "[DML.zip]({{FullUrl}}) [DML2.zip](https://github.com/user-attachments/files/1/Other.zip)"
        }
        """;

        var release = DarktideReleaseParser.Parse(json);

        Assert.NotNull(release.Error);
        Assert.Empty(release.Packages);
    }

    [Fact]
    public void Parse_NoZipLinks_Stops()
    {
        var json = """{ "tag_name": "v1", "assets": [], "body": "no links" }""";

        var release = DarktideReleaseParser.Parse(json);

        Assert.NotNull(release.Error);
        Assert.False(release.HasFullPackage);
    }

    [Theory]
    [InlineData("https://github.com/user-attachments/files/1/a.zip", true)]
    [InlineData("https://github.com/Darktide-Mod-Framework/Darktide-Mod-Loader/releases/download/v1/a.zip", true)]
    [InlineData("http://github.com/user-attachments/files/1/a.zip", false)]
    [InlineData("https://evil.example.com/a.zip", false)]
    [InlineData("https://github.com/other/repo/a.zip", false)]
    public void IsOfficialDownloadUrl_ValidatesHostAndPath(string url, bool expected)
        => Assert.Equal(expected, DarktideReleaseParser.IsOfficialDownloadUrl(url));

    [Fact]
    public async Task DownloadPackage_RejectsNonOfficialUrlWithoutNetwork()
    {
        using var client = new HttpClient();
        var service = new DarktideReleaseClient(client);
        var package = new DarktideReleasePackage(DarktideReleasePackageType.Full, "x.zip",
            "https://evil.example.com/x.zip", "body");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DownloadPackageAsync(package, Path.GetTempPath()));
    }
}
