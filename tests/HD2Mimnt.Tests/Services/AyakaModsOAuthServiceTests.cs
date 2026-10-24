using System.Text;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Tests.Services;

public sealed class AyakaModsOAuthServiceTests
{
    [Fact]
    public async Task Login_ShouldEncryptTokensRestoreLocallyAndLogoutWithoutRemovingToolKeys()
    {
        using var scope = new Scope();
        await scope.Database.InitializeDatabaseAsync();
        var store = new PlatformCredentialStore(scope.Database);
        await store.SaveApiKeyAsync("ayakamods", "ayaka-tool-key");
        await store.SaveApiKeyAsync("nexusmods", "nexus-tool-key");
        var service = scope.Create(store);
        await service.ApplyClientIdAsync("registered-test-client");
        await service.LoginAsync(_ => { });
        Assert.True(service.IsSignedIn);
        using var ownedConnection = await scope.Database.OpenConnectionAsync();
        using (var command = ownedConnection.CreateCommand())
        {
            command.CommandText = "SELECT ProtectedApiKey FROM PlatformCredentials WHERE PlatformId = 'ayakamods-oauth'";
            var encrypted = Encoding.UTF8.GetString(Assert.IsType<byte[]>(await command.ExecuteScalarAsync()));
            Assert.DoesNotContain("synthetic-access", encrypted);
            Assert.DoesNotContain("synthetic-refresh", encrypted);
        }
        var restored = scope.Create(store);
        await restored.RestoreAsync();
        Assert.True(restored.IsSignedIn);
        Assert.Equal(0, scope.Client.Refreshes); // Startup performs no network work.
        Assert.Equal("synthetic-access", await restored.GetAccessTokenAsync());
        await restored.LogoutAsync();
        Assert.Null(await store.ReadApiKeyAsync("ayakamods-oauth"));
        Assert.Equal("ayaka-tool-key", await store.ReadApiKeyAsync("ayakamods"));
        Assert.Equal("nexus-tool-key", await store.ReadApiKeyAsync("nexusmods"));
        Assert.Equal("registered-test-client", new SettingsService(scope.Root).GetSetting("Ayaka.OAuth.ClientId", ""));
    }

    [Fact]
    public async Task Refresh_SaveFailure_ShouldRetryPersistingRotatedTokensWithoutRefreshingOldTokenAgain()
    {
        using var scope = new Scope();
        var store = new MemoryStore();
        var service = scope.Create(store);
        scope.Client.Result = scope.Client.Result with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        await service.LoginAsync(_ => { });
        store.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => service.GetAccessTokenAsync());
        Assert.True(service.IsSignedIn);
        Assert.Equal(1, scope.Client.Refreshes);
        store.FailSave = false;
        Assert.Equal("rotated-access", await service.GetAccessTokenAsync());
        Assert.Equal(1, scope.Client.Refreshes);
        var restored = scope.Create(store);
        await restored.RestoreAsync();
        Assert.Equal("rotated-access", await restored.GetAccessTokenAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_Failure_ShouldRetainTransientLoginAndRemoveRevokedLogin(bool revoked)
    {
        using var scope = new Scope();
        var store = new MemoryStore();
        var service = scope.Create(store);
        scope.Client.Result = scope.Client.Result with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        await service.LoginAsync(_ => { });
        scope.Client.RefreshError = new AyakaModsOAuthException(revoked ? "HTTP 400" : "HTTP 429", revoked);
        await Assert.ThrowsAsync<AyakaModsOAuthException>(() => service.GetAccessTokenAsync());
        Assert.Equal(!revoked, service.IsSignedIn);
        Assert.Equal(!revoked, store.Values.ContainsKey("ayakamods-oauth"));
    }

    [Fact]
    public async Task FailedSaveOrLogout_ShouldPreserveConsistentState()
    {
        using var scope = new Scope();
        var store = new MemoryStore { FailSave = true };
        var service = scope.Create(store);
        await Assert.ThrowsAsync<IOException>(() => service.LoginAsync(_ => { }));
        Assert.False(service.IsSignedIn);
        store.FailSave = false;
        await service.LoginAsync(_ => { });
        store.FailDelete = true;
        await Assert.ThrowsAsync<IOException>(() => service.LogoutAsync());
        Assert.True(service.IsSignedIn);
    }

    [Fact]
    public async Task Restore_ShouldRejectMismatchedRegistrationAndClearStaleMemory()
    {
        using var scope = new Scope();
        var store = new MemoryStore();
        var service = scope.Create(store);
        await service.LoginAsync(_ => { });
        await scope.Settings.SetSettingAsync("Ayaka.OAuth.ClientId", "different-client");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync());
        Assert.False(service.IsSignedIn);
        Assert.Equal(0, scope.Client.Refreshes);
    }

    [Fact]
    public async Task LogoutDuringLogin_ShouldPreventLateCallbackFromRestoringSession()
    {
        using var scope = new Scope();
        var store = new MemoryStore();
        var service = scope.Create(store);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.Client.Wait = release.Task;
        var login = service.LoginAsync(_ => { });
        await service.LogoutAsync();
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        Assert.False(service.IsSignedIn);
        Assert.Empty(store.Values);
    }

    private sealed class Scope : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "HD2Mimnt-AyakaOAuth-" + Guid.NewGuid().ToString("N"));
        public string DatabasePath => Path.Combine(Root, "hd2mimnt.db");
        public HD2MimntDbContext Database { get; }
        public SettingsService Settings { get; }
        public Client Client { get; } = new();
        public Scope()
        {
            Directory.CreateDirectory(Root);
            Database = new(DatabasePath);
            Settings = new(Root);
        }
        public AyakaModsOAuthService Create(IPlatformCredentialStore store) => new(Client, store, Settings,
            new() { ClientId = "registered-test-client" });
        public void Dispose() { Database.Dispose(); Directory.Delete(Root, true); }
    }
    private sealed class MemoryStore : IPlatformCredentialStore
    {
        public Dictionary<string, string> Values { get; } = new();
        public bool FailSave { get; set; }
        public bool FailDelete { get; set; }
        public Task SaveApiKeyAsync(string id, string value, CancellationToken token = default)
        { if (FailSave) throw new IOException(); Values[id] = value; return Task.CompletedTask; }
        public Task<string?> ReadApiKeyAsync(string id, CancellationToken token = default) => Task.FromResult(Values.GetValueOrDefault(id));
        public Task DeleteApiKeyAsync(string id, CancellationToken token = default)
        { if (FailDelete) throw new IOException(); Values.Remove(id); return Task.CompletedTask; }
    }
    private sealed class Client : IAyakaModsOAuthClient
    {
        public AyakaModsOAuthTokens Result { get; set; } = new() { AccessToken = "synthetic-access", RefreshToken = "synthetic-refresh", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        public Task Wait { get; set; } = Task.CompletedTask;
        public Exception? RefreshError { get; set; }
        public int Refreshes { get; private set; }
        public async Task<AyakaModsOAuthTokens> LoginAsync(AyakaModsOAuthOptions options, Action<Uri> open, CancellationToken token = default)
        { options.Validate(); await Wait.WaitAsync(token); return Result; }
        public Task<AyakaModsOAuthTokens> RefreshAsync(AyakaModsOAuthOptions options, AyakaModsOAuthTokens old, CancellationToken token = default)
        {
            Refreshes++;
            if (RefreshError != null) throw RefreshError;
            return Task.FromResult(Result with { AccessToken = "rotated-access", RefreshToken = "rotated-refresh", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        }
    }
}
