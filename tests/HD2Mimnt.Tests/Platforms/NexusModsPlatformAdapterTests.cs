using HD2Mimnt.Core.Entities;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Platforms;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HD2Mimnt.Tests.Platforms;

public class NexusModsPlatformAdapterTests : IDisposable
{
    private const string Api = "https://api.nexusmods.com/v3";
    private readonly ContractHandler _handler = new();
    private readonly HttpClient _client;
    private readonly NexusModsPlatformAdapter _platform;

    public NexusModsPlatformAdapterTests()
    {
        _client = new HttpClient(_handler);
        _platform = new NexusModsPlatformAdapter(_client);
        _platform.SetApiKey("test-key");
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task DeveloperCredentials_Should_ApplyToRegisteredPlatformAndRejectInvalidReplacement()
    {
        _platform.ClearAuthentication();
        var registry = new PlatformRegistry();
        registry.RegisterPlatform(_platform);
        var service = new NexusModsTestService(registry);
        Assert.False(service.IsAuthenticated);
        service.ApplyApiKey("  developer-test-key  ");
        Assert.True(service.IsAuthenticated);
        Assert.Throws<ArgumentException>(() => service.ApplyApiKey(" "));
        Assert.Throws<ArgumentException>(() => service.ApplyApiKey("invalid\r\nkey"));
        SetupMod();
        await registry.GetPlatform("nexusmods")!.GetModInfoAsync("123");
        Assert.Equal("developer-test-key", _handler.Requests.Single().ApiKey);
        Assert.False(_client.DefaultRequestHeaders.Contains("apikey"));
        service.ClearApiKey();
        Assert.False(service.IsAuthenticated);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _platform.GetModInfoAsync("123"));
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public void Platform_Should_Have_Correct_Properties()
    {
        Assert.Equal("NexusMods", _platform.Name);
        Assert.Equal("nexusmods", _platform.Id);
        Assert.True(_platform.Capabilities.HasFlag(PlatformCapabilities.Download | PlatformCapabilities.Files |
            PlatformCapabilities.Metadata | PlatformCapabilities.Versions | PlatformCapabilities.Authentication));
    }

    [Fact]
    public async Task Authentication_Should_Switch_Modes_Without_Changing_Shared_Headers()
    {
        _client.DefaultRequestHeaders.Add("X-Custom", "preserved");
        SetupMod();
        await _platform.GetModInfoAsync("123");
        Assert.Equal("test-key", _handler.Requests[^1].ApiKey);
        Assert.Null(_handler.Requests[^1].Authorization);

        _platform.SetBearerToken("signed-jwt");
        await _platform.GetModInfoAsync("123");
        Assert.Null(_handler.Requests[^1].ApiKey);
        Assert.Equal("Bearer signed-jwt", _handler.Requests[^1].Authorization);
        _platform.ClearApiKey();
        Assert.True(_platform.IsAuthenticated);
        _platform.SetApiKey("replacement-key");
        await _platform.GetModInfoAsync("123");
        Assert.Equal("replacement-key", _handler.Requests[^1].ApiKey);
        Assert.Null(_handler.Requests[^1].Authorization);
        _platform.ClearAuthentication();
        Assert.False(_platform.IsAuthenticated);
        Assert.Equal("preserved", _client.DefaultRequestHeaders.GetValues("X-Custom").Single());
        Assert.False(_client.DefaultRequestHeaders.Contains("apikey"));
        Assert.Null(_client.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public void Authentication_Should_Reject_Empty_Credentials()
    {
        Assert.Throws<ArgumentException>(() => _platform.SetApiKey(" "));
        Assert.Throws<ArgumentException>(() => _platform.SetBearerToken(" "));
    }

    [Fact]
    public async Task Protected_Operations_Should_Reject_Missing_Credentials()
    {
        _platform.ClearAuthentication();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _platform.GetModInfoAsync("123"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _platform.GetFilesAsync("123"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _platform.GetVersionsAsync("123"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _platform.DownloadModAsync("123"));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task IsAvailableAsync_Should_Check_Public_Feed_Without_Validating_Account()
    {
        _platform.ClearAuthentication();
        SetupTrending();
        Assert.True(await _platform.IsAvailableAsync());
        Assert.Equal(Api + "/games/helldivers2/trending-mods", _handler.Requests.Single().Url);
        Assert.Null(_handler.Requests.Single().ApiKey);
        _handler.Json(HttpMethod.Get, "/games/helldivers2/trending-mods", "{}", HttpStatusCode.NotFound);
        Assert.False(await _platform.IsAvailableAsync());
    }

    [Fact]
    public async Task SearchAsync_Should_Filter_Public_Trending_And_Map_Page_Ids()
    {
        SetupTrending();
        var result = await _platform.SearchAsync(new PlatformSearchQuery { Query = "armor" });
        var mod = Assert.Single(result.Mods);
        Assert.Equal("123", mod.Id);
        Assert.Equal("Armor", mod.Name);
        Assert.Equal("", mod.Author);
        Assert.Equal("", mod.Version);
        Assert.Null(mod.UpdatedAt);
        Assert.Equal("trending", mod.Metadata["search_scope"]);
        Assert.Contains("前 5", result.ScopeDescription);
        Assert.Null(_handler.Requests.Single().ApiKey); // Even when credentials are configured.
    }

    [Fact]
    public async Task SearchAsync_Should_Sort_And_Page_Only_The_Feed()
    {
        SetupTrending();
        var result = await _platform.SearchAsync(new PlatformSearchQuery
        {
            SortBy = PlatformSearchSort.Name, Page = 2, PageSize = 1
        });
        Assert.Equal("Weapon", Assert.Single(result.Mods).Name);
        Assert.Equal(2, result.TotalCount);
        Assert.False(result.HasNextPage);
        result = await _platform.SearchAsync(new PlatformSearchQuery { Page = int.MaxValue, PageSize = int.MaxValue });
        Assert.Empty(result.Mods);
    }

    [Fact]
    public async Task SearchAsync_Should_Reject_Unsupported_Sorts_Filters_And_Invalid_Paging()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => _platform.SearchAsync(new PlatformSearchQuery { SortBy = PlatformSearchSort.DownloadCount }));
        await Assert.ThrowsAsync<NotSupportedException>(() => _platform.SearchAsync(new PlatformSearchQuery { Filters = new() { ["category"] = "1" } }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _platform.SearchAsync(new PlatformSearchQuery { Page = 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _platform.SearchAsync(new PlatformSearchQuery { PageSize = 0 }));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task GetModInfoAsync_Should_Read_Data_Envelope_And_Preserve_Both_Ids()
    {
        SetupMod();
        var mod = await _platform.GetModInfoAsync("123");
        Assert.Equal("123", mod.Id);
        Assert.Equal("Test Mod", mod.Name);
        Assert.Equal("global-mod", mod.Metadata["nexus_global_mod_id"]);
        Assert.Equal("123", mod.Metadata["nexus_mod_id"]);
        Assert.Empty(mod.Version); // Not part of the v3 Mod schema.
        Assert.Empty(mod.Author);
        Assert.Equal(Api + "/games/helldivers2/mods/123", _handler.Requests.Single().Url);
    }

    [Fact]
    public async Task GetModInfoAsync_Should_Handle_Nullable_Name()
    {
        _handler.Json(HttpMethod.Get, "/games/helldivers2/mods/123", """{"data":{"id":"global-mod","game_scoped_id":"123","game_id":"1","name":null}}""");
        Assert.Empty((await _platform.GetModInfoAsync("123")).Name);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":{\"name\":\"missing IDs\"}}")]
    public async Task GetModInfoAsync_Should_Reject_Invalid_Response(string json)
    {
        _handler.Json(HttpMethod.Get, "/games/helldivers2/mods/123", json);
        await Assert.ThrowsAsync<JsonException>(() => _platform.GetModInfoAsync("123"));
    }

    [Fact]
    public async Task GetFilesAsync_Should_Resolve_Global_Mod_And_Version_Ids_And_Filter_Version()
    {
        SetupFiles();
        var files = await _platform.GetFilesAsync("123", "1.0");
        var file = Assert.Single(files);
        Assert.Equal("version-primary", file.Id);
        Assert.Equal("Main archive", file.Name);
        Assert.Equal(0, file.Size); // Not provided by v3.
        Assert.Equal(DateTimeKind.Utc, file.UploadedAt!.Value.Kind);
        Assert.Equal(new[]
        {
            Api + "/games/helldivers2/mods/123", Api + "/mods/global-mod/files",
            Api + "/mod-files/main-file/versions", Api + "/mod-files/optional-file/versions"
        }, _handler.Requests.Select(r => r.Url));
    }

    [Fact]
    public async Task GetVersionsAsync_Should_Prefer_Primary_And_Exclude_Inactive_Versions()
    {
        SetupFiles();
        var versions = await _platform.GetVersionsAsync("123");
        Assert.Equal(new[] { "1.0", "optional-2" }, versions.Select(v => v.Version));
        Assert.All(versions, v => Assert.Equal(0, v.FileSize));
    }

    [Fact]
    public async Task DownloadModAsync_Should_Post_For_Primary_Version_And_Download_Without_Credentials()
    {
        SetupFiles();
        _handler.Json(HttpMethod.Post, "/mod-file-versions/version-primary/download-repacked",
            """{"download_url":"https://cdn.example.com/archive?signature=test","expires_at":"2026-10-11T00:00:00Z"}""");
        var bytes = new byte[] { 80, 75, 3, 4, 5 };
        _handler.Routes[(HttpMethod.Get, "https://cdn.example.com/archive?signature=test")] =
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        var progress = new RecordingProgress();

        using var stream = await _platform.DownloadModAsync("123", progress);
        Assert.Equal(0, stream.Position);
        using var copied = new MemoryStream();
        await stream.CopyToAsync(copied);
        Assert.Equal(bytes, copied.ToArray());
        Assert.Equal(bytes.Length, progress.Last!.DownloadedBytes);
        Assert.Equal(bytes.Length, progress.Last.TotalBytes);
        var link = _handler.Requests[^2];
        Assert.Equal(HttpMethod.Post, link.Method);
        Assert.Equal("HD2Mimnt", link.ApplicationName);
        Assert.Equal("test-key", link.ApiKey);
        var archive = _handler.Requests[^1];
        Assert.Null(archive.ApiKey);
        Assert.Null(archive.Authorization);
        Assert.Null(archive.ApplicationName);
    }

    [Fact]
    public async Task DownloadModAsync_Should_Select_Main_When_Primary_Is_Absent()
    {
        SetupFiles(primary: false);
        _handler.Json(HttpMethod.Post, "/mod-file-versions/version-primary/download-repacked",
            """{"download_url":"http://cdn.example.com/insecure","expires_at":"2026-10-11T00:00:00Z"}""");
        await Assert.ThrowsAsync<JsonException>(() => _platform.DownloadModAsync("123"));
        Assert.EndsWith("/version-primary/download-repacked", _handler.Requests[^1].Url);
    }

    [Fact]
    public async Task DownloadModAsync_Should_Not_Request_Link_For_Archived_Only_Mod()
    {
        SetupMod();
        _handler.Json(HttpMethod.Get, "/mods/global-mod/files", """{"data":{"mod_files":[{"id":"old-file","name":"Old","is_active":false,"last_file_uploaded_at":null,"versions_count":1,"archived_count":1,"removed_count":0}]}}""");
        _handler.Json(HttpMethod.Get, "/mod-files/old-file/versions", """{"data":{"versions":[{"id":"old","file":{"id":"old-file","name":"Old"},"position":"0","game_scoped_id":"9","name":"Old","version":"9.0","category":"archived","uploaded_at":"2026-10-10T00:00:00Z","is_primary":true}]}}""");
        Assert.Empty(await _platform.GetVersionsAsync("123"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _platform.DownloadModAsync("123"));
        Assert.DoesNotContain(_handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Api_Error_Should_Preserve_Status_And_Problem_Details(HttpStatusCode status)
    {
        _handler.Json(HttpMethod.Get, "/games/helldivers2/mods/123",
            """{"type":"about:blank","title":"Access denied","status":403,"detail":"This file is unavailable.","instance":"/mods/123"}""", status, "application/problem+json");
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => _platform.GetModInfoAsync("123"));
        Assert.Equal(status, error.StatusCode);
        Assert.Contains("Access denied", error.Message);
        Assert.Contains("This file is unavailable.", error.Message);
    }

    [Fact]
    public async Task Operations_Should_Propagate_Cancellation()
    {
        SetupTrending();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _platform.IsAvailableAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _platform.GetFilesAsync("123", cancellationToken: cts.Token));
        Assert.Empty(_handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RateLimiter_Should_Honour_RetryAfter_And_Allow_Cancellation(bool useDate)
    {
        var limiter = new RateLimiter();
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = useDate
            ? new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(1))
            : new RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
        limiter.UpdateFromHeaders(response);
        using var cts = new CancellationTokenSource();
        var wait = limiter.WaitIfNeededAsync(cts.Token);
        Assert.False(wait.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    private void SetupMod() => _handler.Json(HttpMethod.Get, "/games/helldivers2/mods/123",
        """{"data":{"id":"global-mod","game_scoped_id":"123","game_id":"1","name":"Test Mod"}}""");

    private void SetupTrending() => _handler.Json(HttpMethod.Get, "/games/helldivers2/trending-mods",
        """{"data":{"mods":[{"name":"Weapon","author":"Author","summary":null,"picture_url":null,"mod_page_url":"https://www.nexusmods.com/helldivers2/mods/456"},{"name":"Armor","author":null,"summary":"Armor description","picture_url":null,"mod_page_url":"https://www.nexusmods.com/helldivers2/mods/123"}]}}""");

    private void SetupFiles(bool primary = true)
    {
        SetupMod();
        _handler.Json(HttpMethod.Get, "/mods/global-mod/files", """{"data":{"mod_files":[{"id":"main-file","name":"Main","is_active":true,"last_file_uploaded_at":"2026-10-09T00:00:00Z","versions_count":2,"archived_count":1,"removed_count":0},{"id":"optional-file","name":"Optional","is_active":true,"last_file_uploaded_at":"2026-10-10T00:00:00Z","versions_count":1,"archived_count":0,"removed_count":0}]}}""");
        _handler.Json(HttpMethod.Get, "/mod-files/main-file/versions", """{"data":{"versions":[{"id":"version-archived","file":{"id":"main-file","name":"Main"},"position":"0","game_scoped_id":"10","name":"Archived","version":"0.9","category":"archived","uploaded_at":"2026-10-10T00:00:00Z","is_primary":true},{"id":"version-primary","file":{"id":"main-file","name":"Main"},"position":"1","game_scoped_id":"11","name":"Main archive","version":"1.0","category":"main","uploaded_at":"2026-10-09T00:00:00Z","is_primary":PRIMARY}]}}""".Replace("PRIMARY", primary ? "true" : "false"));
        _handler.Json(HttpMethod.Get, "/mod-files/optional-file/versions", """{"data":{"versions":[{"id":"version-optional","file":{"id":"optional-file","name":"Optional"},"position":"0","game_scoped_id":"12","name":"Optional archive","version":"optional-2","category":"optional","uploaded_at":"2026-10-10T00:00:00Z"}]}}""");
    }

    private sealed class RecordingProgress : IProgress<DownloadProgress>
    {
        public DownloadProgress? Last { get; private set; }
        public void Report(DownloadProgress value) => Last = value;
    }

    private sealed record Request(HttpMethod Method, string Url, string? ApiKey, string? Authorization, string? ApplicationName);

    private sealed class ContractHandler : HttpMessageHandler
    {
        public Dictionary<(HttpMethod, string), Func<HttpResponseMessage>> Routes { get; } = new();
        public List<Request> Requests { get; } = new();

        public void Json(HttpMethod method, string path, string json, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json") =>
            Routes[(method, Api + path)] = () => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, mediaType) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(new(request.Method, url,
                request.Headers.TryGetValues("apikey", out var keys) ? keys.Single() : null,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Application-Name", out var names) ? names.Single() : null));
            Assert.True(Routes.TryGetValue((request.Method, url), out var response), $"Unexpected request: {request.Method} {url}");
            return Task.FromResult(response!());
        }
    }
}
