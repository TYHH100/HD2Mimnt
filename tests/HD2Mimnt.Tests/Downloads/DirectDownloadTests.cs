using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Downloads;

namespace HD2Mimnt.Tests.Downloads;

public sealed class DirectDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-direct-tests-{Guid.NewGuid():N}");
    private readonly byte[] _data = Enumerable.Range(0, 32 * 1024 + 17).Select(i => (byte)(i % 251)).ToArray();
    private readonly OptionsProvider _options = new();
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private DirectDownloadService CreateService()
        => new(new DirectDownloadClientFactory(_options, Path.Combine(_root, "cache"), Path.Combine(_root, "temp")),
            _options, new SettingsService(Path.Combine(_root, "settings")), Path.Combine(_root, "temp"));

    [Fact]
    public async Task Download_RelativeRedirectsAndCookies_UsesFinalUnicodeNameAndExactBytes()
    {
        await using var server = new Server(request => request.Path switch
        {
            "/start" => new(302, "Location: /middle\r\nSet-Cookie: allowed=yes; Path=/\r\n", []),
            "/middle" => new(307, "Location: /payload/fallback.zip?signature=secret\r\n", []),
            _ when request.Headers.GetValueOrDefault("Cookie")?.Contains("allowed=yes") != true => new(403, "", []),
            _ => FileResponse(request, "Content-Disposition: attachment; filename=ascii.zip; filename*=UTF-8''%E4%B8%AD%E6%96%87%E6%A8%A1%E7%BB%84.zip\r\n")
        });
        var destination = Path.Combine(_root, "out");
        var saved = await CreateService().DownloadAsync(server.Address + "start", "", destination,
            new(DownloadProxyMode.Direct));
        Assert.Equal("中文模组.zip", Path.GetFileName(saved));
        Assert.Equal(_data, await File.ReadAllBytesAsync(saved));
        Assert.True(server.Requests.Count(r => r.Headers.ContainsKey("Range")) >= 2);
        Assert.DoesNotContain(server.Requests, r => r.Headers.ContainsKey("Authorization") || r.Headers.ContainsKey("apikey"));
        Assert.Single(Directory.GetFiles(destination));
    }

    [Fact]
    public async Task Download_CustomHttpProxy_RedirectAndEverySegmentUseProxy()
    {
        var authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("proxy-user:proxy-password"));
        await using var proxy = new Server(request => request.Headers.GetValueOrDefault("Proxy-Authorization") != authorization
            ? new(407, "Proxy-Authenticate: Basic realm=\"test\"\r\n", [])
            : request.Path == "/start"
                ? new(302, "Location: http://cdn.example.invalid/files/proxied.zip\r\n", []) : FileResponse(request));
        var saved = await CreateService().DownloadAsync("http://origin.example.invalid/start", "", Path.Combine(_root, "out"),
            new(DownloadProxyMode.Custom, proxy.Address, "proxy-user", "proxy-password"));
        Assert.Equal("proxied.zip", Path.GetFileName(saved));
        Assert.Equal(_data, await File.ReadAllBytesAsync(saved));
        Assert.All(proxy.Requests, request => Assert.StartsWith("http://", request.Target));
        Assert.Contains(proxy.Requests, request => request.Target.Contains("origin.example.invalid"));
        Assert.Contains(proxy.Requests, request => request.Target.Contains("cdn.example.invalid") && request.Headers.ContainsKey("Range"));
        Assert.DoesNotContain(proxy.Requests, request => request.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Download_ExistingFile_GeneratesNumberedNameAndPreservesOriginal()
    {
        await using var server = new Server(request => FileResponse(request));
        var destination = Path.Combine(_root, "out"); Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "mod.zip"), "existing");
        var saved = await CreateService().DownloadAsync(server.Address + "mod.zip", "", destination, new(DownloadProxyMode.Direct));
        Assert.Equal("mod (1).zip", Path.GetFileName(saved));
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(destination, "mod.zip")));
        Assert.Equal(_data, await File.ReadAllBytesAsync(saved));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadAndImport_AwaitsImporterAndAlwaysCleansSource(bool failImport)
    {
        await using var server = new Server(request => FileResponse(request));
        string? sourcePath = null;
        var service = CreateService();
        async Task Import(string archive, CancellationToken token)
        {
            sourcePath = archive;
            Assert.Equal(_data, await File.ReadAllBytesAsync(archive, token));
            await Task.Yield();
            Assert.True(File.Exists(archive));
            if (failImport) throw new InvalidDataException("import failed");
        }
        var operation = service.DownloadAsync(server.Address + "mod.zip", "", null, new(DownloadProxyMode.Direct), Import);
        if (failImport) await Assert.ThrowsAsync<InvalidDataException>(() => operation);
        else await operation;
        Assert.NotNull(sourcePath);
        Assert.False(File.Exists(sourcePath));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "temp")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "cache")));
    }

    [Fact]
    public async Task Download_CancelDuringBody_AwaitsCleanupAndLeavesNoIncompleteFinalFile()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await using var server = new Server(_ =>
        {
            if (Interlocked.Increment(ref count) == 1) return new(200, "", _data);
            return new(200, "", _data, async token => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); });
        });
        var destination = Path.Combine(_root, "out");
        using var cancellation = new CancellationTokenSource();
        var operation = CreateService().DownloadAsync(server.Address + "mod.zip", "", destination,
            new(DownloadProxyMode.Direct), token: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Empty(Directory.GetFiles(destination));
    }

    [Theory]
    [InlineData("html")]
    [InlineData("loop")]
    public async Task Download_WebPageOrRedirectLoop_FailsWithoutPublishingFile(string scenario)
    {
        await using var server = new Server(_ => scenario == "html"
            ? new(200, "Content-Type: text/html\r\n", Encoding.UTF8.GetBytes("<html>Login</html>"))
            : new(302, "Location: /loop\r\n", []));
        var destination = Path.Combine(_root, "out");
        var error = await Record.ExceptionAsync(() => CreateService().DownloadAsync(server.Address + scenario,
            "", destination, new(DownloadProxyMode.Direct)));
        Assert.NotNull(error);
        if (scenario == "html") Assert.Equal(DirectDownloadResponseFailure.WebPage, Assert.IsType<DirectDownloadResponseException>(error).Failure);
        Assert.False(Directory.Exists(destination));
        Assert.True(server.Requests.Count <= 21);
    }

    [Theory]
    [InlineData("application/json", "{\"ec\":40100,\"em\":\"\\u8be5\\u529f\\u80fd\\u9700\\u8981\\u767b\\u5f55\\u67e5\\u770b\",\"data\":{}}", DirectDownloadResponseFailure.LoginRequired)]
    [InlineData("text/plain", "{\"success\":false,\"message\":\"Please sign in\"}", DirectDownloadResponseFailure.LoginRequired)]
    [InlineData(null, "{\"code\":\"UNAUTHENTICATED\",\"message\":\"Authentication required\"}", DirectDownloadResponseFailure.LoginRequired)]
    [InlineData("application/problem+json", "{\"status\":401,\"title\":\"Unauthorized\"}", DirectDownloadResponseFailure.LoginRequired)]
    [InlineData("application/json", "{\"code\":403,\"message\":\"Permission denied\"}", DirectDownloadResponseFailure.AccessDenied)]
    [InlineData("application/json", "{\"error\":{\"code\":\"token_expired\",\"message\":\"Session expired\"}}", DirectDownloadResponseFailure.ExpiredLink)]
    [InlineData("application/json", "{\"code\":500,\"message\":\"Internal error private-token\"}", DirectDownloadResponseFailure.ServerError)]
    public async Task Download_Http200ChunkedApiErrors_NeverDownloadsOrImportsErrorBody(string? type, string body,
        DirectDownloadResponseFailure expected)
    {
        await using var server = new Server(_ => new(200, type == null ? "" : $"Content-Type: {type}\r\n", Encoding.UTF8.GetBytes(body), Chunked: true));
        var imported = false; var resolved = false;
        var error = await Assert.ThrowsAsync<DirectDownloadResponseException>(() => CreateService().DownloadAsync(
            server.Address + "redirect", "", null, new(DownloadProxyMode.Direct),
            (_, _) => { imported = true; return Task.CompletedTask; }, resolved: _ => resolved = true));
        Assert.Equal(expected, error.Failure);
        Assert.DoesNotContain("private-token", error.Message);
        Assert.False(imported); Assert.False(resolved);
        Assert.False(Directory.Exists(Path.Combine(_root, "cache")));
        Assert.False(Directory.Exists(Path.Combine(_root, "temp")));
    }

    [Fact]
    public async Task Download_AuthenticationLostAfterMetadata_RejectsFileAndCleansPartial()
    {
        var count = 0;
        await using var server = new Server(request => Interlocked.Increment(ref count) == 1 ? FileResponse(request)
            : new(200, "Content-Type: application/json\r\n", Encoding.UTF8.GetBytes("{\"ec\":40100,\"em\":\"login required\"}")));
        var destination = Path.Combine(_root, "out");
        var error = await Assert.ThrowsAsync<DirectDownloadResponseException>(() => CreateService().DownloadAsync(
            server.Address + "mod.zip", "", destination, new(DownloadProxyMode.Direct)));
        Assert.Equal(DirectDownloadResponseFailure.LoginRequired, error.Failure);
        Assert.Empty(Directory.GetFiles(destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Download_JsonFileOrUnlabelledBinary_PreservesInspectedBytes(bool json)
    {
        var bytes = json ? Encoding.UTF8.GetBytes("{\"name\":\"mod\",\"version\":1}") : _data;
        await using var server = new Server(_ => new(200, json ? "Content-Type: application/json\r\n" : "", bytes, Chunked: true));
        var saved = await CreateService().DownloadAsync(server.Address + (json ? "manifest.json" : "mod.zip"), "",
            Path.Combine(_root, "out"), new(DownloadProxyMode.Direct));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(saved));
    }

    [Theory]
    [InlineData(401, DirectDownloadResponseFailure.LoginRequired)]
    [InlineData(403, DirectDownloadResponseFailure.AccessDenied)]
    public async Task Download_HttpAuthenticationErrors_RequireBrowser(int status, DirectDownloadResponseFailure expected)
    {
        await using var server = new Server(_ => new(status, "Content-Type: text/html\r\n", []));
        var error = await Assert.ThrowsAsync<DirectDownloadResponseException>(() => CreateService().DownloadAsync(
            server.Address + "mod.zip", "", Path.Combine(_root, "out"), new(DownloadProxyMode.Direct)));
        Assert.Equal(expected, error.Failure); Assert.True(error.RequiresBrowser);
    }

    [Fact]
    public async Task ImportBrowserFile_ImporterFails_PreservesUserDownload()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "browser-download.zip"); await File.WriteAllBytesAsync(path, _data);
        await Assert.ThrowsAsync<InvalidDataException>(() => CreateService().ImportBrowserFileAsync(path,
            (_, _) => throw new InvalidDataException("test"), CancellationToken.None));
        Assert.Equal(_data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task SavePreferences_DoesNotPersistCredentialsOrDownloadLink()
    {
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var service = new DirectDownloadService(new DirectDownloadClientFactory(_options, _root, _root), _options, settings, _root);
        await service.SavePreferencesAsync(new(DownloadProxyMode.Custom, "http://127.0.0.1:7890", "private-user", "private-password"), "out");
        var saved = new SettingsService(Path.Combine(_root, "settings"));
        Assert.Equal(DownloadProxyMode.Custom, saved.GetSetting("DirectDownload.ProxyMode", DownloadProxyMode.System));
        var json = System.Text.Json.JsonSerializer.Serialize(await saved.GetAllSettingsAsync());
        Assert.DoesNotContain("private-user", json); Assert.DoesNotContain("private-password", json);
        Assert.DoesNotContain("DirectDownload.Link", json);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SavePreferencesAsync(
            new(DownloadProxyMode.Custom, "http://user:secret@localhost:7890"), "out"));
    }

    [Theory]
    [InlineData("../mod.zip", "mod.zip")]
    [InlineData("C:\\temp\\mod.zip", "mod.zip")]
    [InlineData("CON.zip", "_CON.zip")]
    [InlineData("a:b?.zip", "a_b_.zip")]
    [InlineData("...", "download.zip")]
    public void SanitizeFileName_UnsafeNames_StayInsideDestination(string input, string expected)
        => Assert.Equal(expected, DirectDownloadService.SanitizeFileName(input));

    private Reply FileResponse(Request request, string headers = "")
    {
        headers += "Accept-Ranges: bytes\r\nETag: \"revision-1\"\r\n";
        if (!request.Headers.TryGetValue("Range", out var value)) return new(200, headers, _data);
        var range = value[6..].Split('-');
        var start = int.Parse(range[0]); var end = int.Parse(range[1]);
        return new(206, headers + $"Content-Range: bytes {start}-{end}/{_data.Length}\r\n", _data[start..(end + 1)]);
    }

    private sealed class OptionsProvider : IDownloadOptionsProvider
    {
        public Task<DownloadOptions> GetOptionsAsync() => Task.FromResult(new DownloadOptions
        {
            MaxParallelSegments = 3, MinimumSegmentSize = 1024, MaxRetries = 0, EnableResume = true
        });
    }

    private sealed record Request(string Target, Dictionary<string, string> Headers)
    {
        public string Path => new Uri(new Uri("http://localhost"), Target).AbsolutePath;
    }
    private sealed record Reply(int Status, string Headers, byte[] Body, Func<CancellationToken, Task>? BeforeBody = null, bool Chunked = false);

    /// <summary>Real local HTTP transport also acts as a forward proxy without external network access.</summary>
    private sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _connections = [];
        private readonly Task _loop;
        public ConcurrentBag<Request> Requests { get; } = [];
        public string Address { get; }
        public Server(Func<Request, Reply> respond)
        {
            _listener.Start();
            Address = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            _loop = AcceptAsync(respond);
        }
        private async Task AcceptAsync(Func<Request, Reply> respond)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _connections.Add(HandleAsync(connection, respond));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        private async Task HandleAsync(TcpClient connection, Func<Request, Reply> respond)
        {
            using (connection)
            try
            {
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                var line = await reader.ReadLineAsync(_stop.Token);
                if (line == null) return;
                var target = line.Split(' ')[1];
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_stop.Token)))
                {
                    var colon = line.IndexOf(':'); headers[line[..colon]] = line[(colon + 1)..].Trim();
                }
                var request = new Request(target, headers);
                Requests.Add(request);
                var response = respond(request);
                var length = response.Chunked ? "Transfer-Encoding: chunked\r\n" : $"Content-Length: {response.Body.Length}\r\n";
                var responseHeaders = Encoding.ASCII.GetBytes($"HTTP/1.1 {response.Status} Response\r\nConnection: close\r\n{length}{response.Headers}\r\n");
                await stream.WriteAsync(responseHeaders, _stop.Token);
                await stream.FlushAsync(_stop.Token);
                if (response.BeforeBody != null) await response.BeforeBody(_stop.Token);
                if (response.Chunked) await stream.WriteAsync(Encoding.ASCII.GetBytes($"{response.Body.Length:X}\r\n"), _stop.Token);
                await stream.WriteAsync(response.Body, _stop.Token);
                if (response.Chunked) await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n0\r\n\r\n"), _stop.Token);
            }
            catch (IOException) { }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); await _loop; _listener.Stop(); await Task.WhenAll(_connections); _stop.Dispose();
        }
    }
}
