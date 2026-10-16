using HD2Mimnt.Core.Entities;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Platforms;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

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
        _platform = new NexusModsPlatformAdapter(_client, applicationVersion: "0.1.0");
        _platform.SetApiKey("test-key");
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Api_DebugLog_ShouldPreserveResponseDiagnosticsAndRedactSecrets()
    {
        var logger = new ResponseLogger();
        var platform = new NexusModsPlatformAdapter(_client, applicationVersion: "0.1.0", logger: logger);
        platform.SetApiKey("test-key");
        _handler.Routes[(HttpMethod.Get, Api + "/games/helldivers2/mods/123")] = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"title":"Forbidden","detail":"Mod not available: 17374; test-key; echoed response-secret","status":403,"nested":{"access_token":"response-secret","email":"private@example.com","user_id":789},"download_url":"https://cdn.example.com/path-secret?signature=signed-secret","items":[{"refresh_token":"refresh-secret"}]}""", Encoding.UTF8, "application/problem+json")
            };
            response.Headers.Add("Set-Cookie", "session=cookie-secret");
            response.Headers.Add("X-RL-Hourly-Remaining", "99");
            response.Headers.Location = new Uri("https://cdn.example.com/path-secret?key=location-secret");
            return response;
        };
        var error = await Assert.ThrowsAsync<PlatformRequestException>(() => platform.GetModInfoAsync("123"));
        Assert.Contains("Mod not available: 17374", error.ServerReason);
        var log = Assert.Single(logger.Messages);
        Assert.Contains("403", log);
        Assert.Contains("Mod not available: 17374", log);
        Assert.Contains("application/problem+json", log);
        Assert.Contains("X-RL-Hourly-Remaining", log);
        Assert.Contains("99", log);
        Assert.Contains("\"user_id\":789", log);
        Assert.Contains("private@example.com", log);
        Assert.Contains("https://cdn.example.com/path-secret?signature=[REDACTED]", log);
        foreach (var secret in new[] { "test-key", "response-secret", "refresh-secret", "cookie-secret", "signed-secret", "location-secret" })
            Assert.DoesNotContain(secret, log);
    }

    [Theory]
    [InlineData("403", true)]
    [InlineData("\"403\"", true)]
    [InlineData("\"oauth-auth-code\"", false)]
    public async Task Api_DebugLog_ShouldPreserveHttpErrorCodeButRedactAuthorizationCodes(string code, bool preserve)
    {
        var logger = new ResponseLogger();
        var platform = new NexusModsPlatformAdapter(_client, applicationVersion: "0.1.0", logger: logger);
        platform.SetApiKey("test-key");
        _handler.Json(HttpMethod.Get, "/games/helldivers2/mods/123",
            "{\"code\":" + code + ",\"message\":\"Mod not available: 17373\",\"nested\":{\"code\":\"private-code\"}}",
            HttpStatusCode.Forbidden);
        await Assert.ThrowsAsync<PlatformRequestException>(() => platform.GetModInfoAsync("123"));
        var log = Assert.Single(logger.Messages);
        Assert.Contains(preserve ? "\"code\":" + code : "\"code\":\"[REDACTED]\"", log);
        Assert.Contains("Mod not available: 17373", log);
        Assert.DoesNotContain("oauth-auth-code", log);
        Assert.DoesNotContain("private-code", log);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Api_DebugLog_ShouldRespectLevelAndPreserveSuccessfulParsing(bool debugEnabled)
    {
        var logger = new ResponseLogger { Enabled = debugEnabled };
        var platform = new NexusModsPlatformAdapter(_client, applicationVersion: "0.1.0", logger: logger);
        platform.SetApiKey("test-key");
        SetupMod();
        Assert.Equal("Test Mod", (await platform.GetModInfoAsync("123")).Name);
        Assert.Equal(debugEnabled ? 1 : 0, logger.Messages.Count);
        if (debugEnabled) Assert.Contains("Test Mod", logger.Messages[0]);
    }

    [Fact]
    public async Task Api_DebugLog_ShouldSanitizeNonJsonErrorsAndBearerEcho()
    {
        var logger = new ResponseLogger();
        var platform = new NexusModsPlatformAdapter(_client, applicationVersion: "0.1.0", logger: logger);
        platform.SetBearerToken("bearer-secret");
        _handler.Json(HttpMethod.Get, "/games/helldivers2/mods/123",
            "<html>Not published bearer-secret token=unknown-secret private@example.com https://cdn.example.com/path?key=url-secret</html>",
            HttpStatusCode.Forbidden, "text/html");
        await Assert.ThrowsAsync<PlatformRequestException>(() => platform.GetModInfoAsync("123"));
        var log = Assert.Single(logger.Messages);
        Assert.Contains("Not published", log);
        Assert.Contains("private@example.com", log);
        Assert.Contains("https://cdn.example.com/path?key=[REDACTED]", log);
        foreach (var secret in new[] { "bearer-secret", "unknown-secret", "url-secret" })
            Assert.DoesNotContain(secret, log);
    }

    private sealed class ResponseLogger : ILogger<NexusModsPlatformAdapter>
    {
        public bool Enabled { get; init; } = true;
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => Enabled && logLevel == LogLevel.Debug;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Assert.Equal(LogLevel.Debug, logLevel);
            Messages.Add(formatter(state, exception));
        }
    }

    [Fact]
    public void WebsiteDownload_ShouldUseWebsiteFileIdAndExposeOnlyPublicProtocolTarget()
    {
        var file = new PlatformModFile { Id = "nexus-v1-file:68239", WebsiteFileId = "68239" };
        Assert.Equal("https://www.nexusmods.com/helldivers2/mods/16633?tab=files&file_id=68239&nmm=1",
            _platform.GetWebsiteDownloadUrl("16633", file));
        Assert.Throws<ArgumentException>(() => _platform.GetWebsiteDownloadUrl("16633", new PlatformModFile { Id = "global-version-id" }));
        var target = _platform.ReadProtocolTarget(NxmLink);
        Assert.Equal(new ModProtocolTarget("nexusmods", "helldivers2", "123", "456"), target);
        Assert.DoesNotContain("test-free-key", JsonSerializer.Serialize(target));
    }

    [Fact]
    public async Task DeveloperCredentials_Should_ApplyToRegisteredPlatformAndRejectInvalidReplacement()
    {
        _platform.ClearAuthentication();
        var registry = new PlatformRegistry();
        registry.RegisterPlatform(_platform);
        var service = new NexusModsTestService(registry);
        Assert.False(service.IsAuthenticated);
        await service.ApplyApiKeyAsync("  developer-test-key  ");
        Assert.True(service.IsAuthenticated);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyApiKeyAsync(" "));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyApiKeyAsync("invalid\r\nkey"));
        SetupMod();
        await registry.GetPlatform("nexusmods")!.GetModInfoAsync("123");
        Assert.Equal("developer-test-key", _handler.Requests.Single().ApiKey);
        Assert.False(_client.DefaultRequestHeaders.Contains("apikey"));
        await service.ClearApiKeyAsync();
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
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Api_Error_Should_Preserve_Status_And_Problem_Details(HttpStatusCode status)
    {
        _handler.Json(HttpMethod.Get, "/games/helldivers2/mods/123",
            """{"type":"about:blank","title":"Access denied","status":403,"detail":"This file is unavailable.","instance":"/mods/123"}""", status, "application/problem+json");
        var error = await Assert.ThrowsAsync<PlatformRequestException>(() => _platform.GetModInfoAsync("123"));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal("获取模组信息", error.Operation);
        Assert.Contains("Access denied", error.Message);
        Assert.Contains("This file is unavailable.", error.Message);
    }

    [Fact]
    public async Task Api_Error_ShouldPreserveUnavailableModMessage()
    {
        _handler.Json(HttpMethod.Get, "/games/helldivers2/mods/123",
            """{"message":"Mod not available: 17374"}""", HttpStatusCode.Forbidden);
        var error = await Assert.ThrowsAsync<PlatformRequestException>(() => _platform.GetModInfoAsync("123"));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal("Mod not available: 17374", error.ServerReason);
    }

    [Fact]
    public async Task Api_Error_ShouldHideCredentialEchoInServerReason()
    {
        _handler.Json(HttpMethod.Get, "/games/helldivers2/mods/123",
            """{"title":"Invalid credential","detail":"Rejected test-key"}""", HttpStatusCode.Unauthorized);
        var error = await Assert.ThrowsAsync<PlatformRequestException>(() => _platform.GetModInfoAsync("123"));
        Assert.DoesNotContain("test-key", error.Message);
        Assert.Contains("Rejected [已隐藏]", error.ServerReason);
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

    private const string NxmLink = "nxm://helldivers2/mods/123/files/456?key=test-free-key&expires=4102444800&user_id=789";
    private void LegacyJson(string path, string json, HttpStatusCode status = HttpStatusCode.OK)
        => _handler.Routes[(HttpMethod.Get, "https://api.nexusmods.com/v1" + path)] =
            () => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private void SetupNxm()
    {
        LegacyJson("/users/validate.json", """{"user_id":789,"is_premium":false}""");
        LegacyJson("/games/helldivers2/mods/123/files/456.json", """{"file_id":456,"category_id":1,"name":"Free main file","file_name":"free-main.7z","version":"2.0","size_kb":10}""");
        LegacyJson("/games/helldivers2/mods/123/files/456/download_link.json?key=test-free-key&expires=4102444800",
            """[{"URI":"https://cdn.example.com/free?signature=test","name":"CDN","short_name":"CDN"}]""");
        _handler.Routes[(HttpMethod.Get, "https://cdn.example.com/free?signature=test")] =
            () => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
    }

    [Fact]
    public async Task FreeUpdateCheck_ShouldReadWebsiteFilesWithoutRequestingDownload()
    {
        LegacyJson("/games/helldivers2/mods/123/files.json", """{"files":[{"file_id":111,"category_id":3,"name":"Optional","version":"9.0","uploaded_timestamp":200},{"file_id":456,"category_id":1,"name":"Main","version":"2.0","uploaded_timestamp":100},{"file_id":222,"category_id":4,"name":"Old","version":"1.0"}]}""");
        var files = await _platform.GetProtocolFilesAsync("123");
        Assert.Equal(new[] { "456", "111" }, files.Select(file => file.WebsiteFileId));
        Assert.Equal("nexus-v1-file:456", files[0].Id);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task NxmDownload_ShouldVerifyAccountAndExactFileAndUseWebsiteAuthorization()
    {
        SetupNxm();
        var file = await _platform.ResolveProtocolFileAsync("123", NxmLink);
        Assert.Equal("nexus-v1-file:456", file.Id);
        Assert.Equal("456", file.WebsiteFileId);
        Assert.Equal("free-main.7z", file.FileName);
        Assert.Equal("2.0", file.Version);
        using var stream = await _platform.DownloadProtocolFileAsync("123", file.Id, NxmLink);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        Assert.Equal(new byte[] { 1, 2, 3 }, copy.ToArray());
        Assert.Contains(_handler.Requests, r => r.Url.EndsWith("download_link.json?key=test-free-key&expires=4102444800"));
        Assert.DoesNotContain(_handler.Requests, r => r.Method == HttpMethod.Post || r.Url.Contains("/v3/"));
        Assert.Null(_handler.Requests[^1].ApiKey);
        Assert.Null(_handler.Requests[^1].Authorization);
    }

    [Theory]
    [InlineData("nxm://skyrim/mods/123/files/456?key=key&expires=4102444800&user_id=789")]
    [InlineData("nxm://helldivers2/mods/999/files/456?key=key&expires=4102444800&user_id=789")]
    [InlineData("nxm://helldivers2/mods/123/files/0?key=key&expires=4102444800&user_id=789")]
    [InlineData("nxm://helldivers2/mods/123/files/456?key=key&expires=4102444800")]
    [InlineData("nxm://helldivers2/mods/123/files/456?key=key&key=second&expires=4102444800&user_id=789")]
    [InlineData("https://helldivers2/mods/123/files/456?key=key&expires=4102444800&user_id=789")]
    public async Task NxmResolve_ShouldRejectInvalidLinkBeforeRequest(string input)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _platform.ResolveProtocolFileAsync("123", input));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task NxmResolve_ShouldRejectExpiredOrOtherAccountAndDownloadMismatch()
    {
        SetupNxm();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _platform.ResolveProtocolFileAsync("123", NxmLink.Replace("4102444800", "1")));
        Assert.Empty(_handler.Requests);
        await Assert.ThrowsAsync<ArgumentException>(() => _platform.DownloadProtocolFileAsync("123", "nexus-v1-file:999", NxmLink));
        Assert.Empty(_handler.Requests);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _platform.ResolveProtocolFileAsync("123", NxmLink.Replace("user_id=789", "user_id=790")));
        Assert.Contains("同一账户", error.Message);
        Assert.Single(_handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task NxmDownload_ShouldPreserveFailureReasonAndHideTemporaryKey(HttpStatusCode status)
    {
        SetupNxm();
        LegacyJson("/games/helldivers2/mods/123/files/456/download_link.json?key=test-free-key&expires=4102444800",
            """{"message":"Rejected temporary key test-free-key"}""", status);
        var error = await Assert.ThrowsAsync<PlatformRequestException>(() => _platform.DownloadProtocolFileAsync("123", "nexus-v1-file:456", NxmLink));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal("获取网页授权下载链接", error.Operation);
        Assert.Contains("Rejected temporary key [已隐藏]", error.ServerReason);
        Assert.DoesNotContain("test-free-key", error.Message);
    }

    [Fact]
    public async Task DownloadFileAsync_ShouldUseSelectedOptionalVersionInsteadOfPrimary()
    {
        SetupFiles();
        _handler.Json(HttpMethod.Post, "/mod-file-versions/version-optional/download-repacked",
            """{"download_url":"https://cdn.example.com/optional"}""");
        _handler.Routes[(HttpMethod.Get, "https://cdn.example.com/optional")] =
            () => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2 }) };
        using var stream = await _platform.DownloadFileAsync("123", "version-optional");
        Assert.Contains(_handler.Requests, r => r.Method == HttpMethod.Post && r.Url.EndsWith("version-optional/download-repacked"));
        Assert.DoesNotContain(_handler.Requests, r => r.Url.EndsWith("version-primary/download-repacked"));
        Assert.Null(_handler.Requests[^1].ApiKey);
    }

    [Theory]
    [InlineData("version-archived")]
    [InlineData("removed-version")]
    public async Task DownloadFileAsync_ShouldRejectUnavailableSelectedVersion(string versionId)
    {
        SetupFiles();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _platform.DownloadFileAsync("123", versionId));
        Assert.DoesNotContain(_handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public async Task Membership_ShouldRequirePremiumRatherThanSupporter(bool premium, bool supporter, bool allowed)
    {
        LegacyJson("/users/validate.json", JsonSerializer.Serialize(new { user_id = 789, is_premium = premium, is_supporter = supporter }));
        Assert.Equal(allowed, await _platform.HasDirectDownloadAccessAsync());
    }

    [Fact]
    public async Task Membership_ShouldRejectMissingStatus()
    {
        LegacyJson("/users/validate.json", """{"user_id":789}""");
        await Assert.ThrowsAsync<JsonException>(() => _platform.HasDirectDownloadAccessAsync());
    }

    [Fact]
    public async Task PremiumDownload_ShouldUseSelectedWebsiteFileWithoutAuthorizationOrV3AndKeepCdnAnonymous()
    {
        LegacyJson("/users/validate.json", """{"user_id":789,"is_premium":true}""");
        LegacyJson("/games/helldivers2/mods/123/files/456.json", """{"file_id":456,"category_id":3,"name":"Optional"}""");
        LegacyJson("/games/helldivers2/mods/123/files/456/download_link.json", """[{"URI":"https://cdn.example.com/premium"}]""");
        _handler.Routes[(HttpMethod.Get, "https://cdn.example.com/premium")] =
            () => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 7, 8 }) };
        using var stream = await _platform.DownloadFileAsync("123", "nexus-v1-file:456");
        Assert.Equal(7, stream.ReadByte());
        Assert.Contains(_handler.Requests, request => request.Url.EndsWith("files/456/download_link.json"));
        Assert.DoesNotContain(_handler.Requests, request => request.Url.Contains("/v3/") || request.Url.Contains("?"));
        Assert.Null(_handler.Requests[^1].ApiKey);
    }

    [Theory]
    [InlineData(false, 456, 1)]
    [InlineData(true, 999, 1)]
    [InlineData(true, 456, 6)]
    [InlineData(true, 456, 7)]
    public async Task PremiumDownload_ShouldRejectMissingPermissionOrUnavailableFile(bool premium, int returnedId, int category)
    {
        LegacyJson("/users/validate.json", JsonSerializer.Serialize(new { user_id = 789, is_premium = premium }));
        LegacyJson("/games/helldivers2/mods/123/files/456.json", JsonSerializer.Serialize(new { file_id = returnedId, category_id = category }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _platform.DownloadFileAsync("123", "nexus-v1-file:456"));
        Assert.DoesNotContain(_handler.Requests, request => request.Url.Contains("download_link") || request.Url.Contains("/v3/"));
    }

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

    private sealed record Request(HttpMethod Method, string Url, string? ApiKey, string? Authorization,
        string? ApplicationName, string? ApplicationVersion);

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
                request.Headers.TryGetValues("Application-Name", out var names) ? names.Single() : null,
                request.Headers.TryGetValues("Application-Version", out var versions) ? versions.Single() : null));
            if (url.StartsWith(Api + "/", StringComparison.Ordinal))
            {
                Assert.Equal("HD2Mimnt", Requests[^1].ApplicationName);
                Assert.Equal("0.1.0", Requests[^1].ApplicationVersion);
            }
            Assert.True(Routes.TryGetValue((request.Method, url), out var response), $"Unexpected request: {request.Method} {url}");
            return Task.FromResult(response!());
        }
    }
}
