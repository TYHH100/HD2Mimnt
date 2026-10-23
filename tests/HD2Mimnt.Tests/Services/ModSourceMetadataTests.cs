using System.Text.Json;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Platforms;
using HD2Mimnt.Infrastructure.Repositories;
using HD2Mimnt.Infrastructure.Storage;

namespace HD2Mimnt.Tests.Services;

public sealed class ModSourceMetadataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-Metadata-" + Guid.NewGuid().ToString("N"));
    private HD2MimntDbContext? _database;
    private string Library => Path.Combine(_root, "library");
    private static readonly ModPlatformLink Source = new("ayakamods", "helldivers2", "5089", "https://ayakamods.com/mods/5089/",
        Name: "Health Regeneration", InstalledFileVersionId: "ayaka-file:8442:39632", InstalledWebsiteFileId: "39632",
        InstalledVersion: "2026-10-15", Author: "Argxs", AuthorUrl: "https://ayakamods.com/members/argxs.408385/");

    private async Task<(ModService Mods, ModRepository Repository)> CreateAsync()
    {
        Directory.CreateDirectory(_root);
        _database = new(Path.Combine(_root, "test.db"));
        await _database.InitializeDatabaseAsync();
        var repository = new ModRepository(_database);
        return (new ModService(repository, Library), repository);
    }

    [Fact]
    public async Task Import_ShouldRecordSourceSeparatelyAndNormalizeOnlyLibraryManifest()
    {
        var (mods, repository) = await CreateAsync();
        var directory = Path.Combine(_root, "source");
        Directory.CreateDirectory(directory);
        var patch = new byte[] { 1, 3, 5, 7 };
        await File.WriteAllBytesAsync(Path.Combine(directory, "0123456789abcdef.patch_0"), patch);
        const string manifest = """{"name":"Original","version":"1.0","author":"Archive author"}""";
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), manifest);
        var sourceHash = await ModHashService.CalculateAsync(directory);
        var id = await mods.ImportModAsync(directory, sourceLink: Source);
        var saved = (await mods.GetModByIdAsync(id))!;
        Assert.Equal(Source, saved.PlatformLink);
        Assert.Equal(patch, await File.ReadAllBytesAsync(Path.Combine(saved.Path, "0123456789abcdef.patch_0")));
        Assert.Equal(manifest, await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json")));
        using var standard = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(saved.Path, "manifest.json")));
        Assert.False(standard.RootElement.TryGetProperty("author", out _));
        Assert.Equal("Archive author", new HD2Mimnt.Core.Services.ManifestParser().ParseFromFile(Path.Combine(saved.Path, "manifest.json"))!.Author);
        Assert.Equal(saved.Hash, await ModHashService.CalculateAsync(saved.Path));
        Assert.Equal(sourceHash, await ModHashService.CalculateAsync(directory));
        var metadata = Assert.Single(Directory.GetFiles(Path.Combine(Library, ".hd2mimnt-metadata"), "*.json"));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(metadata));
        Assert.Equal(1, document.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(id, document.RootElement.GetProperty("ModId").GetGuid());
        Assert.Equal(Source.InstalledVersion, document.RootElement.GetProperty("Source").GetProperty("InstalledVersion").GetString());
        Assert.DoesNotContain("download_url", await File.ReadAllTextAsync(metadata));
        Assert.Equal(Source, (await repository.GetByIdAsync(id))!.PlatformLink);
        _database!.Dispose();
        var (reopened, _) = await CreateAsync();
        Assert.Equal(Source, (await reopened.GetModByIdAsync(id))!.PlatformLink);
        await reopened.DeleteModAsync(id);
        Assert.False(File.Exists(metadata));
    }

    [Fact]
    public async Task CheckAndRebind_ShouldPreserveInstalledVersionAndAuthorHomepage()
    {
        var (mods, repository) = await CreateAsync();
        var mod = new Mod { Id = Guid.NewGuid(), Name = "Local", Path = "missing", Version = "1.0" };
        await repository.AddAsync(mod);
        await mods.SetPlatformLinkAsync(mod.Id, Source);
        await mods.RecordUpdateCheckAsync(mod.Id, Source, "future-version");
        var saved = await mods.SetPlatformLinkAsync(mod.Id, Source with
        { LatestVersion = "future-version", InstalledVersion = null, InstalledFileVersionId = null, InstalledWebsiteFileId = null, AuthorUrl = null });
        Assert.Equal(Source.InstalledVersion, saved.PlatformLink!.InstalledVersion);
        Assert.Equal(Source.InstalledFileVersionId, saved.PlatformLink.InstalledFileVersionId);
        Assert.Equal(Source.AuthorUrl, saved.PlatformLink.AuthorUrl);
        Assert.Equal("future-version", saved.PlatformLink.LatestVersion);
        Assert.Equal("1.0", saved.Version);
        var json = await new ModMetadataStore(Library).ReadAsync(mod.Id);
        Assert.Equal(saved.PlatformLink, json);
    }

    [Fact]
    public async Task AuthorHomepage_ShouldSaveWithoutPlatformOrNetworkAndKeepExistingApiBinding()
    {
        var (mods, repository) = await CreateAsync();
        var mod = new Mod { Id = Guid.NewGuid(), Name = "Local", Path = "missing" };
        await repository.AddAsync(mod);
        var links = new ModLinkService(new PlatformRegistry(), mods);
        var saved = await links.SaveAuthorPageAsync(mod.Id, "https://afdian.com/a/example");
        Assert.False(saved.PlatformLink!.SupportsUpdates);
        Assert.Equal("https://afdian.com/a/example", saved.PlatformLink.AuthorUrl);
        Assert.Null(saved.PlatformId);
        await mods.SetPlatformLinkAsync(mod.Id, Source);
        saved = await links.SaveAuthorPageAsync(mod.Id, "https://afdian.com/a/another");
        Assert.True(saved.PlatformLink!.SupportsUpdates);
        Assert.Equal(Source.Url, saved.PlatformLink.Url);
        Assert.Equal(Source.InstalledFileVersionId, saved.PlatformLink.InstalledFileVersionId);
        Assert.Equal("https://afdian.com/a/another", saved.PlatformLink.AuthorUrl);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows")]
    [InlineData("https://secret@afdian.com/a/example")]
    public async Task AuthorHomepage_ShouldRejectNonWebsiteLinks(string url)
    {
        var (mods, _) = await CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => new ModLinkService(new PlatformRegistry(), mods)
            .SaveAuthorPageAsync(Guid.NewGuid(), url));
        Assert.False(Directory.Exists(Path.Combine(Library, ".hd2mimnt-metadata")));
    }

    [Fact]
    public async Task BrokenJson_ShouldFallBackToCompatibleDatabaseRecord()
    {
        var (mods, repository) = await CreateAsync();
        var mod = new Mod { Id = Guid.NewGuid(), Name = "Local", Path = "missing", PlatformLink = Source };
        await repository.AddAsync(mod);
        var directory = Path.Combine(Library, ".hd2mimnt-metadata");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, mod.Id.ToString("N") + ".json"), "broken-json");
        Assert.Equal(Source, (await mods.GetModByIdAsync(mod.Id))!.PlatformLink);
    }

    [Fact]
    public async Task FailedDatabaseWrite_ShouldRestorePreviousJson()
    {
        var (mods, repository) = await CreateAsync();
        var mod = new Mod { Id = Guid.NewGuid(), Name = "Local", Path = "missing" };
        await repository.AddAsync(mod);
        await mods.SetPlatformLinkAsync(mod.Id, Source);
        using var ownedConnection = await _database!.OpenConnectionAsync();
        using var command = ownedConnection.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_metadata BEFORE UPDATE ON Mods BEGIN SELECT RAISE(FAIL, 'test failure'); END;";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => mods.RecordUpdateCheckAsync(mod.Id, Source, "next"));
        Assert.Equal(Source, await new ModMetadataStore(Library).ReadAsync(mod.Id));
        Assert.Equal(Source, (await repository.GetByIdAsync(mod.Id))!.PlatformLink);
        Assert.Empty(Directory.GetFiles(Path.Combine(Library, ".hd2mimnt-metadata"), "*.tmp"));
    }

    public void Dispose()
    {
        _database?.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
