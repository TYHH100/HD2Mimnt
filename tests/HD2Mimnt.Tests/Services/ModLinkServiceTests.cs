using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Platforms;
using HD2Mimnt.Infrastructure.Repositories;
using Moq;

namespace HD2Mimnt.Tests.Services;

public sealed class ModLinkServiceTests : IDisposable
{
    private const string Url = "https://www.nexusmods.com/helldivers2/mods/16633";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-Link-{Guid.NewGuid():N}");
    private readonly Mock<IModPlatform> _platform = new();
    private HD2MimntDbContext? _db;
    private ModRepository _repository = null!;
    private ModService _mods = null!;

    private async Task<ModLinkService> CreateAsync()
    {
        Directory.CreateDirectory(_root);
        _db = new HD2MimntDbContext(Path.Combine(_root, "mods.db"));
        await _db.InitializeDatabaseAsync();
        _repository = new ModRepository(_db);
        _mods = new ModService(_repository, Path.Combine(_root, "storage"));
        _platform.SetupGet(p => p.Id).Returns("nexusmods");
        _platform.Setup(p => p.GetModInfoAsync("16633", It.IsAny<CancellationToken>())).ReturnsAsync(new PlatformModInfo
        {
            Id = "16633", Name = "Remote mod", Metadata = new() { ["nexus_global_mod_id"] = "global-16633" }
        });
        _platform.Setup(p => p.GetVersionsAsync("16633", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PlatformModVersion> { new() { Version = "2.0" } });
        var registry = new PlatformRegistry();
        registry.RegisterPlatform(_platform.Object);
        return new(registry, _mods);
    }

    [Theory]
    [InlineData(Url)]
    [InlineData(" https://nexusmods.com/helldivers2/mods/016633/?tab=files#description ")]
    [InlineData("http://WWW.NEXUSMODS.COM/helldivers2/mods/16633")]
    public async Task Parse_ShouldExtractWebsiteIdAndCanonicalUrl(string input)
    {
        var service = await CreateAsync();
        var link = service.Parse(input);
        Assert.Equal(new ModPlatformLink("nexusmods", "helldivers2", "16633", Url), link);
        _platform.Verify(p => p.GetModInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("https://www.nexusmods.com.evil.test/helldivers2/mods/16633")]
    [InlineData("https://nexusmods.com@evil.test/helldivers2/mods/16633")]
    [InlineData("https://user@nexusmods.com/helldivers2/mods/16633")]
    [InlineData("https://www.nexusmods.com:444/helldivers2/mods/16633")]
    [InlineData("https://www.nexusmods.com/skyrim/mods/16633")]
    [InlineData("https://www.nexusmods.com/helldivers2/mods/0")]
    [InlineData("https://www.nexusmods.com/helldivers2/mods/16633/files/1")]
    [InlineData("https://www.nexusmods.com/helldivers2/mods/not-an-id")]
    [InlineData("https://ayakamod.com/mods/16633")]
    [InlineData("")]
    public async Task Fetch_InvalidLinks_ShouldRejectBeforeNetwork(string input)
    {
        var service = await CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => service.FetchAsync(input));
        _platform.Verify(p => p.GetModInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Save_ShouldPersistAndReloadBindingsForUpdatesAndDeleteThemWithMod()
    {
        var service = await CreateAsync();
        var id = Guid.NewGuid();
        var other = new Mod { Id = Guid.NewGuid(), Name = "Other", Path = "missing-other" };
        await _repository.AddAsync(new Mod
        {
            Id = id, Name = "Local name", Version = "1.0", Author = "Local author", Path = "missing-local",
            Hash = "original-hash", Tags = new() { "user-tag" }
        });
        await _repository.AddAsync(other);
        var link = await service.FetchAsync(Url + "?tab=files");
        Assert.Equal("Remote mod", link.Name);
        Assert.Equal("global-16633", link.GlobalModId);
        Assert.Equal("2.0", link.LatestVersion);
        await service.SaveAsync(id, link);
        _db!.Dispose();
        _db = null;
        service = await CreateAsync();
        var saved = (await _repository.GetByIdAsync(id))!;
        Assert.Equal(link, saved.PlatformLink);
        Assert.Equal("nexusmods", saved.PlatformId);
        Assert.Equal("16633", saved.PlatformModId);
        Assert.Equal(16633, saved.NexusModId);
        Assert.Equal("2.0", saved.PlatformVersion);
        Assert.Equal("Local name", saved.Name);
        Assert.Equal("Local author", saved.Author);
        Assert.Equal("1.0", saved.Version);
        Assert.Equal("original-hash", saved.Hash);
        Assert.Equal(new[] { "user-tag" }, saved.Tags);
        var registry = new PlatformRegistry();
        registry.RegisterPlatform(_platform.Object);
        var update = await new UpdateCheckerService(registry, _repository).CheckUpdateAsync(id);
        Assert.NotNull(update);
        Assert.True(update.HasUpdate);
        Assert.Equal("16633", update.PlatformModId);
        Assert.NotNull((await _repository.GetByIdAsync(id))!.LastUpdateCheck);
        Assert.Equal(link, (await _repository.GetByIdAsync(id))!.PlatformLink);
        _platform.Setup(p => p.DownloadModAsync("16633", It.IsAny<IProgress<DownloadProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[] { 1, 2, 3 }));
        var downloads = new DownloadManagerService(registry);
        var destination = Path.Combine(_root, "download.zip");
        var downloadId = downloads.EnqueueDownload(saved.PlatformId!, saved.PlatformModId!, destination);
        await downloads.WaitForDownloadAsync(downloadId);
        Assert.Equal(DownloadStatus.Completed, downloads.GetDownloadTask(downloadId)!.Status);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(destination));
        _platform.Verify(p => p.DownloadModAsync("16633", It.IsAny<IProgress<DownloadProgress>?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null((await _repository.GetByIdAsync(other.Id))!.PlatformLink);
        Assert.True(await _mods.DeleteModAsync(id));
        Assert.Null(await _repository.GetByIdAsync(id));
        Assert.Single(await _repository.GetAllAsync());
    }

    [Fact]
    public async Task Fetch_FailureOrCancellation_ShouldNotReplaceSavedLink()
    {
        var service = await CreateAsync();
        var id = Guid.NewGuid();
        await _repository.AddAsync(new Mod { Id = id, Name = "Test", Path = "missing" });
        var original = await service.FetchAsync(Url);
        await service.SaveAsync(id, original);
        _platform.Setup(p => p.GetVersionsAsync("16633", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("failed"));
        await Assert.ThrowsAsync<HttpRequestException>(() => service.FetchAsync(Url));
        Assert.Equal(original, (await _repository.GetByIdAsync(id))!.PlatformLink);
        _platform.Setup(p => p.GetModInfoAsync("16633", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FetchAsync(Url));
        Assert.Equal(original, (await _repository.GetByIdAsync(id))!.PlatformLink);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SaveAsync(Guid.NewGuid(), original));
    }

    [Fact]
    public async Task Initialize_OldDatabase_ShouldAddPlatformColumnsWithoutChangingExistingMods()
    {
        await CreateAsync();
        var original = new Mod { Id = Guid.NewGuid(), Name = "Existing", Path = "missing", Version = "old", Tags = new() { "keep" } };
        await _repository.AddAsync(original);
        var connection = await _db!.GetConnectionAsync();
        foreach (var column in new[] { "PlatformId", "PlatformModId", "PlatformVersion", "LastUpdateCheck", "PlatformLinkJson" })
        {
            using var drop = connection.CreateCommand();
            drop.CommandText = $"ALTER TABLE Mods DROP COLUMN {column}";
            await drop.ExecuteNonQueryAsync();
        }
        await _db.InitializeDatabaseAsync();
        await _db.InitializeDatabaseAsync();
        var saved = (await _repository.GetByIdAsync(original.Id))!;
        Assert.Equal(original.Name, saved.Name);
        Assert.Equal(original.Version, saved.Version);
        Assert.Equal(original.Tags, saved.Tags);
        Assert.Null(saved.PlatformLink);
        Assert.Null(saved.PlatformModId);
    }

    [Fact]
    public async Task Fetch_WithoutApiKey_ShouldGiveActionableError()
    {
        await CreateAsync();
        using var client = new HttpClient();
        var registry = new PlatformRegistry();
        registry.RegisterPlatform(new NexusModsPlatformAdapter(client));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new ModLinkService(registry, _mods).FetchAsync(Url));
        Assert.Contains("开发工具", ex.Message);
    }

    public void Dispose()
    {
        _db?.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
