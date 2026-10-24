using System.Net;
using System.Security.Cryptography;
using System.Text;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Platforms.AyakaMods;

namespace HD2Mimnt.Tests.Platforms;

public sealed class AyakaModsOAuthTests
{
    private static AyakaModsOAuthOptions Options() => new() { ClientId = "synthetic-public-client" };
    private static AyakaModsOAuthTokens Old() => new() { AccessToken = "old-access", RefreshToken = "old-refresh" };
    private static Dictionary<string, string> Fields(string query) => query.TrimStart('?').Split('&')
        .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    [Fact]
    public async Task Login_ShouldUseDynamicLoopbackS256AndRejectUnmatchedOrDuplicateCallbacks()
    {
        using var handler = new TokenHandler();
        using var http = new HttpClient(handler);
        using var browser = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var client = new AyakaModsOAuthClient(http);
        Uri? authorize = null;
        var login = client.LoginAsync(Options(), uri => authorize = uri, timeout.Token);
        var query = Fields(authorize!.Query);
        Assert.Equal("https://ayakamods.com/oauth2/authorize", authorize.GetLeftPart(UriPartial.Path));
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("mod:read mod_rating:read", query["scope"]);
        Assert.Equal(Options().ClientId, query["client_id"]);
        Assert.False(query.ContainsKey("code_verifier"));
        var redirect = new Uri(query["redirect_uri"]);
        Assert.Equal("127.0.0.1", redirect.Host);
        Assert.True(redirect.Port > 0 && redirect.Port != 80);
        Assert.Equal(new Uri(Options().RedirectUri).AbsolutePath, redirect.AbsolutePath);
        foreach (var suffix in new[] { "?state=wrong&code=bad", "?code=bad",
            $"?state={query["state"]}&state={query["state"]}&code=bad",
            $"?state={query["state"]}&code=one&code=two" })
        {
            using var bad = await browser.GetAsync(redirect + suffix, timeout.Token);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.False(login.IsCompleted);
        }
        using var good = await browser.GetAsync(redirect + $"?state={query["state"]}&code=synthetic-code", timeout.Token);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Equal("no-store", good.Headers.CacheControl!.ToString());
        var tokens = await login;
        Assert.Equal("new-access", tokens.AccessToken); // Opaque tokens are supported; not a JWT contract.
        Assert.Equal("new-refresh", tokens.RefreshToken);
        var fields = handler.Form!;
        Assert.Equal("authorization_code", fields["grant_type"]);
        Assert.Equal("synthetic-code", fields["code"]);
        Assert.Equal(redirect.AbsoluteUri, fields["redirect_uri"]);
        Assert.False(fields.ContainsKey("client_secret"));
        Assert.InRange(fields["code_verifier"].Length, 43, 128);
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(fields["code_verifier"])))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(query["code_challenge"], challenge);
        Assert.Equal(1, handler.Requests);
        Assert.DoesNotContain(tokens.RefreshToken, tokens.ToString());
        await Assert.ThrowsAsync<HttpRequestException>(() => browser.GetAsync(redirect, timeout.Token));
    }

    [Fact]
    public async Task Login_CancelOrDeny_ShouldCloseListenerWithoutTokenExchange()
    {
        using var handler = new TokenHandler();
        using var http = new HttpClient(handler);
        using var browser = new HttpClient(new HttpClientHandler { UseProxy = false });
        var client = new AyakaModsOAuthClient(http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        Uri? authorize = null;
        var login = client.LoginAsync(Options(), uri => authorize = uri, cancel.Token);
        var redirect = Fields(authorize!.Query)["redirect_uri"];
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        await Assert.ThrowsAsync<HttpRequestException>(() => browser.GetAsync(redirect, timeout.Token));
        var denied = client.LoginAsync(Options(), uri => authorize = uri, timeout.Token);
        var query = Fields(authorize!.Query);
        using var response = await browser.GetAsync(query["redirect_uri"] + $"?state={query["state"]}&error=access_denied", timeout.Token);
        Assert.Contains("拒绝", (await Assert.ThrowsAsync<AyakaModsOAuthException>(() => denied)).Message);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Refresh_ShouldRotateTokensAndRetainPreviousRefreshIfOmitted()
    {
        using var handler = new TokenHandler();
        using var http = new HttpClient(handler);
        var client = new AyakaModsOAuthClient(http);
        var next = await client.RefreshAsync(Options(), Old());
        Assert.Equal("refresh_token", handler.Form!["grant_type"]);
        Assert.Equal("old-refresh", handler.Form["refresh_token"]);
        handler.OmitRefresh = true;
        Assert.Equal(next.RefreshToken, (await client.RefreshAsync(Options(), next)).RefreshToken);
    }

    [Theory]
    [InlineData(400, true)]
    [InlineData(401, true)]
    [InlineData(429, false)]
    [InlineData(503, false)]
    public async Task Refresh_Failure_ShouldKeepTransientErrorsSeparateAndHideSecrets(int status, bool requiresLogin)
    {
        using var handler = new TokenHandler { Status = (HttpStatusCode)status };
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<AyakaModsOAuthException>(() => new AyakaModsOAuthClient(http).RefreshAsync(Options(), Old()));
        Assert.Equal(requiresLogin, error.RequiresLogin);
        Assert.DoesNotContain("server-secret", error.ToString());
    }

    [Theory]
    [InlineData("{\"access_token\":\"server-secret\"}")]
    [InlineData("{\"access_token\":\"server-secret\",\"refresh_token\":\"refresh\",\"token_type\":\"basic\",\"expires_in\":3600}")]
    [InlineData("{\"access_token\":\"server-secret\",\"refresh_token\":\"refresh\",\"token_type\":\"bearer\",\"expires_in\":0}")]
    public async Task Refresh_InvalidToken_ShouldNotExposeResponse(string json)
    {
        using var handler = new TokenHandler { Body = json };
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<AyakaModsOAuthException>(() => new AyakaModsOAuthClient(http).RefreshAsync(Options(), Old()));
        Assert.DoesNotContain("server-secret", error.ToString());
    }

    [Fact]
    public async Task Login_MissingRegistration_ShouldNotOpenBrowser()
    {
        using var http = new HttpClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AyakaModsOAuthClient(http).LoginAsync(new(), _ => Assert.Fail("Unexpected browser")));
    }

    private sealed class TokenHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public bool OmitRefresh { get; set; }
        public string? Body { get; init; }
        public Dictionary<string, string>? Form { get; private set; }
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://ayakamods.com/api/oauth2/token", request.RequestUri!.AbsoluteUri);
            Assert.False(request.Headers.Contains("XF-Api-Key"));
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            Form = Fields(await request.Content.ReadAsStringAsync(token));
            return new(Status) { Content = new StringContent(Body ?? (Status != HttpStatusCode.OK ? "server-secret" :
                "{\"access_token\":\"new-access\",\"token_type\":\"bearer\",\"expires_in\":3600" +
                (OmitRefresh ? "}" : ",\"refresh_token\":\"new-refresh\"}")), Encoding.UTF8, "application/json") };
        }
    }
}
