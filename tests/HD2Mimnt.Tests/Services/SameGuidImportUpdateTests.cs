using System.IO.Compression;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Tests.Services;

public sealed class SameGuidImportUpdateTests : IDisposable
{
    private const string Patch = "0123456789abcdef.patch_0";
    private static readonly Guid ModGuid = new("1d9f8c4a-3b2e-4f60-9a7d-5c1e2b8f4a70");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hd2mimnt-same-guid-" + Guid.NewGuid().ToString("N"));
    private readonly HD2MimntDbContext _db;
    private readonly ModRepository _repository;
    private readonly SettingsService _settings;
    private readonly ModService _mods;
    private string Library => Path.Combine(_root, "library");

    public SameGuidImportUpdateTests()
    {
        Directory.CreateDirectory(_root);
        _db = new(Path.Combine(_root, "test.db"));
        _db.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _repository = new(_db);
        _settings = new(Path.Combine(_root, "settings"));
        _mods = new(_repository, Library, settingsService: _settings, tempDirectory: Path.Combine(_root, "temp"));
    }

    [Fact]
    public async Task SameGuidImport_UpdatesExistingModInsteadOfAddingCopy()
    {
        var first = await _mods.ImportModAsync(CreateArchive("1"));
        var original = (await _mods.GetModByIdAsync(first))!;

        var second = await _mods.ImportModAsync(CreateArchive("2"));

        Assert.Equal(first, second);
        var updated = (await _mods.GetModByIdAsync(first))!;
        Assert.Equal("2", updated.Version);
        Assert.Equal("content-2", await File.ReadAllTextAsync(Path.Combine(updated.Path, Patch)));
        Assert.False(Directory.Exists(original.Path));
        Assert.Single(await _repository.GetAllAsync());
    }

    [Fact]
    public async Task SameGuidImport_IdenticalContent_IsNoOpInsteadOfDuplicateError()
    {
        var id = await _mods.ImportModAsync(CreateArchive("1"));
        var original = (await _mods.GetModByIdAsync(id))!;

        var second = await _mods.ImportModAsync(CreateArchive("1"));

        Assert.Equal(id, second);
        var unchanged = (await _mods.GetModByIdAsync(id))!;
        Assert.Equal(original.Path, unchanged.Path);
        Assert.Single(await _repository.GetAllAsync());
    }

    [Fact]
    public async Task SwitchOff_ImportsSameGuidAsIndependentMod()
    {
        await _settings.SetSettingAsync(SettingKeys.UpdateSameGuidOnImport, false);
        var first = await _mods.ImportModAsync(CreateArchive("1"));

        var second = await _mods.ImportModAsync(CreateArchive("2"));

        Assert.NotEqual(first, second);
        var mods = (await _repository.GetAllAsync()).ToList();
        Assert.Equal(2, mods.Count);
        Assert.Contains(mods, mod => mod.Id == first && mod.Version == "1");
        Assert.Contains(mods, mod => mod.Id == second && mod.Version == "2");
    }

    [Fact]
    public async Task SameGuidImport_KeepVersions_RetainsPreviousVersion()
    {
        await _settings.SetSettingAsync(SettingKeys.KeepModVersions, true);
        var id = await _mods.ImportModAsync(CreateArchive("1"));
        var original = (await _mods.GetModByIdAsync(id))!;

        await _mods.ImportModAsync(CreateArchive("2"));

        var saved = Assert.Single(await _mods.GetLocalVersionsAsync(id));
        Assert.Equal("1", saved.Content.Version);
        Assert.Equal(original.Path, saved.Content.Path);
        Assert.True(Directory.Exists(saved.Content.Path));
        Assert.Equal("2", (await _mods.GetModByIdAsync(id))!.Version);
    }

    private string CreateArchive(string version)
    {
        var path = Path.Combine(_root, "same-guid-" + version + "-" + Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
            writer.Write("{\"Guid\":\"" + ModGuid + "\",\"Name\":\"Same mod\",\"Version\":\"" + version + "\",\"Author\":\"Author\"}");
        using (var writer = new StreamWriter(zip.CreateEntry(Patch).Open())) writer.Write("content-" + version);
        return path;
    }

    public void Dispose() { _db.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
