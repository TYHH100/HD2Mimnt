using System.Net;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Platforms.AyakaMods;

namespace HD2Mimnt.Tests.Platforms;

/// <summary>Document-derived fixtures, not evidence of a successful live-key request.</summary>
public sealed partial class AyakaModsPlatformTests
{
    [Fact]
    public async Task MetadataCache_ShouldReuseResponsesAndInvalidateWhenToolKeyChanges()
    {
        using var handler = ApiHandler();
        using var http = new HttpClient(handler);
        using var platform = new AyakaModsPlatformAdapter(http);
        platform.SetApiKey("first-key");
        await platform.GetModInfoAsync("42");
        await platform.GetModInfoAsync("42");
        Assert.Single(handler.Requests);
        platform.SetApiKey("second-key");
        await platform.GetModInfoAsync("42");
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("second-key", handler.Requests.Last().Key);
        platform.ClearApiKey();
        await Assert.ThrowsAsync<InvalidOperationException>(() => platform.GetModInfoAsync("42"));
    }

    private const string Mod = """{"mod":{"mod_id":42,"game_id":119,"title":"Armor","username":"Builder","version_string":"2.0","last_update":1791763200}}""";
    private const string Versions = """{"versions":[{"mod_version_id":7,"mod_id":42,"version_string":"1.0","release_date":1000,"files":[{"id":8,"filename":"old.zip","size":3}]},{"mod_version_id":9,"mod_id":42,"version_string":"2.0","release_date":2000,"files":[{"id":10,"filename":"chosen.zip","size":4,"download_url":"https://untrusted.test/raw-secret"},{"id":11,"filename":"optional.zip","size":5}]}]}""";

    [Theory]
    [InlineData(ModBrowseFeed.Trending, "download_count")]
    [InlineData(ModBrowseFeed.LatestAdded, "mod_date")]
    [InlineData(ModBrowseFeed.LatestUpdated, "last_update")]
    public async Task Browse_ShouldUseGame119ServerPagingAndRequestLocalKey(ModBrowseFeed feed, string order)
    {
        using var handler = new Handler(_ => Json("""{"mods":[{"mod_id":42,"game_id":119,"title":"Armor","tag_line":"Summary","download_count":1200}],"pagination":{"current_page":2,"per_page":7,"last_page":3,"total":18}}"""));
        using var client = new HttpClient(handler);
        using var platform = new AyakaModsPlatformAdapter(client);
        platform.SetApiKey("synthetic-ayaka-key");
        var result = await platform.BrowsePageAsync(feed, 2);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"https://ayakamods.com/api/mod-games/119/mods/?order={order}&direction=desc&page=2", request.Url);
        Assert.Equal("synthetic-ayaka-key", request.Key);
        Assert.Equal(7, result.PageSize);
        Assert.Equal(2, result.Page);
        Assert.Equal(18, result.TotalCount);
        Assert.True(result.HasNextPage);
        Assert.Equal("Summary", Assert.Single(result.Mods).Description);
        Assert.False(client.DefaultRequestHeaders.Contains("XF-Api-Key"));
    }

    [Fact]
    public async Task MissingKeyAndUndocumentedSearch_ShouldNotSendRequests()
    {
        using var handler = new Handler(_ => Json("{}"));
        using var client = new HttpClient(handler);
        using var platform = new AyakaModsPlatformAdapter(client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => platform.BrowseAsync(ModBrowseFeed.Trending));
        await Assert.ThrowsAsync<InvalidOperationException>(() => platform.GetModInfoAsync("42"));
        await Assert.ThrowsAsync<NotSupportedException>(() => platform.SearchAsync(new() { Query = "armor" }));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("""{"mods":[{"mod_id":42,"game_id":1}],"pagination":{"current_page":1,"per_page":20,"total":1}}""")]
    [InlineData("""{"mods":[],"pagination":{"current_page":1,"per_page":0,"total":0}}""")]
    [InlineData("""{"mods":[]} """)]
    public async Task Browse_ShouldRejectForeignGamesAndInvalidPaging(string body)
    {
        using var handler = new Handler(_ => Json(body));
        using var client = new HttpClient(handler);
        using var platform = new AyakaModsPlatformAdapter(client);
        platform.SetApiKey("test-key");
        await Assert.ThrowsAsync<JsonException>(() => platform.BrowseAsync(ModBrowseFeed.Trending));
    }

    [Fact]
    public async Task DetailsAndFiles_ShouldKeepVersionAndAttachmentIdentitiesSeparateAndDiscardRawUrls()
    {
        using var handler = ApiHandler();
        using var client = new HttpClient(handler);
        using var platform = new AyakaModsPlatformAdapter(client);
        platform.SetApiKey("test-key");
        var mod = await platform.GetModInfoAsync("42");
        Assert.Equal("Armor", mod.Name);
        Assert.Equal("Builder", mod.Author);
        Assert.NotNull(mod.UpdatedAt);
        var files = await platform.GetFilesAsync("42");
        Assert.Equal(new[] { "ayaka-file:9:10", "ayaka-file:9:11", "ayaka-file:7:8" }, files.Select(f => f.Id));
        Assert.Equal("chosen.zip", files[0].FileName);
        Assert.All(files, file => Assert.Empty(file.DownloadUrl));
        Assert.Single(await platform.GetFilesAsync("42", "1.0"));
        Assert.Equal("2.0", (await platform.GetVersionsAsync("42"))[0].Version);
    }

    [Fact]
    public async Task DetailAndVersionOwnership_ShouldRejectMismatchedIdentity()
    {
        using var handler = new Handler(_ => Json(Mod.Replace("\"game_id\":119", "\"game_id\":1")));
        using var client = new HttpClient(handler);
        using var platform = new AyakaModsPlatformAdapter(client);
        platform.SetApiKey("test-key");
        await Assert.ThrowsAsync<JsonException>(() => platform.GetModInfoAsync("42"));
        await Assert.ThrowsAsync<ArgumentException>(() => platform.GetFilesAsync("42/other"));
    }

    [Theory]
    [InlineData("https://ayakamods.com/mods/armor.42/", "42")]
    [InlineData("https://ayakamods.com/mods/weakpoint-lock-on-v2.3981/download", "3981")]
    [InlineData("https://www.ayakamods.com/mods/0042/?tab=history#x", "42")]
    public void Link_ShouldNormalizeWebsiteIdentityWithoutKeepingQueryOrFragment(string input, string id)
    {
        var link = AyakaModsLinkParser.Parse(input);
        Assert.Equal("ayakamods", link.PlatformId);
        Assert.Equal(id, link.ModId);
        Assert.Equal($"https://ayakamods.com/mods/{id}/", link.Url);
    }

    [Theory]
    [InlineData("https://ayakamods.com.evil.test/mods/42/")]
    [InlineData("https://key@ayakamods.com/mods/42/")]
    [InlineData("https://ayakamods.com:444/mods/42/")]
    [InlineData("https://ayakamods.com/mods/42/download/extra")]
    [InlineData("https://ayakamods.com/mods/0/")]
    [InlineData("https://ayakamods.com/games/helldivers-2.119/")]
    public void Link_ShouldRejectUnexpectedHostAndRoutes(string input)
        => Assert.Throws<ArgumentException>(() => AyakaModsLinkParser.Parse(input));

    [Fact]
    public async Task Download_ShouldRecheckSelectedFileUseApiEndpointAndStripKeyOnRedirect()
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-Ayaka-" + Guid.NewGuid().ToString("N"));
        using var handler = ApiHandler();
        using var download = new Handler(request =>
        {
            if (request.RequestUri!.Host == "ayakamods.com")
            {
                Assert.Equal("/api/mod-versions/9/download?file=10", request.RequestUri.PathAndQuery);
                return new(HttpStatusCode.Found) { Headers = { Location = new Uri("https://files.example.test/chosen.zip?signature=not-persisted") } };
            }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 }) };
        });
        using var client = new HttpClient(handler);
        using var platform = new AyakaModsPlatformAdapter(client, new Options(), root, root, download);
        platform.SetApiKey("test-key");
        try
        {
            await using (var stream = await platform.DownloadFileAsync("42", "ayaka-file:9:10"))
            {
                using var output = new MemoryStream();
                await stream.CopyToAsync(output);
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, output.ToArray());
            }
            Assert.Equal("test-key", download.Requests[0].Key);
            Assert.Null(download.Requests[1].Key);
            Assert.All(download.Requests, request => Assert.Null(request.Authorization));
            Assert.Empty(Directory.GetFiles(root));
            await Assert.ThrowsAsync<InvalidOperationException>(() => platform.DownloadFileAsync("42", "ayaka-file:9:999"));
            Assert.Equal(2, download.Requests.Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("application/json", "{\"errors\":[{\"code\":\"no_permission\"}]}")]
    [InlineData("text/html", "<html>Sign in</html>")]
    public async Task Download_ShouldRejectSuccessfulErrorDocumentsAndCleanTemporaryFiles(string type, string body)
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-Ayaka-" + Guid.NewGuid().ToString("N"));
        using var api = ApiHandler();
        using var download = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) });
        using var client = new HttpClient(api);
        using var platform = new AyakaModsPlatformAdapter(client, new Options(), root, root, download);
        platform.SetApiKey("test-key");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => platform.DownloadFileAsync("42", "ayaka-file:9:10"));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ApiError_ShouldPreserveStatusWithoutEchoingCredentialsOrRawResponse()
    {
        using var handler = new Handler(_ => new(HttpStatusCode.Forbidden) { Content = new StringContent("test-key https://signed.example.test/private") });
        using var client = new HttpClient(handler);
        using var platform = new AyakaModsPlatformAdapter(client);
        platform.SetApiKey("test-key");
        var exception = await Assert.ThrowsAsync<PlatformRequestException>(() => platform.GetModInfoAsync("42"));
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.DoesNotContain("test-key", exception.ToString());
        Assert.DoesNotContain("signed.example", exception.ToString());
    }

    private static Handler ApiHandler() => new(request => Json(request.RequestUri!.AbsolutePath.EndsWith("/versions/") ? Versions : Mod));
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Options : IDownloadOptionsProvider
    {
        public Task<DownloadOptions> GetOptionsAsync() => Task.FromResult(new DownloadOptions { EnableResume = false, MaxParallelSegments = 1 });
    }
    private sealed record Request(string Url, string? Key, string? Authorization);
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<Request> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(new(request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("XF-Api-Key", out var keys) ? keys.Single() : null, request.Headers.Authorization?.ToString()));
            return Task.FromResult(response(request));
        }
    }
}
