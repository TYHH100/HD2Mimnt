using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Platforms.NexusMods;

namespace HD2Mimnt.Tests.Platforms;

public sealed class NexusModsOAuthTests
{
    [Fact]
    public async Task Login_ValidLoopbackCallback_ShouldUseS256RejectWrongStateAndExchangeOneCode()
    {
        using var rsa = RSA.Create(2048);
        using var handler = new TokenHandler(rsa);
        using var http = new HttpClient(handler);
        using var browser = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var options = Options();
        var opened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new NexusModsOAuthClient(http, tokenPublicKey: rsa.ExportSubjectPublicKeyInfoPem());
        var login = client.LoginAsync(options, uri => opened.SetResult(uri), timeout.Token);
        var authorize = await opened.Task.WaitAsync(timeout.Token);
        var parameters = Query(authorize.Query);
        Assert.Equal("https://users.nexusmods.com/oauth/authorize", authorize.GetLeftPart(UriPartial.Path));
        Assert.Equal("S256", parameters["code_challenge_method"]);
        Assert.Equal("code", parameters["response_type"]);
        Assert.Equal(options.RedirectUri, parameters["redirect_uri"]);
        Assert.Equal(options.ClientId, parameters["client_id"]);
        Assert.Equal("", parameters["scope"]);
        Assert.InRange(parameters["state"].Length, 43, 128);
        Assert.False(parameters.ContainsKey("code_verifier"));
        using (var bad = await browser.GetAsync(options.RedirectUri + "?state=wrong&code=bad-code", timeout.Token))
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.False(login.IsCompleted);
        using (var duplicate = await browser.GetAsync(options.RedirectUri + "?state=" + parameters["state"] +
            "&state=" + parameters["state"] + "&code=bad-code", timeout.Token))
            Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        using (var duplicateCode = await browser.GetAsync(options.RedirectUri + "?state=" + parameters["state"] +
            "&code=bad-one&code=bad-two", timeout.Token))
            Assert.Equal(HttpStatusCode.BadRequest, duplicateCode.StatusCode);
        using (var missingState = await browser.GetAsync(options.RedirectUri + "?code=bad-code", timeout.Token))
            Assert.Equal(HttpStatusCode.BadRequest, missingState.StatusCode);
        using var good = await browser.GetAsync(options.RedirectUri + "?state=" + parameters["state"] + "&code=synthetic-code", timeout.Token);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Equal("no-store", good.Headers.CacheControl!.ToString());
        var tokens = await login;
        Assert.Equal("synthetic-code", handler.Fields!["code"]);
        Assert.Equal("authorization_code", handler.Fields["grant_type"]);
        Assert.Equal(options.ClientId, handler.Fields["client_id"]);
        Assert.Equal("", handler.Fields["scope"]);
        Assert.Equal(options.RedirectUri, handler.Fields["redirect_uri"]);
        var verifier = handler.Fields["code_verifier"];
        Assert.InRange(verifier.Length, 43, 128);
        Assert.Equal(parameters["code_challenge"], Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        Assert.False(handler.Fields.ContainsKey("client_secret"));
        Assert.Null(handler.Authorization);
        Assert.Equal("application/x-www-form-urlencoded", handler.ContentType);
        Assert.Equal("Tester", tokens.UserName);
        Assert.Equal("123", tokens.UserId);
        Assert.Equal(1, handler.Requests);
        Assert.DoesNotContain(tokens.RefreshToken, tokens.ToString());
        await Assert.ThrowsAsync<HttpRequestException>(() => browser.GetAsync(options.RedirectUri, timeout.Token));
    }

    [Fact]
    public async Task Login_CancelledOrDenied_ShouldReleaseListenerWithoutTokenRequest()
    {
        using var rsa = RSA.Create(2048);
        using var handler = new TokenHandler(rsa);
        using var http = new HttpClient(handler);
        using var browser = new HttpClient(new HttpClientHandler { UseProxy = false });
        var options = Options();
        var client = new NexusModsOAuthClient(http, tokenPublicKey: rsa.ExportSubjectPublicKeyInfoPem());
        using var cts = new CancellationTokenSource();
        var login = client.LoginAsync(options, _ => cts.Cancel(), cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        // 立即在同一端口重启登录，证明取消释放了监听。
        Uri? authorize = null;
        var retry = client.LoginAsync(options, uri => authorize = uri);
        var query = Query(authorize!.Query);
        using var rejected = await browser.GetAsync(options.RedirectUri + "?state=" + query["state"] + "&error=access_denied");
        var ex = await Assert.ThrowsAsync<NexusModsOAuthException>(() => retry);
        Assert.Contains("拒绝", ex.Message);
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("expired")]
    [InlineData("issuer")]
    [InlineData("algorithm")]
    [InlineData("user")]
    public async Task Token_InvalidJwt_ShouldRejectWithoutExposingToken(string fault)
    {
        using var rsa = RSA.Create(2048);
        using var handler = new TokenHandler(rsa) { Fault = fault };
        using var http = new HttpClient(handler);
        var client = new NexusModsOAuthClient(http, tokenPublicKey: rsa.ExportSubjectPublicKeyInfoPem());
        var ex = await Assert.ThrowsAsync<NexusModsOAuthException>(() => client.RefreshAsync(Options(), OldTokens()));
        Assert.Contains("无效", ex.Message);
        Assert.DoesNotContain("synthetic-refresh", ex.Message);
        Assert.DoesNotContain(handler.AccessToken!, ex.Message);
    }

    [Fact]
    public async Task Refresh_ShouldRotateTokensAndRetainRefreshTokenWhenResponseOmitsIt()
    {
        using var rsa = RSA.Create(2048);
        using var handler = new TokenHandler(rsa);
        using var http = new HttpClient(handler);
        var client = new NexusModsOAuthClient(http, tokenPublicKey: rsa.ExportSubjectPublicKeyInfoPem());
        var options = Options();
        var tokens = await client.RefreshAsync(options, OldTokens());
        Assert.Equal("refresh_token", handler.Fields!["grant_type"]);
        Assert.Equal(options.ClientId, handler.Fields["client_id"]);
        Assert.Equal("synthetic-refresh-old", handler.Fields["refresh_token"]);
        Assert.False(handler.Fields.ContainsKey("client_secret"));
        Assert.Equal("synthetic-refresh-new", tokens.RefreshToken);
        handler.OmitRefreshToken = true;
        var next = await client.RefreshAsync(options, tokens);
        Assert.Equal(tokens.RefreshToken, next.RefreshToken);
    }

    [Theory]
    [InlineData(400, true)]
    [InlineData(401, true)]
    [InlineData(429, false)]
    [InlineData(503, false)]
    public async Task Refresh_HttpFailure_ShouldDistinguishRevocationAndHideResponse(int status, bool requiresLogin)
    {
        using var rsa = RSA.Create(2048);
        using var handler = new TokenHandler(rsa) { Status = (HttpStatusCode)status };
        using var http = new HttpClient(handler);
        var client = new NexusModsOAuthClient(http, tokenPublicKey: rsa.ExportSubjectPublicKeyInfoPem());
        var ex = await Assert.ThrowsAsync<NexusModsOAuthException>(() => client.RefreshAsync(Options(), OldTokens()));
        Assert.Equal(requiresLogin, ex.RequiresLogin);
        Assert.Contains(status.ToString(), ex.Message);
        Assert.DoesNotContain("synthetic-refresh-old", ex.Message);
    }

    [Fact]
    public async Task Login_MissingRegistration_ShouldNotOpenBrowserOrListen()
    {
        using var http = new HttpClient();
        var opened = false;
        var client = new NexusModsOAuthClient(http);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.LoginAsync(new(), _ => opened = true));
        Assert.False(opened);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task Token_CurrentOfficialJwks_ShouldVerifyRotationAndRejectWrongKeyOrSignature(bool wrongKey, bool corrupt, bool reject)
    {
        using var previous = RSA.Create(2048);
        using var current = RSA.Create(2048);
        using var handler = new TokenHandler(current) { UseJwks = true, WrongJwksKey = wrongKey,
            Fault = corrupt ? "signature" : null };
        using var http = new HttpClient(handler);
        var client = new NexusModsOAuthClient(http, tokenPublicKey: previous.ExportSubjectPublicKeyInfoPem());
        if (reject)
        {
            await Assert.ThrowsAsync<NexusModsOAuthException>(() => client.RefreshAsync(Options(), OldTokens()));
        }
        else
        {
            var tokens = await client.RefreshAsync(Options(), OldTokens());
            Assert.Equal("Tester", tokens.UserName);
            await client.RefreshAsync(Options(), tokens);
        }
        Assert.Equal(1, handler.KeyRequests);
    }

    private static NexusModsOAuthOptions Options()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        return new() { ClientId = "hd2mimnt-test", RedirectUri = $"http://127.0.0.1:{port}/oauth/nexus/callback/" };
    }
    private static NexusModsOAuthTokens OldTokens() => new()
    {
        AccessToken = "synthetic-old-access", RefreshToken = "synthetic-refresh-old", UserId = "123",
        UserName = "Tester", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
    };
    private static Dictionary<string, string> Query(string input) => input.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
        .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length == 2 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "");
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class TokenHandler(RSA rsa) : HttpMessageHandler
    {
        public string? Fault { get; init; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public bool OmitRefreshToken { get; set; }
        public bool UseJwks { get; init; }
        public bool WrongJwksKey { get; init; }
        public int KeyRequests { get; private set; }
        public Dictionary<string, string>? Fields { get; private set; }
        public string? Authorization { get; private set; }
        public string? ContentType { get; private set; }
        public string? AccessToken { get; private set; }
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            Assert.Equal("HD2Mimnt", request.Headers.GetValues("Application-Name").Single());
            Assert.False(string.IsNullOrWhiteSpace(request.Headers.GetValues("Application-Version").Single()));
            if (request.RequestUri!.AbsolutePath == "/oauth/discovery/keys")
            {
                Assert.Equal("https://users.nexusmods.com/oauth/discovery/keys", request.RequestUri.AbsoluteUri);
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Null(request.Headers.Authorization);
                KeyRequests++;
                using var other = RSA.Create(2048);
                var key = (WrongJwksKey ? other : rsa).ExportParameters(false);
                var keyBody = new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = "current-key",
                    n = Encode(key.Modulus!), e = Encode(key.Exponent!) } } };
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(keyBody)) };
            }
            Assert.Equal("https://users.nexusmods.com/oauth/token", request.RequestUri!.AbsoluteUri);
            Fields = Query(await request.Content!.ReadAsStringAsync(token));
            Authorization = request.Headers.Authorization?.ToString();
            ContentType = request.Content.Headers.ContentType!.MediaType;
            if (Status != HttpStatusCode.OK) return new(Status) { Content = new StringContent("echo synthetic-refresh-old") };
            var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = Fault == "algorithm" ? "none" : "RS256",
                kid = UseJwks ? "current-key" : null, jku = "https://untrusted.invalid/keys" }));
            var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iss = Fault == "issuer" ? "wrong-service" : "nexus-user-service", sub = "123",
                exp = DateTimeOffset.UtcNow.AddMinutes(Fault == "expired" ? -10 : 30).ToUnixTimeSeconds(),
                user = new { id = Fault == "user" ? 999 : 123, username = "Tester" }
            }));
            var signature = rsa.SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (Fault == "signature") signature[0] ^= 0xFF;
            AccessToken = header + "." + payload + "." + Encode(signature);
            var body = new Dictionary<string, object> { ["access_token"] = AccessToken, ["token_type"] = "Bearer", ["expires_in"] = 1800 };
            if (!OmitRefreshToken) body["refresh_token"] = "synthetic-refresh-new";
            return new(Status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        }
    }
}
