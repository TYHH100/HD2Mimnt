using System.Net;
using System.Text;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Platforms;
using HD2Mimnt.Infrastructure.Repositories;
using Moq;

namespace HD2Mimnt.Tests.Services;

public sealed class NexusModsAuthTests
{
    private static readonly NexusModsOAuthOptions Options = new() { ClientId = "hd2mimnt-test" };

    [Fact]
    public async Task TestClientId_ShouldPersistApplyImmediatelyRestoreDefaultAndPreservePersonalKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-OAuthConfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var http = new HttpClient();
            var registry = new PlatformRegistry();
            registry.RegisterPlatform(new NexusModsPlatformAdapter(http));
            var oauth = new FakeOAuth();
            var settings = new SettingsService(root);
            var auth = new NexusModsAuthService(registry, new MemoryStore(), oauth, Options, settings: settings);
            await auth.ApplyApiKeyAsync("synthetic-personal-key");
            await auth.ApplyOAuthClientIdAsync("  registered-test-client  ");
            Assert.Equal("registered-test-client", auth.OAuthClientId);
            Assert.True(auth.IsApiKeySaved);
            var restarted = new NexusModsAuthService(registry, oauthClient: oauth, options: Options,
                settings: new SettingsService(root));
            Assert.Equal("registered-test-client", restarted.OAuthClientId);
            await restarted.LoginAsync(_ => { });
            Assert.Equal("registered-test-client", oauth.LastClientId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ApplyOAuthClientIdAsync("another-client"));
            Assert.Equal("registered-test-client", restarted.OAuthClientId);
            Assert.True(restarted.IsOAuthSignedIn);
            await restarted.LogoutAsync();
            await restarted.ApplyOAuthClientIdAsync("");
            Assert.Equal(Options.ClientId, restarted.OAuthClientId);
            Assert.Equal("", new SettingsService(root).GetSetting(SettingKeys.NexusOAuthTestClientId, "missing"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TestClientId_InvalidOrFailedSave_ShouldPreserveConfigurationAndPendingLogin()
    {
        using var http = new HttpClient();
        var registry = new PlatformRegistry();
        registry.RegisterPlatform(new NexusModsPlatformAdapter(http));
        var repository = new Mock<ISettingsRepository>();
        repository.Setup(r => r.IsLegacyMigrationComplete()).Returns(true);
        repository.Setup(r => r.ReadAll()).Returns(new Dictionary<string, string>());
        repository.Setup(r => r.ApplyAsync(It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<IReadOnlyCollection<string>>()))
            .ThrowsAsync(new IOException("synthetic save failure"));
        var root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-OAuthConfig-" + Guid.NewGuid().ToString("N"));
        var settings = new SettingsService(repository.Object, root);
        var pending = new TaskCompletionSource<NexusModsOAuthTokens>(TaskCreationOptions.RunContinuationsAsynchronously);
        var auth = new NexusModsAuthService(registry, oauthClient: new FakeOAuth { Pending = pending.Task },
            options: Options, settings: settings);
        var login = auth.LoginAsync(_ => { });
        await Assert.ThrowsAsync<ArgumentException>(() => auth.ApplyOAuthClientIdAsync("client\n"));
        await Assert.ThrowsAsync<IOException>(() => auth.ApplyOAuthClientIdAsync("another-client"));
        Assert.Equal(Options.ClientId, auth.OAuthClientId);
        Assert.Equal("", settings.GetSetting(SettingKeys.NexusOAuthTestClientId, ""));
        pending.SetResult(Tokens("one"));
        await login;
        Assert.True(auth.IsOAuthSignedIn);
    }

    [Fact]
    public async Task TestClientId_ChangeDuringLogin_ShouldRejectOldCallbackWithoutOverwritingPersonalKey()
    {
        using var http = new HttpClient();
        var pending = new TaskCompletionSource<NexusModsOAuthTokens>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oauth = new FakeOAuth { Pending = pending.Task };
        var store = new MemoryStore();
        var (auth, _) = Create(store, http, oauth);
        await auth.ApplyApiKeyAsync("synthetic-personal-key");
        var login = auth.LoginAsync(_ => { });
        await auth.ApplyOAuthClientIdAsync("another-client");
        Assert.Equal(Options.ClientId, oauth.LastClientId);
        pending.SetResult(Tokens("one"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        Assert.False(auth.IsOAuthSignedIn);
        Assert.True(auth.IsApiKeySaved);
        Assert.Equal("synthetic-personal-key", store.Value);
    }

    [Fact]
    public async Task OAuthCredentials_ShouldBeEncryptedRestoredWithoutNetworkAndDeletedByLogout()
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-OAuth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var handler = new ApiHandler();
        using var http = new HttpClient(handler);
        var oauth = new FakeOAuth();
        try
        {
            var path = Path.Combine(root, "test.db");
            using (var database = new HD2MimntDbContext(path))
            {
                await database.InitializeDatabaseAsync();
                var (auth, platform) = Create(new PlatformCredentialStore(database), http, oauth);
                await auth.ApplyApiKeyAsync("synthetic-previous-api-key");
                await auth.LoginAsync(_ => { });
                Assert.True(auth.IsOAuthSignedIn);
                Assert.False(auth.IsApiKeySaved);
                Assert.Equal("Tester", auth.UserName);
                await platform.HasDirectDownloadAccessAsync();
                Assert.Equal("Bearer synthetic-access-one", handler.Requests.Single().Authorization);
                Assert.Null(handler.Requests.Single().ApiKey);
                using var command = (await database.GetConnectionAsync()).CreateCommand();
                command.CommandText = "SELECT ProtectedApiKey FROM PlatformCredentials";
                var cipher = Assert.IsType<byte[]>(await command.ExecuteScalarAsync());
                Assert.DoesNotContain("synthetic-access-one", Encoding.UTF8.GetString(cipher));
                Assert.DoesNotContain("synthetic-refresh-one", Encoding.UTF8.GetString(cipher));
                command.CommandText = "SELECT COUNT(*) FROM PlatformCredentials";
                Assert.Equal(1L, await command.ExecuteScalarAsync());
            }
            Assert.DoesNotContain("synthetic-access-one", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
            var oldRequests = handler.Requests.Count;
            using (var database = new HD2MimntDbContext(path))
            {
                await database.InitializeDatabaseAsync();
                var store = new PlatformCredentialStore(database);
                var (auth, platform) = Create(store, http, oauth);
                await auth.RestoreApiKeyAsync();
                Assert.Equal(oldRequests, handler.Requests.Count);
                Assert.Equal(0, oauth.Refreshes);
                Assert.True(auth.IsOAuthSignedIn);
                await auth.ClearApiKeyAsync();
                Assert.True(auth.IsOAuthSignedIn); // 测试 Key 按钮不会清除 OAuth。
                await platform.HasDirectDownloadAccessAsync();
                Assert.Equal("Bearer synthetic-access-one", handler.Requests.Last().Authorization);
                await auth.LogoutAsync();
                Assert.False(auth.IsAuthenticated);
                Assert.Null(await store.ReadApiKeyAsync("nexusmods"));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExpiredToken_ConcurrentRequests_ShouldRefreshOncePersistRotationAndUseBearerForV1V2V3()
    {
        using var handler = new ApiHandler();
        using var http = new HttpClient(handler);
        var store = new MemoryStore();
        var oauth = new FakeOAuth { Initial = Tokens("one", -1) };
        var (auth, platform) = Create(store, http, oauth);
        await auth.LoginAsync(_ => { });
        await Task.WhenAll(platform.HasDirectDownloadAccessAsync(), platform.HasDirectDownloadAccessAsync(),
            platform.GetModInfoAsync("123"), platform.SearchBrowseAsync(new() { Query = "test" }));
        Assert.Equal(1, oauth.Refreshes);
        Assert.Contains("synthetic-refresh-two", store.Value);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("Bearer synthetic-access-two", request.Authorization);
            Assert.Null(request.ApiKey);
            Assert.Equal("HD2Mimnt", request.ApplicationName);
            Assert.False(string.IsNullOrWhiteSpace(request.ApplicationVersion));
        });
        await platform.SearchAsync(new() { Query = "" });
        Assert.Null(handler.Requests.Last().Authorization); // 公开热门请求不发送凭据。
        Assert.Null(http.DefaultRequestHeaders.Authorization);
        var (restored, restoredPlatform) = Create(store, http, oauth);
        await restored.RestoreApiKeyAsync();
        await restoredPlatform.HasDirectDownloadAccessAsync();
        Assert.Equal(1, oauth.Refreshes);
        Assert.True(restored.IsOAuthSignedIn);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshFailure_ShouldClearRevokedLoginButKeepTransientFailure(bool revoked)
    {
        using var handler = new ApiHandler();
        using var http = new HttpClient(handler);
        var store = new MemoryStore();
        var oauth = new FakeOAuth { Initial = Tokens("one", -1), Failure = new NexusModsOAuthException("synthetic failure", revoked) };
        var (auth, platform) = Create(store, http, oauth);
        await auth.LoginAsync(_ => { });
        await Assert.ThrowsAsync<NexusModsOAuthException>(() => platform.HasDirectDownloadAccessAsync());
        Assert.Empty(handler.Requests);
        Assert.Equal(!revoked, auth.IsAuthenticated);
        Assert.Equal(!revoked, auth.IsOAuthSignedIn);
        if (revoked) Assert.Null(store.Value);
        else Assert.NotNull(store.Value);
    }

    [Fact]
    public async Task Refresh_SaveFailure_ShouldRetryPersistenceWithoutReusingRotatedRefreshToken()
    {
        using var handler = new ApiHandler();
        using var http = new HttpClient(handler);
        var store = new MemoryStore();
        var oauth = new FakeOAuth { Initial = Tokens("one", -1) };
        var (auth, platform) = Create(store, http, oauth);
        await auth.LoginAsync(_ => { });
        store.FailNextSave = true;
        await Assert.ThrowsAsync<IOException>(() => platform.HasDirectDownloadAccessAsync());
        Assert.Empty(handler.Requests);
        Assert.Equal(1, oauth.Refreshes);
        await platform.HasDirectDownloadAccessAsync();
        Assert.Equal(1, oauth.Refreshes);
        Assert.Contains("synthetic-refresh-two", store.Value);
        Assert.Equal("Bearer synthetic-access-two", handler.Requests.Single().Authorization);
    }

    [Fact]
    public async Task CredentialSaveOrDeleteFailure_ShouldPreservePreviousLogin()
    {
        using var handler = new ApiHandler();
        using var http = new HttpClient(handler);
        var store = new Mock<IPlatformCredentialStore>();
        var oauth = new FakeOAuth();
        var (auth, platform) = Create(store.Object, http, oauth);
        await auth.ApplyApiKeyAsync("synthetic-old-key");
        store.Setup(s => s.SaveApiKeyAsync("nexusmods", It.Is<string>(value => value.StartsWith("hd2mimnt-oauth-v1:")), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("synthetic write failure"));
        await Assert.ThrowsAsync<IOException>(() => auth.LoginAsync(_ => { }));
        await platform.HasDirectDownloadAccessAsync();
        Assert.Equal("synthetic-old-key", handler.Requests.Last().ApiKey);
        store.Reset();
        await auth.LoginAsync(_ => { });
        store.Setup(s => s.DeleteApiKeyAsync("nexusmods", It.IsAny<CancellationToken>())).ThrowsAsync(new IOException());
        await Assert.ThrowsAsync<IOException>(() => auth.LogoutAsync());
        Assert.True(auth.IsOAuthSignedIn);
        await platform.HasDirectDownloadAccessAsync();
        Assert.Equal("Bearer synthetic-access-one", handler.Requests.Last().Authorization);
    }

    [Fact]
    public async Task Login_LogoutOrKeyChangeWhileBrowserPending_ShouldNotResurrectOldSession()
    {
        using var handler = new ApiHandler();
        using var http = new HttpClient(handler);
        var pending = new TaskCompletionSource<NexusModsOAuthTokens>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oauth = new FakeOAuth { Pending = pending.Task };
        var store = new MemoryStore();
        var (auth, platform) = Create(store, http, oauth);
        var login = auth.LoginAsync(_ => { });
        await auth.LogoutAsync();
        await auth.ApplyApiKeyAsync("synthetic-replacement-key");
        pending.SetResult(Tokens("one"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        Assert.False(auth.IsOAuthSignedIn);
        await platform.HasDirectDownloadAccessAsync();
        Assert.Equal("synthetic-replacement-key", handler.Requests.Single().ApiKey);
        Assert.Equal("synthetic-replacement-key", store.Value);
    }

    [Fact]
    public async Task RestoredCredentials_ChangedClientId_ShouldRejectWithoutSendingToken()
    {
        using var http = new HttpClient();
        var store = new MemoryStore();
        var oauth = new FakeOAuth();
        var (auth, _) = Create(store, http, oauth);
        await auth.LoginAsync(_ => { });
        var registry = new PlatformRegistry();
        registry.RegisterPlatform(new NexusModsPlatformAdapter(http));
        var changed = new NexusModsAuthService(registry, store, oauth, new() { ClientId = "another-client" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => changed.RestoreApiKeyAsync());
        Assert.False(changed.IsAuthenticated);
        Assert.Equal(0, oauth.Refreshes);
    }

    private static (NexusModsAuthService Auth, NexusModsPlatformAdapter Platform) Create(IPlatformCredentialStore store, HttpClient http, FakeOAuth oauth)
    {
        var registry = new PlatformRegistry();
        var platform = new NexusModsPlatformAdapter(http, applicationVersion: "0.1.0");
        registry.RegisterPlatform(platform);
        return (new(registry, store, oauth, Options), platform);
    }

    private static NexusModsOAuthTokens Tokens(string suffix, int minutes = 30) => new()
    {
        AccessToken = "synthetic-access-" + suffix, RefreshToken = "synthetic-refresh-" + suffix,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(minutes), UserName = "Tester", UserId = "123"
    };
    private sealed class FakeOAuth : INexusModsOAuthClient
    {
        public NexusModsOAuthTokens Initial { get; init; } = Tokens("one");
        public Task<NexusModsOAuthTokens>? Pending { get; init; }
        public NexusModsOAuthException? Failure { get; init; }
        public int Refreshes { get; private set; }
        public string? LastClientId { get; private set; }
        public Task<NexusModsOAuthTokens> LoginAsync(NexusModsOAuthOptions options, Action<Uri> openBrowser, CancellationToken cancellationToken = default)
        {
            LastClientId = options.ClientId;
            return Pending?.WaitAsync(cancellationToken) ?? Task.FromResult(Initial);
        }
        public async Task<NexusModsOAuthTokens> RefreshAsync(NexusModsOAuthOptions options, NexusModsOAuthTokens tokens, CancellationToken cancellationToken = default)
        {
            Refreshes++;
            await Task.Delay(25, cancellationToken);
            if (Failure != null) throw Failure;
            return Tokens("two");
        }
    }
    private sealed class MemoryStore : IPlatformCredentialStore
    {
        public string? Value { get; private set; }
        public bool FailNextSave { get; set; }
        public Task SaveApiKeyAsync(string platformId, string apiKey, CancellationToken cancellationToken = default)
        {
            if (FailNextSave) { FailNextSave = false; throw new IOException("synthetic save failure"); }
            Value = apiKey; return Task.CompletedTask;
        }
        public Task<string?> ReadApiKeyAsync(string platformId, CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task DeleteApiKeyAsync(string platformId, CancellationToken cancellationToken = default) { Value = null; return Task.CompletedTask; }
    }
    private sealed record SentRequest(string Path, string? Authorization, string? ApiKey, string? ApplicationName, string? ApplicationVersion);
    private sealed class ApiHandler : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(new(path, request.Headers.Authorization?.ToString(), Header("apikey"), Header("Application-Name"), Header("Application-Version")));
            string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
            var json = path switch
            {
                "/v1/users/validate.json" => """{"user_id":123,"is_premium":true}""",
                "/v3/games/helldivers2/mods/123" => """{"data":{"id":"global-mod","game_id":"553850","game_scoped_id":"123","name":"Test mod"}}""",
                "/v3/games/helldivers2/trending-mods" => """{"data":{"mods":[]}}""",
                "/v2/graphql" => """{"data":{"mods":{"nodes":[],"totalCount":0}}}""",
                _ => throw new InvalidOperationException(path)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
