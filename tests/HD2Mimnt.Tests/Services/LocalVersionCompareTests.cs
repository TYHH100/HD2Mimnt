using System.IO.Compression;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.Tests.Services;

public sealed class LocalVersionCompareTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hd2mimnt-version-compare-" + Guid.NewGuid().ToString("N"));
    private readonly HD2MimntDbContext _db;
    private readonly ModRepository _repository;
    private readonly SettingsService _settings;
    private readonly ModService _mods;
    private string Library => Path.Combine(_root, "library");

    public LocalVersionCompareTests()
    {
        Directory.CreateDirectory(_root);
        _db = new(Path.Combine(_root, "test.db"));
        _db.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _repository = new(_db);
        _settings = new(Path.Combine(_root, "settings"));
        _mods = new(_repository, Library, settingsService: _settings, tempDirectory: Path.Combine(_root, "temp"));
    }

    [Fact]
    public async Task Compare_ReportsAddedModifiedAndRemovedAgainstCurrentVersion()
    {
        await _settings.SetSettingAsync(SettingKeys.KeepModVersions, true);
        var id = await _mods.ImportModAsync(CreateArchive("1",
            ("0123456789abcdef.patch_0", "one"), ("1111111111111111.patch_0", "old")));
        await _mods.UpdateFromLocalArchiveAsync(id, CreateArchive("2",
            ("0123456789abcdef.patch_0", "two"), ("2222222222222222.patch_0", "new")));

        var saved = Assert.Single(await _mods.GetLocalVersionsAsync(id));
        var diff = await _mods.CompareLocalVersionAsync(id, saved.Id);

        Assert.Contains(diff.Changes, change => change is { Kind: LocalVersionChangeKind.Added, Path: "1111111111111111.patch_0" });
        Assert.Contains(diff.Changes, change => change is { Kind: LocalVersionChangeKind.Removed, Path: "2222222222222222.patch_0" });
        // 两个版本里 body.patch_0 字节数相同、内容不同，必须按内容判定为修改。
        Assert.Contains(diff.Changes, change => change is { Kind: LocalVersionChangeKind.Modified, Path: "0123456789abcdef.patch_0" });
        Assert.Equal(1, diff.AddedCount);
        Assert.Equal(1, diff.RemovedCount);
    }

    [Fact]
    public async Task SwitchBack_PreservesOriginalVersionDate()
    {
        await _settings.SetSettingAsync(SettingKeys.KeepModVersions, true);
        var id = await _mods.ImportModAsync(CreateArchive("1", ("0123456789abcdef.patch_0", "one")));
        var imported = (await _mods.GetModByIdAsync(id))!;
        await _mods.UpdateFromLocalArchiveAsync(id, CreateArchive("2", ("0123456789abcdef.patch_0", "two")));
        var saved = Assert.Single(await _mods.GetLocalVersionsAsync(id));
        Assert.Equal(imported.ModifiedAt, saved.Content.ModifiedAt);

        await _mods.SwitchLocalVersionAsync(id, saved.Id);

        var switched = (await _mods.GetModByIdAsync(id))!;
        Assert.Equal("1", switched.Version);
        Assert.Equal(imported.ModifiedAt, switched.ModifiedAt);
    }

    [Fact]
    public void VersionChoiceLabel_ShowsContentDateInsteadOfArchiveDate()
    {
        var content = new Mod { Version = "1.0.0", Size = 1024, ModifiedAt = new DateTime(2024, 5, 6, 12, 0, 0, DateTimeKind.Utc) };
        var choice = new LocalModVersionChoice(new LocalModVersion(Guid.NewGuid(), new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), content));

        Assert.Contains("2024-05-06", choice.Label);
        Assert.DoesNotContain("2026-01-02", choice.Label);
    }

    private string CreateArchive(string version, params (string Path, string Content)[] files)
    {
        var path = Path.Combine(_root, "compare-" + version + "-" + Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
            writer.Write("{\"Name\":\"Compare mod\",\"Version\":\"" + version + "\",\"Author\":\"Author\"}");
        foreach (var (name, content) in files)
            using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write(content);
        return path;
    }

    public void Dispose() { _db.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
