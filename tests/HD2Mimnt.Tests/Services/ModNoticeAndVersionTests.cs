using System.IO.Compression;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Services;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Tests.Services;

public sealed class ModNoticeAndVersionTests : IDisposable
{
    private const string Patch = "0123456789abcdef.patch_0";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hd2mimnt-notice-" + Guid.NewGuid().ToString("N"));
    private readonly HD2MimntDbContext _db;
    private readonly ModRepository _repository;
    private readonly SettingsService _settings;
    private readonly ModService _mods;
    private string Library => Path.Combine(_root, "library");

    public ModNoticeAndVersionTests()
    {
        Directory.CreateDirectory(_root);
        _db = new(Path.Combine(_root, "test.db"));
        _db.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _repository = new(_db);
        _settings = new(Path.Combine(_root, "settings"));
        _mods = new(_repository, Library, settingsService: _settings, tempDirectory: Path.Combine(_root, "temp"));
    }

    [Theory]
    [InlineData("1.0.0", "1.1.0")]
    [InlineData("1", "1.1.0")]
    [InlineData("1.2.3", "1.3.0")]
    [InlineData("v2.5", "2.6.0")]
    [InlineData("2.0.0-beta.1", "2.1.0")]
    [InlineData("", ModVersionNumber.Default)]
    [InlineData("unknown", ModVersionNumber.Default)]
    public void BumpMinor_UsesNumericMinorComponent(string current, string expected)
        => Assert.Equal(expected, ModVersionNumber.BumpMinor(current));

    [Fact]
    public async Task Import_WithoutManifestVersion_StartsAtDefaultVersion()
    {
        var path = Path.Combine(_root, "no-version.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
                writer.Write("{\"Name\":\"No version\",\"Author\":\"Author\"}");
            using (var writer = new StreamWriter(zip.CreateEntry(Patch).Open())) writer.Write("content");
        }

        var id = await _mods.ImportModAsync(path);

        var imported = (await _repository.GetByIdAsync(id))!;
        Assert.Equal(ModVersionNumber.Default, imported.Version);
        Assert.Equal(ModNoticeKind.New, imported.Notice);
    }

    [Fact]
    public async Task ImportAndLocalArchiveUpdate_SetNoticeAndIncrementMinorVersion()
    {
        var id = await _mods.ImportModAsync(CreateArchive("1"));
        Assert.Equal(ModNoticeKind.New, (await _repository.GetByIdAsync(id))!.Notice);
        Assert.Equal("1", (await _repository.GetByIdAsync(id))!.Version);

        await _mods.UpdateFromLocalArchiveAsync(id, CreateArchive("2"));

        var updated = (await _repository.GetByIdAsync(id))!;
        Assert.Equal(ModNoticeKind.Updated, updated.Notice);
        Assert.Equal("1.1.0", updated.Version);
    }

    [Fact]
    public async Task DismissModNotice_ClearsPersistedNotice()
    {
        var id = await _mods.ImportModAsync(CreateArchive("1"));
        await _mods.DismissModNoticeAsync(id);

        Assert.Equal(ModNoticeKind.None, (await _repository.GetByIdAsync(id))!.Notice);
    }

    [Fact]
    public async Task PlatformUpdate_KeepsWebsiteVersionAndMarksUpdated()
    {
        var id = await _mods.ImportModAsync(CreateArchive("1"));
        var link = new ModPlatformLink("nexusmods", "helldivers2", "42", "https://www.nexusmods.com/helldivers2/mods/42")
            { InstalledVersion = "1" };
        await _mods.SetPlatformLinkAsync(id, link);

        await _mods.ReplaceFromArchiveAsync(id, CreateArchive("9"),
            link, new PlatformModFile { Id = "file-9", Version = "2.5.0" });

        var updated = (await _repository.GetByIdAsync(id))!;
        Assert.Equal("2.5.0", updated.Version);
        Assert.Equal(ModNoticeKind.Updated, updated.Notice);
    }

    private string CreateArchive(string version)
    {
        var path = Path.Combine(_root, "version-" + version + "-" + Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
            writer.Write("{\"Name\":\"Notice mod\",\"Version\":\"" + version + "\",\"Author\":\"Author\"}");
        using (var writer = new StreamWriter(zip.CreateEntry(Patch).Open())) writer.Write("content-" + version);
        return path;
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
