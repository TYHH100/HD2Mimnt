using System.Security.Cryptography;
using System.Text;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Platforms;
using HD2Mimnt.Infrastructure.Repositories;
using Moq;

namespace HD2Mimnt.Tests.Services;

public sealed class NexusModsCredentialTests
{
    private static NexusModsTestService CreateService(IPlatformCredentialStore store, HttpClient client)
    {
        var registry = new PlatformRegistry();
        registry.RegisterPlatform(new NexusModsPlatformAdapter(client));
        return new(registry, store);
    }

    [Fact]
    public async Task Credentials_ShouldBeEncryptedRestoredAfterDatabaseReopenAndDeletedByClear()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-Credentials-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "test.db");
        const string key = "synthetic-long-nexus-key-for-encrypted-persistence";
        using var client = new HttpClient();
        try
        {
            using (var db = new HD2MimntDbContext(path))
            {
                await db.InitializeDatabaseAsync();
                var store = new PlatformCredentialStore(db);
                var auth = CreateService(store, client);
                await auth.ApplyApiKeyAsync("first-test-key");
                await auth.ApplyApiKeyAsync("  " + key + "  ");
                Assert.True(auth.IsApiKeySaved);
                using var connection = await db.OpenConnectionAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT ProtectedApiKey FROM PlatformCredentials";
                var ciphertext = Assert.IsType<byte[]>(await command.ExecuteScalarAsync());
                Assert.DoesNotContain(key, Encoding.UTF8.GetString(ciphertext));
                command.CommandText = "SELECT COUNT(*) FROM PlatformCredentials";
                Assert.Equal(1L, await command.ExecuteScalarAsync());
                Assert.Equal(key, await store.ReadApiKeyAsync("nexusmods"));
            }
            Assert.DoesNotContain(key, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
            using (var reopened = new HD2MimntDbContext(path))
            {
                await reopened.InitializeDatabaseAsync();
                var auth = CreateService(new PlatformCredentialStore(reopened), client);
                Assert.False(auth.IsAuthenticated);
                await auth.RestoreApiKeyAsync();
                Assert.True(auth.IsAuthenticated);
                Assert.True(auth.IsApiKeySaved);
                await auth.ClearApiKeyAsync();
                Assert.False(auth.IsAuthenticated);
                Assert.False(auth.IsApiKeySaved);
            }
            using var last = new HD2MimntDbContext(path);
            await last.InitializeDatabaseAsync();
            var finalAuth = CreateService(new PlatformCredentialStore(last), client);
            await finalAuth.RestoreApiKeyAsync();
            Assert.False(finalAuth.IsAuthenticated);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CorruptedCredential_ShouldRejectRestoreAndAllowExplicitReplacement()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-Credentials-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var client = new HttpClient();
        try
        {
            using var db = new HD2MimntDbContext(Path.Combine(root, "test.db"));
            await db.InitializeDatabaseAsync();
            var store = new PlatformCredentialStore(db);
            var auth = CreateService(store, client);
            await auth.ApplyApiKeyAsync("synthetic-key");
            using var connection = await db.OpenConnectionAsync();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE PlatformCredentials SET ProtectedApiKey = X'010203'";
                await command.ExecuteNonQueryAsync();
            }
            await Assert.ThrowsAsync<CryptographicException>(() => auth.RestoreApiKeyAsync());
            Assert.False(auth.IsAuthenticated);
            Assert.False(auth.IsApiKeySaved);
            await auth.ApplyApiKeyAsync("replacement-key");
            await auth.RestoreApiKeyAsync();
            Assert.True(auth.IsAuthenticated);
            Assert.Equal("replacement-key", await store.ReadApiKeyAsync("nexusmods"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PersistenceFailures_ShouldKeepPreviouslyAppliedKeyAndSavedState()
    {
        var store = new Mock<IPlatformCredentialStore>();
        using var handler = new CredentialHandler();
        using var client = new HttpClient(handler);
        var registry = new PlatformRegistry();
        var platform = new NexusModsPlatformAdapter(client);
        registry.RegisterPlatform(platform);
        var auth = new NexusModsTestService(registry, store.Object);
        await auth.ApplyApiKeyAsync("old-test-key");
        store.Setup(s => s.SaveApiKeyAsync("nexusmods", "new-test-key", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Synthetic write failure"));
        store.Setup(s => s.DeleteApiKeyAsync("nexusmods", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Synthetic deletion failure"));
        await Assert.ThrowsAsync<IOException>(() => auth.ApplyApiKeyAsync("new-test-key"));
        await Assert.ThrowsAsync<IOException>(() => auth.ClearApiKeyAsync());
        Assert.True(auth.IsApiKeySaved);
        await platform.GetModInfoAsync("123");
        Assert.Equal("old-test-key", handler.AppliedKey);
    }

    private sealed class CredentialHandler : HttpMessageHandler
    {
        public string? AppliedKey { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AppliedKey = request.Headers.GetValues("apikey").Single();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":{"id":"test-global-id","game_id":"553850","game_scoped_id":"123","name":"Test mod"}}""")
            });
        }
    }
}
