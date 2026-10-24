using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Platforms.AyakaMods;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Tests.Platforms;

public sealed partial class AyakaModsPlatformTests
{
    [Fact]
    public async Task Api_DebugLog_ShouldRecord400DetailsAndRedactSecretsBeforeLogging()
    {
        var logger = new AyakaResponseLogger();
        using var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"code":400,"errors":[{"code":"parameter_invalid","message":"Invalid version 9: synthetic-key; echoed response-secret","params":{"file":10}}],"XF-Api-Key":"response-secret","user_id":987654,"email":"private@example.com","access_token":"access-secret","download_url":"https://cdn.example.test/private-path?signature=signed-secret"}""", Encoding.UTF8, "application/json")
            };
            response.Headers.Add("Set-Cookie", "session=cookie-secret");
            response.Headers.Add("X-Request-Id", "request-400-example");
            response.Headers.Location = new("https://cdn.example.test/private-path?token=location-secret");
            return response;
        });
        using var http = new HttpClient(handler);
        using var platform = new AyakaModsPlatformAdapter(http, logger: logger);
        platform.SetApiKey("synthetic-key");
        var error = await Assert.ThrowsAsync<PlatformRequestException>(() => platform.GetModInfoAsync("42"));
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        var log = Assert.Single(logger.Messages);
        Assert.Contains("Ayaka API response", log);
        Assert.Contains("获取模组信息", log);
        Assert.Contains("GET https://ayakamods.com/api/mods/42/", log);
        Assert.Contains("HTTP 400", log);
        Assert.Contains("\"code\":400", log);
        Assert.Contains("parameter_invalid", log);
        Assert.Contains("Invalid version 9", log);
        Assert.Contains("request-400-example", log);
        Assert.Contains("application/json", log);
        Assert.Contains("\"user_id\":987654", log);
        Assert.Contains("private@example.com", log);
        Assert.Contains("https://cdn.example.test/private-path?signature=[REDACTED]", log);
        foreach (var secret in new[] { "synthetic-key", "response-secret", "access-secret", "cookie-secret", "signed-secret", "location-secret" })
        {
            Assert.DoesNotContain(secret, log);
            Assert.DoesNotContain(secret, string.Join("\n", logger.Fields));
            Assert.DoesNotContain(secret, error.ToString());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Api_DebugLog_ShouldRespectConfiguredLevelAndPreserveResponseParsing(bool enabled)
    {
        var logger = new AyakaResponseLogger { Enabled = enabled };
        using var handler = ApiHandler();
        using var http = new HttpClient(handler);
        using var platform = new AyakaModsPlatformAdapter(http, logger: logger);
        platform.SetApiKey("synthetic-key");
        Assert.Equal("Armor", (await platform.GetModInfoAsync("42")).Name);
        Assert.Equal(enabled ? 1 : 0, logger.Messages.Count);
        if (enabled) Assert.Contains("Armor", logger.Messages[0]);
        logger.Enabled = !enabled;
        platform.SetApiKey("another-key"); // Invalidate metadata cache; dynamic log level applies on next request.
        Assert.Equal("Armor", (await platform.GetModInfoAsync("42")).Name);
        Assert.Single(logger.Messages);
    }

    [Fact]
    public async Task Api_DebugLog_ShouldIncludePlainTextFailureAndSanitizeCredentialEchoes()
    {
        var logger = new AyakaResponseLogger();
        using var handler = new Handler(_ => new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("<html>Invalid parameter\nsynthetic-key token=unknown-secret Bearer bearer-secret private@example.com https://cdn.example.test/private-path?key=query-secret\nlast diagnostic line</html>", Encoding.UTF8, "text/html")
        });
        using var http = new HttpClient(handler);
        using var platform = new AyakaModsPlatformAdapter(http, logger: logger);
        platform.SetApiKey("synthetic-key");
        await Assert.ThrowsAsync<PlatformRequestException>(() => platform.GetModInfoAsync("42"));
        var log = Assert.Single(logger.Messages);
        Assert.Contains("Invalid parameter", log);
        Assert.Contains("last diagnostic line", log);
        Assert.Contains("private@example.com", log);
        Assert.Contains("https://cdn.example.test/private-path?key=[REDACTED]", log);
        foreach (var secret in new[] { "synthetic-key", "unknown-secret", "bearer-secret", "query-secret" })
            Assert.DoesNotContain(secret, log);
    }

    [Fact]
    public async Task Api_DebugLog_ShouldKeepListParametersAndPublicFileMetadata()
    {
        var logger = new AyakaResponseLogger();
        using var handler = new Handler(request => Json(request.RequestUri!.AbsolutePath.Contains("mod-games/")
            ? """{"mods":[],"pagination":{"current_page":2,"per_page":20,"total":21}}"""
            : request.RequestUri.AbsolutePath.EndsWith("/versions/") ? Versions : Mod));
        using var http = new HttpClient(handler);
        using var platform = new AyakaModsPlatformAdapter(http, logger: logger);
        platform.SetApiKey("synthetic-key");
        await platform.BrowsePageAsync(ModBrowseFeed.LatestUpdated, 2);
        await platform.GetFilesAsync("42");
        Assert.Contains(logger.Messages, log => log.Contains("获取游戏模组列表") && log.Contains("/api/mod-games/119/mods/"));
        Assert.Contains(logger.Messages, log => log.Contains("获取模组版本与文件列表") && log.Contains("/api/mods/42/versions/"));
        Assert.Contains("GET https://ayakamods.com/api/mod-games/119/mods/?order=last_update&direction=desc&page=2", string.Join("\n", logger.Messages));
        Assert.Contains("https://untrusted.test/raw-secret", string.Join("\n", logger.Messages)); // No credential field: retain the full address.
    }

    [Theory]
    [InlineData(400)]
    [InlineData(200)]
    public async Task Download_DebugLog_ShouldPreserveErrorBodyWithoutChangingFailure(int status)
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-AyakaLog-" + Guid.NewGuid().ToString("N"));
        var logger = new AyakaResponseLogger();
        using var api = ApiHandler();
        using var download = new Handler(_ => new((HttpStatusCode)status)
        { Content = new StringContent("""{"errors":[{"code":"parameter_invalid","message":"Selected file invalid; synthetic-key"}]}""", Encoding.UTF8, "application/json") });
        using var http = new HttpClient(api);
        using var platform = new AyakaModsPlatformAdapter(http, new Options(), root, root, download, logger);
        platform.SetApiKey("synthetic-key");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => platform.DownloadFileAsync("42", "ayaka-file:9:10"));
            var log = Assert.Single(logger.Messages, line => line.Contains("下载所选文件"));
            Assert.Contains("GET https://ayakamods.com/api/mod-versions/9/download?file=10", log);
            Assert.Contains("HTTP " + status, log);
            Assert.Contains("parameter_invalid", log);
            Assert.Contains("Selected file invalid", log);
            Assert.DoesNotContain("synthetic-key", log);
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Download_DebugLog_ShouldKeepRedirectRoutesAndParametersButRedactCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-AyakaLog-" + Guid.NewGuid().ToString("N"));
        var logger = new AyakaResponseLogger();
        var bytes = Encoding.UTF8.GetBytes("private-archive-content-never-log");
        using var api = ApiHandler();
        using var download = new Handler(request => request.RequestUri!.Host == "ayakamods.com"
            ? new(HttpStatusCode.Found) { Headers = { Location = new("https://cdn.example.test/archive/armor.zip?signature=signed-secret&file=10&expires=1792065126") } }
            : new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes)
                { Headers = { ContentType = new("application/zip"), ContentDisposition = new("attachment") { FileName = "armor.zip" } } } });
        using var http = new HttpClient(api);
        using var platform = new AyakaModsPlatformAdapter(http, new Options(), root, root, download, logger);
        platform.SetApiKey("synthetic-key");
        try
        {
            await using var stream = await platform.DownloadFileAsync("42", "ayaka-file:9:10");
            using var output = new MemoryStream();
            await stream.CopyToAsync(output);
            Assert.Equal(bytes, output.ToArray());
            var logs = logger.Messages.Where(line => line.Contains("下载所选文件")).ToArray();
            Assert.Equal(2, logs.Length);
            Assert.Contains("HTTP 302", logs[0]);
            Assert.Contains("GET https://ayakamods.com/api/mod-versions/9/download?file=10", logs[0]);
            Assert.All(logs, log => Assert.Contains("https://cdn.example.test/archive/armor.zip?signature=[REDACTED]&file=10&expires=1792065126", log));
            Assert.Contains("GET https://cdn.example.test/", logs[1]);
            foreach (var secret in new[] { "signed-secret", "synthetic-key", "private-archive-content-never-log" })
            {
                Assert.DoesNotContain(secret, string.Join("\n", logs));
                Assert.DoesNotContain(secret, string.Join("\n", logger.Fields));
            }
            Assert.Equal("https://cdn.example.test/archive/armor.zip?signature=signed-secret&file=10&expires=1792065126", download.Requests.Last().Url);
            Assert.Null(download.Requests.Last().Key);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("https://ayakamods.com/api/mod-versions/8348/download?file=39440", "https://ayakamods.com/api/mod-versions/8348/download?file=39440")]
    [InlineData("https://cdn.example.test/armor.zip?file=39440&to%6Ben=encoded%2Bsecret&page=2", "https://cdn.example.test/armor.zip?file=39440&to%6Ben=[REDACTED]&page=2")]
    [InlineData("https://cdn.example.test/armor.zip?X-Amz-Signature=aws-secret&X-Amz-Credential=credential-secret&X-Amz-Expires=300", "https://cdn.example.test/armor.zip?X-Amz-Signature=[REDACTED]&X-Amz-Credential=[REDACTED]&X-Amz-Expires=300")]
    [InlineData("https://user:password-secret@cdn.example.test/armor.zip?file=39440", "https://[REDACTED]@cdn.example.test/armor.zip?file=39440")]
    [InlineData("https://ayakamods.com/callback#access_token=fragment-secret&file=39440", "https://ayakamods.com/callback#access_token=[REDACTED]&file=39440")]
    public async Task Api_DebugLog_ShouldRedactOnlyUrlCredentialsAndKeepPublicFields(string url, string expected)
    {
        var logger = new AyakaResponseLogger();
        var body = JsonSerializer.Serialize(new
        {
            mod = new { mod_id = 4260, game_id = 119, title = "Runtime", username = "Builder", user_id = 987654,
                download_url = url, profile_url = "https://ayakamods.com/members/builder.987654/", can_download = false }
        });
        using var handler = new Handler(_ => Json(body));
        using var http = new HttpClient(handler);
        using var platform = new AyakaModsPlatformAdapter(http, logger: logger);
        platform.SetApiKey("synthetic-key");
        Assert.Equal("Runtime", (await platform.GetModInfoAsync("4260")).Name);
        var log = Assert.Single(logger.Messages);
        Assert.Contains(expected, log);
        Assert.Contains("\"username\":\"Builder\"", log);
        Assert.Contains("\"user_id\":987654", log);
        Assert.Contains("https://ayakamods.com/members/builder.987654/", log);
        Assert.Contains("\"can_download\":false", log);
        foreach (var secret in new[] { "synthetic-key", "encoded%2Bsecret", "encoded+secret", "aws-secret", "credential-secret", "password-secret", "fragment-secret" })
        {
            Assert.DoesNotContain(secret, log);
            Assert.DoesNotContain(secret, string.Join("\n", logger.Fields));
        }
    }

    private sealed class AyakaResponseLogger : ILogger<AyakaModsPlatformAdapter>
    {
        public bool Enabled { get; set; } = true;
        public List<string> Messages { get; } = new();
        public List<string> Fields { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => Enabled && level == LogLevel.Debug;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            Assert.Equal(LogLevel.Debug, level);
            Messages.Add(formatter(state, error));
            if (state is IEnumerable<KeyValuePair<string, object?>> fields)
                Fields.AddRange(fields.Select(field => field.Value?.ToString() ?? ""));
        }
    }
}
