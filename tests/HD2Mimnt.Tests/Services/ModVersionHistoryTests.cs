using System.IO.Compression;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using HD2Mimnt.Infrastructure.Deployment;
using Moq;

namespace HD2Mimnt.Tests.Services;

public sealed class ModVersionHistoryTests : IDisposable
{
    private const string Patch = "0123456789abcdef.patch_0";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hd2mimnt-versions-" + Guid.NewGuid().ToString("N"));
    private readonly HD2MimntDbContext _db;
    private readonly ModRepository _repository;
    private readonly SettingsService _settings;
    private readonly ModService _mods;
    private string Library => Path.Combine(_root, "library");

    public ModVersionHistoryTests()
    {
        Directory.CreateDirectory(_root);
        _db = new(Path.Combine(_root, "test.db"));
        _db.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _repository = new(_db);
        _settings = new(Path.Combine(_root, "settings"));
        _mods = new(_repository, Library, settingsService: _settings, tempDirectory: Path.Combine(_root, "temp"));
    }

    [Fact]
    public async Task LocalArchiveWithoutUsableContent_DoesNotReplaceOrCreateHistory()
    {
        await _settings.SetSettingAsync(SettingKeys.KeepModVersions, true);
        var id = await _mods.ImportModAsync(CreateArchive("1"));
        var path = Path.Combine(_root, "invalid.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("readme.txt").Open())) writer.Write("not a mod");
        await Assert.ThrowsAsync<InvalidDataException>(() => _mods.UpdateFromLocalArchiveAsync(id, path));
        Assert.Equal("1", (await _mods.GetModByIdAsync(id))!.Version);
        Assert.Empty(await _mods.GetLocalVersionsAsync(id));
        Assert.Single(Directory.GetDirectories(Library).Where(p => Guid.TryParse(Path.GetFileName(p), out _)));
    }

    [Fact]
    public async Task DefaultOff_UpdateRemovesOldContentAndDoesNotCreateHistory()
    {
        var id = await _mods.ImportModAsync(CreateArchive("1"));
        var original = (await _mods.GetModByIdAsync(id))!;
        await _mods.UpdateFromLocalArchiveAsync(id, CreateArchive("2"));
        Assert.False(Directory.Exists(original.Path));
        Assert.Empty(await _mods.GetLocalVersionsAsync(id));
        Assert.Equal("2", (await _mods.GetModByIdAsync(id))!.Version);
    }

    [Fact]
    public async Task RetainedVersions_SwitchBackAndForwardAfterReopening_PreservesProfileStateAndInstallationIds()
    {
        await _settings.SetSettingAsync(SettingKeys.KeepModVersions, true);
        var id = await _mods.ImportModAsync(CreateArchive("1"));
        var link = new ModPlatformLink("nexusmods", "helldivers2", "42", "https://www.nexusmods.com/helldivers2/mods/42")
            { InstalledVersion = "1", InstalledFileVersionId = "file-1" };
        await _mods.SetPlatformLinkAsync(id, link);
        var profiles = new ProfileRepository(_db);
        var profile = new Profile { Id = Guid.NewGuid(), Name = "Keep", Mods = new() { new() { ModId = id, Enabled = false, Order = 5, OptionsJson = "[true]" } } };
        await profiles.AddAsync(profile);
        await _mods.ReplaceFromArchiveAsync(id, CreateArchive("2"), link, new PlatformModFile { Id = "file-2", Version = "2" });
        await _mods.UpdateFromLocalArchiveAsync(id, CreateArchive("3"));
        var reopened = new ModService(_repository, Library, settingsService: _settings);
        var first = Assert.Single((await reopened.GetLocalVersionsAsync(id)).Where(v => v.Content.Version == "1"));
        var result = await reopened.SwitchLocalVersionAsync(id, first.Id);
        Assert.Equal("1", result.Mod.Version);
        Assert.Equal("file-1", result.Mod.PlatformLink!.InstalledFileVersionId);
        Assert.Equal("content-1", await File.ReadAllTextAsync(Path.Combine(result.Mod.Path, Patch)));
        var next = Assert.Single((await reopened.GetLocalVersionsAsync(id)).Where(v => v.Content.Version == "2"));
        result = await reopened.SwitchLocalVersionAsync(id, next.Id);
        Assert.Equal("file-2", result.Mod.PlatformLink!.InstalledFileVersionId);
        Assert.Equal("2", result.Mod.PlatformLink.InstalledVersion);
        var savedProfile = (await profiles.GetByIdAsync(profile.Id))!;
        var member = Assert.Single(savedProfile.Mods);
        Assert.Equal(id, member.ModId); Assert.False(member.Enabled); Assert.Equal(5, member.Order); Assert.Equal("[true]", member.OptionsJson);
        Assert.Single(await _repository.GetAllAsync());
        Assert.Equal(2, (await reopened.GetLocalVersionsAsync(id)).Count);
        var index = await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(Library, ".hd2mimnt-versions")).Single());
        Assert.DoesNotContain(Library.Replace("\\", "\\\\"), index);
    }

    [Fact]
    public async Task TurningOffRetention_KeepsExistingHistoryAndSwitchingStillPreservesCurrentVersion()
    {
        await _settings.SetSettingAsync(SettingKeys.KeepModVersions, true);
        var id = await _mods.ImportModAsync(CreateArchive("1"));
        await _mods.UpdateFromLocalArchiveAsync(id, CreateArchive("2"));
        await _settings.SetSettingAsync(SettingKeys.KeepModVersions, false);
        await _mods.UpdateFromLocalArchiveAsync(id, CreateArchive("3"));
        var version = Assert.Single(await _mods.GetLocalVersionsAsync(id));
        await _mods.SwitchLocalVersionAsync(id, version.Id);
        Assert.Equal("3", Assert.Single(await _mods.GetLocalVersionsAsync(id)).Content.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCommit_RollsBackHistoryAndRemovesUncommittedContent(bool switching)
    {
        await _settings.SetSettingAsync(SettingKeys.KeepModVersions, true);
        var id = await _mods.ImportModAsync(CreateArchive("1"));
        await _mods.UpdateFromLocalArchiveAsync(id, CreateArchive("2"));
        var current = (await _mods.GetModByIdAsync(id))!;
        var before = await _mods.GetLocalVersionsAsync(id);
        var failing = new Mock<IModRepository>();
        failing.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).Returns(() => _repository.GetByIdAsync(id));
        failing.Setup(r => r.UpdateAsync(It.IsAny<Mod>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("write failed"));
        var service = new ModService(failing.Object, Library, settingsService: _settings);
        await Assert.ThrowsAsync<IOException>(() => switching
            ? service.SwitchLocalVersionAsync(id, before[0].Id)
            : service.UpdateFromLocalArchiveAsync(id, CreateArchive("3")));
        Assert.Equal(current.Path, (await _repository.GetByIdAsync(id))!.Path);
        Assert.Equal(before[0].Id, Assert.Single(await _mods.GetLocalVersionsAsync(id)).Id);
        Assert.Equal(2, Directory.GetDirectories(Library).Count(p => Guid.TryParse(Path.GetFileName(p), out _)));
        Assert.Empty(Directory.EnumerateFiles(Library, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancelBeforeCommit_DoesNotPublishHistory()
    {
        await _settings.SetSettingAsync(SettingKeys.KeepModVersions, true);
        var id = await _mods.ImportModAsync(CreateArchive("1"));
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _mods.UpdateFromLocalArchiveAsync(id, CreateArchive("2"), cts.Token,
            new InlineProgress(p => { if (p.Percentage == 99) cts.Cancel(); })));
        Assert.Empty(await _mods.GetLocalVersionsAsync(id));
        Assert.Equal("1", (await _mods.GetModByIdAsync(id))!.Version);
    }

    [Fact]
    public async Task DeleteHistoryAndMod_RemovesAllRetainedFiles()
    {
        await _settings.SetSettingAsync(SettingKeys.KeepModVersions, true);
        var id = await _mods.ImportModAsync(CreateArchive("1"));
        await _mods.UpdateFromLocalArchiveAsync(id, CreateArchive("2"));
        var old = Assert.Single(await _mods.GetLocalVersionsAsync(id));
        await _mods.DeleteLocalVersionAsync(id, old.Id);
        Assert.False(Directory.Exists(old.Content.Path));
        Assert.Equal("2", (await _mods.GetModByIdAsync(id))!.Version);
        await _mods.UpdateFromLocalArchiveAsync(id, CreateArchive("3"));
        await _mods.DeleteModAsync(id);
        Assert.Empty(await _repository.GetAllAsync());
        Assert.Empty(Directory.EnumerateFiles(Library, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteMod_WithoutVersionIndexDirectory_RemovesFilesAndRecord(bool deleteLastHistoryFirst)
    {
        var id = await _mods.ImportModAsync(CreateArchive("1"));
        if (deleteLastHistoryFirst)
        {
            await _settings.SetSettingAsync(SettingKeys.KeepModVersions, true);
            await _mods.UpdateFromLocalArchiveAsync(id, CreateArchive("2"));
            var old = Assert.Single(await _mods.GetLocalVersionsAsync(id));
            await _mods.DeleteLocalVersionAsync(id, old.Id);
            Assert.False(Directory.Exists(old.Content.Path));
        }

        var current = (await _mods.GetModByIdAsync(id))!;
        Assert.False(Directory.Exists(Path.Combine(Library, ".hd2mimnt-versions")));

        Assert.True(await _mods.DeleteModAsync(id));

        Assert.False(Directory.Exists(current.Path));
        Assert.Empty(await _repository.GetAllAsync());
        Assert.Empty(await _mods.GetLocalVersionsAsync(id));
        Assert.False(Directory.Exists(Path.Combine(Library, ".hd2mimnt-versions")));
    }

    [Fact]
    public async Task DuplicateImport_DefaultRejectsSameContentIncludingRenamedArchivesAndHistory()
    {
        var archive = CreateArchive("1");
        var id = await _mods.ImportModAsync(archive);
        var renamed = Path.Combine(_root, "renamed.zip"); File.Copy(archive, renamed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _mods.ImportModAsync(renamed));
        await _settings.SetSettingAsync(SettingKeys.KeepModVersions, true);
        await _mods.UpdateFromLocalArchiveAsync(id, CreateArchive("2"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _mods.ImportModAsync(archive));
        Assert.Contains("本地版本", error.Message);
        Assert.Single(await _repository.GetAllAsync());
        Assert.Equal(2, Directory.GetDirectories(Library).Count(p => Guid.TryParse(Path.GetFileName(p), out _)));
    }

    [Fact]
    public async Task AllowDuplicates_ImportsIndependentCopies_AndDifferentContentIsAlwaysAllowed()
    {
        var archive = CreateArchive("1");
        await _settings.SetSettingAsync(SettingKeys.AllowDuplicateImports, true);
        var first = await _mods.ImportModAsync(archive);
        var second = await _mods.ImportModAsync(archive);
        Assert.NotEqual(first, second);
        await _settings.SetSettingAsync(SettingKeys.AllowDuplicateImports, false);
        await _mods.ImportModAsync(CreateArchive("2"));
        Assert.Equal(3, (await _repository.GetAllAsync()).Count());
    }

    [Fact]
    public async Task Retention_KeepsPreviewFilesAndDeletingHistoryPreservesCurrentPreview()
    {
        await _settings.SetSettingAsync(SettingKeys.KeepModVersions, true);
        string AddPreview(string archive)
        {
            using var zip = ZipFile.Open(archive, ZipArchiveMode.Update);
            using var image = zip.CreateEntry("preview.png").Open();
            image.Write(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aWQAAAABJRU5ErkJggg=="));
            return archive;
        }
        var id = await _mods.ImportModAsync(AddPreview(CreateArchive("1")));
        var first = (await _mods.GetModByIdAsync(id))!;
        Assert.True(File.Exists(first.ThumbnailPath));
        await _mods.UpdateFromLocalArchiveAsync(id, AddPreview(CreateArchive("2")));
        var second = (await _mods.GetModByIdAsync(id))!;
        Assert.True(File.Exists(first.ThumbnailPath)); Assert.True(File.Exists(second.ThumbnailPath));
        await _mods.SwitchLocalVersionAsync(id, Assert.Single(await _mods.GetLocalVersionsAsync(id)).Id);
        Assert.Equal(first.ThumbnailPath, (await _mods.GetModByIdAsync(id))!.ThumbnailPath);
        await _mods.DeleteLocalVersionAsync(id, Assert.Single(await _mods.GetLocalVersionsAsync(id)).Id);
        Assert.True(File.Exists(first.ThumbnailPath)); Assert.False(File.Exists(second.ThumbnailPath));
    }

    [Fact]
    public async Task SwitchingDeployedVersion_ShouldKeepRecordsAndAutoDeployIncrementally()
    {
        await _settings.SetSettingAsync(SettingKeys.KeepModVersions, true);
        var gameRoot = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameRoot);
        await _settings.SetSettingAsync(SettingKeys.GamePath, gameRoot);
        var id = await _mods.ImportModAsync(CreateArchive("1"));
        await _mods.UpdateFromLocalArchiveAsync(id, CreateArchive("2"));
        var saved = Assert.Single(await _mods.GetLocalVersionsAsync(id));
        var current = (await _repository.GetByIdAsync(id))!;
        var profiles = new ProfileRepository(_db);
        var profile = new Profile { Id = Guid.NewGuid(), Name = "Deployed" };
        profile.Mods.Add(new ProfileMod { ProfileId = profile.Id, ModId = id, Enabled = true, Order = 0 });
        await profiles.AddAsync(profile);
        var deployments = new DeploymentRepository(_db);
        var target = Path.Combine(gameRoot, "data", Patch);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "deployment");
        var record = new DeploymentRecord
        {
            Id = Guid.NewGuid(), ModId = id, ProfileId = profile.Id, SessionId = Guid.NewGuid(),
            DeploymentMethod = "Copy", StartTime = DateTime.UtcNow, Status = "Completed",
            GamePath = gameRoot, DeploymentOrder = "TopToBottom", DeploymentOrderIndex = 0
        };
        record.Files.Add(new()
        {
            Id = Guid.NewGuid(), DeploymentId = record.Id, ModId = id,
            SourcePath = Path.Combine(current.Path, Patch), TargetPath = target, Method = "Copy",
            GamePath = gameRoot
        });
        await deployments.AddAsync(record);
        var deployment = new DeploymentService(profiles, _repository, deployments,
            new[] { (IDeploymentMethod)new CopyDeploymentMethod() }, settingsService: _settings);
        var service = new ModService(_repository, Library, deploymentService: deployment, settingsService: _settings);

        var result = await service.SwitchLocalVersionAsync(id, saved.Id);

        Assert.True(result.WasDeployed);
        Assert.NotNull(result.Deployment);
        Assert.Equal(DeploymentStatus.Completed, result.Deployment!.Status);
        Assert.Equal("1", (await _repository.GetByIdAsync(id))!.Version);
        Assert.Equal("content-1", await File.ReadAllTextAsync(target));
        var savedRecord = Assert.Single(await deployments.GetByModIdAsync(id));
        Assert.Equal(target, Assert.Single(savedRecord.Files).TargetPath, ignoreCase: true);
        Assert.True(File.Exists(Assert.Single(savedRecord.Files).SourcePath));
    }

    [Fact]
    public async Task SimultaneousImports_OnlyOneIdenticalCopyIsCommitted()
    {
        var archive = CreateArchive("1");
        async Task<bool> Import() { try { await _mods.ImportModAsync(archive); return true; } catch (InvalidOperationException) { return false; } }
        var results = await Task.WhenAll(Task.Run(Import), Task.Run(Import));
        Assert.Single(results.Where(v => v));
        Assert.Single(await _repository.GetAllAsync());
    }

    private string CreateArchive(string version)
    {
        var path = Path.Combine(_root, "version-" + version + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()))
            writer.Write("{\"Name\":\"Same mod\",\"Version\":\"" + version + "\",\"Author\":\"Author\"}");
        using (var writer = new StreamWriter(zip.CreateEntry(Patch).Open())) writer.Write("content-" + version);
        return path;
    }

    private sealed class InlineProgress(Action<ModImportProgress> action) : IProgress<ModImportProgress>
    { public void Report(ModImportProgress value) => action(value); }

    public void Dispose() { _db.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
