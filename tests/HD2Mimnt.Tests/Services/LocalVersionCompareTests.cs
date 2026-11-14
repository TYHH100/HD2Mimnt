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

    [Fact]
    public async Task ReadLocalVersionFile_ReturnsTextAndBinarySummary()
    {
        await _settings.SetSettingAsync(SettingKeys.KeepModVersions, true);
        var id = await _mods.ImportModAsync(CreateArchiveBytes("1",
            ("0123456789abcdef.patch_0", System.Text.Encoding.UTF8.GetBytes("one")),
            ("notes.txt", System.Text.Encoding.UTF8.GetBytes("hello\nworld")),
            ("data.bin", new byte[] { 1, 2, 0, 4, 5 })));
        await _mods.UpdateFromLocalArchiveAsync(id, CreateArchiveBytes("2",
            ("0123456789abcdef.patch_0", System.Text.Encoding.UTF8.GetBytes("two")),
            ("notes.txt", System.Text.Encoding.UTF8.GetBytes("hello\nthere")),
            ("data.bin", new byte[] { 1, 2, 0, 4, 9 })));
        var saved = Assert.Single(await _mods.GetLocalVersionsAsync(id));

        var text = await _mods.ReadLocalVersionFileAsync(id, saved.Id, "notes.txt");
        Assert.True(text.IsText);
        Assert.Equal("hello\nthere", text.CurrentText);
        Assert.Equal("hello\nworld", text.SelectedText);

        var binary = await _mods.ReadLocalVersionFileAsync(id, saved.Id, "data.bin");
        Assert.False(binary.IsText);
        Assert.Equal(1L, binary.DifferingByteCount);
        Assert.Equal(4L, binary.FirstDifferenceOffset);

        await Assert.ThrowsAsync<InvalidDataException>(() => _mods.ReadLocalVersionFileAsync(id, saved.Id, "../escape.txt"));
    }

    [Fact]
    public async Task VersionDiffViewModel_LoadsTextDiffForSelectedFile()
    {
        await _settings.SetSettingAsync(SettingKeys.KeepModVersions, true);
        var id = await _mods.ImportModAsync(CreateArchive("1", ("0123456789abcdef.patch_0", "one")));
        await _mods.UpdateFromLocalArchiveAsync(id, CreateArchive("2", ("0123456789abcdef.patch_0", "two")));
        var saved = Assert.Single(await _mods.GetLocalVersionsAsync(id));
        var diff = await _mods.CompareLocalVersionAsync(id, saved.Id);
        var items = diff.Changes.Select(change => new VersionFileChangeItem(change)).ToArray();

        var vm = new VersionDiffViewModel(_mods, id, saved.Id, "Compare mod", "2 → 1", "summary", items);
        vm.SelectedFile = items.First(item => item.Change.Path == "0123456789abcdef.patch_0");
        await vm.LoadTask;

        Assert.True(vm.HasContent);
        Assert.Contains(vm.Lines, line => line is
            { LeftText: "two", RightText: "one", LeftChanged: true, RightChanged: true });
    }

    [Fact]
    public void DiffBuilder_PairsModifiedLinesSideBySide()
    {
        var diff = VersionDiffBuilder.Build("a\nb\nc", "a\nx\ny\nc");

        Assert.False(diff.Truncated);
        Assert.Equal(4, diff.Lines.Count);
        Assert.Equal(("a", "a"), (diff.Lines[0].LeftText, diff.Lines[0].RightText));
        Assert.True(diff.Lines[1] is { LeftChanged: true, RightChanged: true, LeftText: "b", RightText: "x" });
        Assert.True(diff.Lines[2] is { LeftChanged: false, RightChanged: true, LeftNumber: null, RightText: "y" });
        Assert.Equal(("c", "c"), (diff.Lines[3].LeftText, diff.Lines[3].RightText));
    }

    [Fact]
    public void DiffBuilder_TruncatesLongDiff()
    {
        var left = string.Join("\n", Enumerable.Range(0, 900).Select(index => "left-" + index));
        var right = string.Join("\n", Enumerable.Range(0, 900).Select(index => "right-" + index));

        var diff = VersionDiffBuilder.Build(left, right);

        Assert.True(diff.Truncated);
        Assert.Equal(VersionDiffBuilder.MaxRows, diff.Lines.Count);
    }

    private string CreateArchive(string version, params (string Path, string Content)[] files)
        => CreateArchiveBytes(version, files.Select(file =>
            (file.Path, System.Text.Encoding.UTF8.GetBytes(file.Content))).ToArray());

    private string CreateArchiveBytes(string version, params (string Path, byte[] Content)[] files)
    {
        var path = Path.Combine(_root, "compare-" + version + "-" + Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
            writer.Write("{\"Name\":\"Compare mod\",\"Version\":\"" + version + "\",\"Author\":\"Author\"}");
        foreach (var (name, content) in files)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(content);
        }
        return path;
    }

    public void Dispose() { _db.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
