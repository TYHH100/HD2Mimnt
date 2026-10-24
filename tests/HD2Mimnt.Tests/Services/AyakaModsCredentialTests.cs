using System.Text;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Platforms;
using HD2Mimnt.Infrastructure.Platforms.AyakaMods;
using HD2Mimnt.Infrastructure.Repositories;
using Moq;

namespace HD2Mimnt.Tests.Services;

public sealed class AyakaModsCredentialTests
{
    [Fact]
    public async Task Key_ShouldRemainSeparateFromNexusEncryptedRestoredAndExplicitlyCleared()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-AyakaCredential-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "test.db");
        using var client = new HttpClient();
        using var platform = new AyakaModsPlatformAdapter(client);
        var registry = new PlatformRegistry(); registry.RegisterPlatform(platform);
        try
        {
            using (var database = new HD2MimntDbContext(path))
            {
                await database.InitializeDatabaseAsync();
                var store = new PlatformCredentialStore(database);
                await store.SaveApiKeyAsync("nexusmods", "separate-nexus-test-key");
                var auth = new AyakaModsAuthService(registry, store);
                await auth.ApplyApiKeyAsync(" synthetic-ayaka-secret ");
                Assert.True(auth.IsAuthenticated && auth.IsApiKeySaved);
                Assert.Equal("synthetic-ayaka-secret", await store.ReadApiKeyAsync("ayakamods"));
                Assert.Equal("separate-nexus-test-key", await store.ReadApiKeyAsync("nexusmods"));
            }
            Assert.DoesNotContain("synthetic-ayaka-secret", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
            platform.ClearApiKey();
            using var reopened = new HD2MimntDbContext(path);
            await reopened.InitializeDatabaseAsync();
            var finalStore = new PlatformCredentialStore(reopened);
            var restored = new AyakaModsAuthService(registry, finalStore);
            await restored.RestoreApiKeyAsync();
            Assert.True(restored.IsAuthenticated && restored.IsApiKeySaved);
            await restored.ClearApiKeyAsync();
            Assert.False(restored.IsAuthenticated);
            Assert.Null(await finalStore.ReadApiKeyAsync("ayakamods"));
            Assert.Equal("separate-nexus-test-key", await finalStore.ReadApiKeyAsync("nexusmods"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FailedSaveOrDelete_ShouldPreserveActiveKeyAndFailedRestoreShouldClearStaleState()
    {
        using var client = new HttpClient();
        using var platform = new AyakaModsPlatformAdapter(client);
        var registry = new PlatformRegistry(); registry.RegisterPlatform(platform);
        var store = new Mock<IPlatformCredentialStore>();
        var auth = new AyakaModsAuthService(registry, store.Object);
        await auth.ApplyApiKeyAsync("old-key");
        store.Setup(s => s.SaveApiKeyAsync("ayakamods", "new-key", It.IsAny<CancellationToken>())).ThrowsAsync(new IOException());
        store.Setup(s => s.DeleteApiKeyAsync("ayakamods", It.IsAny<CancellationToken>())).ThrowsAsync(new IOException());
        await Assert.ThrowsAsync<IOException>(() => auth.ApplyApiKeyAsync("new-key"));
        await Assert.ThrowsAsync<IOException>(() => auth.ClearApiKeyAsync());
        Assert.True(auth.IsAuthenticated && auth.IsApiKeySaved);
        store.Setup(s => s.ReadApiKeyAsync("ayakamods", It.IsAny<CancellationToken>())).ThrowsAsync(new System.Security.Cryptography.CryptographicException());
        await Assert.ThrowsAsync<System.Security.Cryptography.CryptographicException>(() => auth.RestoreApiKeyAsync());
        Assert.False(auth.IsAuthenticated);
        Assert.False(auth.IsApiKeySaved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\nheader-injection")]
    [InlineData("key\r\n")]
    public async Task InvalidKey_ShouldNeverReachPersistence(string key)
    {
        using var client = new HttpClient();
        using var platform = new AyakaModsPlatformAdapter(client);
        var registry = new PlatformRegistry(); registry.RegisterPlatform(platform);
        var store = new Mock<IPlatformCredentialStore>();
        var auth = new AyakaModsAuthService(registry, store.Object);
        await Assert.ThrowsAsync<ArgumentException>(() => auth.ApplyApiKeyAsync(key));
        store.VerifyNoOtherCalls();
    }
}
