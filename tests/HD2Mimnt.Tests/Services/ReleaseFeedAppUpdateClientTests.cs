using HD2Mimnt.Infrastructure.Updates;

namespace HD2Mimnt.Tests.Services;

public sealed class ReleaseFeedAppUpdateClientTests
{
    [Fact]
    public void Parse_GitHubRelease_KeepsAssetDigest()
    {
        const string json = """
            {
              "tag_name": "V0.3.2-alpha",
              "body": "notes",
              "html_url": "https://github.com/TYHH100/HD2Mimnt/releases/tag/V0.3.2-alpha",
              "published_at": "2026-11-20T10:00:00Z",
              "assets": [
                {
                  "name": "HD2Mimnt-0.3.2-alpha-win-x64.zip",
                  "browser_download_url": "https://github.com/TYHH100/HD2Mimnt/releases/download/V0.3.2-alpha/HD2Mimnt-0.3.2-alpha-win-x64.zip",
                  "digest": "sha256:6b370f0533c28e471cd84def4687a66a19326dbec11983c3ff2911bf4cc25b4c"
                }
              ]
            }
            """;

        var release = ReleaseFeedAppUpdateClient.Parse(json);

        Assert.NotNull(release);
        Assert.Equal("V0.3.2-alpha", release.TagName);
        Assert.Equal("notes", release.Body);
        var asset = Assert.Single(release.Assets);
        Assert.Equal("sha256:6b370f0533c28e471cd84def4687a66a19326dbec11983c3ff2911bf4cc25b4c", asset.Digest);
    }

    [Fact]
    public void Parse_ForgejoRelease_WithoutDigest_LeavesDigestNull()
    {
        const string json = """
            {
              "tag_name": "V0.3.2-alpha",
              "body": "notes",
              "html_url": "https://codeberg.org/TYHH10/HD2Mimnt/releases/tag/V0.3.2-alpha",
              "published_at": "2026-11-20T10:00:00+01:00",
              "assets": [
                {
                  "name": "HD2Mimnt-0.3.2-alpha-win-x64.zip",
                  "browser_download_url": "https://codeberg.org/TYHH10/HD2Mimnt/releases/download/V0.3.2-alpha/HD2Mimnt-0.3.2-alpha-win-x64.zip",
                  "size": 12345678
                }
              ]
            }
            """;

        var release = ReleaseFeedAppUpdateClient.Parse(json);

        Assert.NotNull(release);
        Assert.Equal("https://codeberg.org/TYHH10/HD2Mimnt/releases/tag/V0.3.2-alpha", release.HtmlUrl);
        var asset = Assert.Single(release.Assets);
        Assert.Null(asset.Digest);
        Assert.Equal("https://codeberg.org/TYHH10/HD2Mimnt/releases/download/V0.3.2-alpha/HD2Mimnt-0.3.2-alpha-win-x64.zip",
            asset.DownloadUrl);
    }
}