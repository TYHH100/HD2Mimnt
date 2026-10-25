using System.IO.Compression;
using System.Net;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.Platforms;
using HD2Mimnt.Infrastructure.Repositories;
using Moq;

namespace HD2Mimnt.Tests.Services;

public sealed class ModOnlineServiceTests : IDisposable
{
    private const string Patch = "0123456789abcdef.patch_0";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-OnlineTests-" + Guid.NewGuid().ToString("N"));
    private HD2MimntDbContext _db;
    private ModRepository _repository;
    private readonly Mock<IModPlatform> _platform = new();
    private readonly Mock<IModFileDownloadPlatform> _files;
    private readonly ModService _mods;
    private readonly ModOnlineService _online;
    private readonly ModPlatformLink _link = new("nexusmods", "helldivers2", "16633", "https://www.nexusmods.com/helldivers2/mods/16633");
    private readonly PlatformModFile _file = new() { Id = "selected-version", Name = "Main", Version = "2.0" };
    private readonly Mod _original;
    private readonly byte[] _archive;

    public ModOnlineServiceTests()
    {
        Directory.CreateDirectory(_root);
        _db = new(Path.Combine(_root, "mods.db"));
        _db.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _repository = new(_db);
        _mods = new(_repository, Path.Combine(_root, "storage"));
        _files = _platform.As<IModFileDownloadPlatform>();
        _platform.SetupGet(p => p.Id).Returns("nexusmods");
        _platform.Setup(p => p.GetModInfoAsync("16633", It.IsAny<CancellationToken>())).ReturnsAsync(new PlatformModInfo { Name = "Remote" });
        _platform.Setup(p => p.GetFilesAsync("16633", null, It.IsAny<CancellationToken>())).ReturnsAsync(new List<PlatformModFile> { _file });
        _archive = CreateZip(Patch, "new patch content");
        _files.Setup(p => p.DownloadFileAsync("16633", _file.Id, It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(_archive));
        var registry = new PlatformRegistry();
        registry.RegisterPlatform(_platform.Object);
        _online = new(registry, _mods, new(registry));
        _original = new Mod { Id = Guid.NewGuid(), Name = "Local name", Author = "Local author", Version = "1.0",
            Tags = new() { "user tag" }, ImportedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            PlatformId = _link.PlatformId, PlatformModId = _link.ModId, PlatformLink = _link,
            Path = Path.Combine(_root, "storage", "original"), Hash = "old hash" };
        Directory.CreateDirectory(_original.Path);
        File.WriteAllText(Path.Combine(_original.Path, Patch), "original content");
        _repository.AddAsync(_original).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Update_ShouldReplaceOriginalAndKeepProfileStateAfterReopeningDatabase()
    {
        var profiles = new ProfileRepository(_db);
        var profile = new Profile { Id = Guid.NewGuid(), Name = "Profile", CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow, Mods = new() { new() { ModId = _original.Id, Enabled = false, Order = 7, OptionsJson = "keep options" } } };
        await profiles.AddAsync(profile);
        var result = await _online.UpdateAsync(_original.Id, _link, _file);
        Assert.False(result.WasDeployed);
        Assert.Null(result.Deployment);
        Assert.False(Directory.Exists(_original.Path));
        _db.Dispose();
        _db = new(Path.Combine(_root, "mods.db"));
        _repository = new(_db);
        var saved = (await _repository.GetByIdAsync(_original.Id))!;
        Assert.Equal("2.0", saved.Version);
        Assert.Equal(_file.Id, saved.PlatformLink!.InstalledFileVersionId);
        Assert.Equal(_original.Name, saved.Name);
        Assert.Equal(_original.Author, saved.Author);
        Assert.Equal(_original.Tags, saved.Tags);
        Assert.Equal(_original.ImportedAt, saved.ImportedAt.ToUniversalTime());
        Assert.NotEqual(_original.Hash, saved.Hash);
        Assert.Equal("new patch content", await File.ReadAllTextAsync(Path.Combine(saved.Path, Patch)));
        Assert.Single(await _repository.GetAllAsync());
        var assignment = Assert.Single((await new ProfileRepository(_db).GetByIdAsync(profile.Id))!.Mods);
        Assert.Equal(_original.Id, assignment.ModId);
        Assert.False(assignment.Enabled);
        Assert.Equal(7, assignment.Order);
        Assert.Equal("keep options", assignment.OptionsJson);
        _files.Verify(p => p.DownloadFileAsync("16633", _file.Id, It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
        _platform.Verify(p => p.DownloadModAsync(It.IsAny<string>(), It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DownloadOnly_ShouldSaveSelectedArchiveWithoutReplacingMod()
    {
        var target = Path.Combine(_root, "saved.zip");
        await _online.DownloadAsync(_original.Id, _link, _file, target);
        Assert.Equal(_archive, await File.ReadAllBytesAsync(target));
        await AssertOriginalAsync();
    }

    [Fact]
    public async Task DeployedUpdate_ShouldKeepDeploymentWhenGamePathNotConfigured()
    {
        var profiles = new ProfileRepository(_db);
        var profile = new Profile { Id = Guid.NewGuid(), Name = "Deployed profile", CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow };
        await profiles.AddAsync(profile);
        var deployments = new DeploymentRepository(_db);
        var target = Path.Combine(_root, "game", "data", Patch);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "old deployment");
        var record = new DeploymentRecord { Id = Guid.NewGuid(), ModId = _original.Id, ProfileId = profile.Id,
            SessionId = Guid.NewGuid(), DeploymentMethod = "Copy", StartTime = DateTime.UtcNow, Status = "Success" };
        record.Files.Add(new() { Id = Guid.NewGuid(), DeploymentId = record.Id, ModId = _original.Id,
            SourcePath = Path.Combine(_original.Path, Patch), TargetPath = target, Method = "Copy" });
        await deployments.AddAsync(record);
        var deployment = new DeploymentService(profiles, _repository, deployments,
            new[] { (IDeploymentMethod)new CopyDeploymentMethod() });
        var mods = new ModService(_repository, Path.Combine(_root, "storage"), deploymentService: deployment);
        var archive = Path.Combine(_root, "update.zip");
        await File.WriteAllBytesAsync(archive, _archive);

        var result = await mods.ReplaceFromArchiveAsync(_original.Id, archive, _link, _file);

        Assert.True(result.WasDeployed);
        Assert.Null(result.Deployment);
        Assert.Equal("old deployment", await File.ReadAllTextAsync(target));
        Assert.Single(await deployments.GetByModIdAsync(_original.Id));
        Assert.False(Directory.Exists(_original.Path));
        Assert.Equal("2.0", (await _repository.GetByIdAsync(_original.Id))!.Version);
    }

    [Fact]
    public async Task DeployedUpdate_ShouldAutoDeployIncrementallyWhenGameConfigured()
    {
        Directory.CreateDirectory(Path.Combine(_root, "settings"));
        var settings = new SettingsService(Path.Combine(_root, "settings"));
        var gameRoot = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameRoot);
        await settings.SetSettingAsync(SettingKeys.GamePath, gameRoot);
        var profiles = new ProfileRepository(_db);
        var profile = new Profile
        {
            Id = Guid.NewGuid(), Name = "Deployed profile", CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow
        };
        profile.Mods.Add(new ProfileMod
        {
            ProfileId = profile.Id, ModId = _original.Id, Enabled = true, Order = 0
        });
        await profiles.AddAsync(profile);
        var deployments = new DeploymentRepository(_db);
        var target = Path.Combine(gameRoot, "data", Patch);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "old deployment");
        var record = new DeploymentRecord
        {
            Id = Guid.NewGuid(), ModId = _original.Id, ProfileId = profile.Id, SessionId = Guid.NewGuid(),
            DeploymentMethod = "Copy", StartTime = DateTime.UtcNow, Status = "Completed",
            GamePath = gameRoot, DeploymentOrder = "TopToBottom", DeploymentOrderIndex = 0
        };
        record.Files.Add(new()
        {
            Id = Guid.NewGuid(), DeploymentId = record.Id, ModId = _original.Id,
            SourcePath = Path.Combine(_original.Path, Patch), TargetPath = target, Method = "Copy",
            GamePath = gameRoot
        });
        await deployments.AddAsync(record);
        var deployment = new DeploymentService(profiles, _repository, deployments,
            new[] { (IDeploymentMethod)new CopyDeploymentMethod() }, settingsService: settings);
        var mods = new ModService(_repository, Path.Combine(_root, "storage"), deploymentService: deployment);
        var archive = Path.Combine(_root, "update.zip");
        await File.WriteAllBytesAsync(archive, _archive);

        var result = await mods.ReplaceFromArchiveAsync(_original.Id, archive, _link, _file);

        Assert.True(result.WasDeployed);
        Assert.NotNull(result.Deployment);
        Assert.Equal(DeploymentStatus.Completed, result.Deployment!.Status);
        Assert.Equal(1, result.Deployment.UpdatedFiles);
        Assert.Equal("new patch content", await File.ReadAllTextAsync(target));
        var saved = Assert.Single(await deployments.GetByModIdAsync(_original.Id));
        var savedFile = Assert.Single(saved.Files);
        Assert.NotEqual(Path.Combine(_original.Path, Patch), savedFile.SourcePath);
        Assert.True(File.Exists(savedFile.SourcePath));
        Assert.Equal(target, savedFile.TargetPath, ignoreCase: true);
    }

    [Fact]
    public async Task InvalidPackage_ShouldKeepOriginalAndRemovePreparedDirectory()
    {
        var archive = Path.Combine(_root, "invalid.zip");
        await File.WriteAllBytesAsync(archive, CreateZip("readme.txt", "no patches"));
        await Assert.ThrowsAsync<InvalidDataException>(() => _mods.ReplaceFromArchiveAsync(_original.Id, archive, _link, _file));
        await AssertOriginalAsync();
        AssertOnlyOriginalDirectory();
    }

    [Fact]
    public async Task DatabaseFailure_ShouldKeepOriginalAndRemovePreparedDirectory()
    {
        var failing = new Mock<IModRepository>();
        failing.Setup(p => p.GetByIdAsync(_original.Id, It.IsAny<CancellationToken>())).Returns(() => _repository.GetByIdAsync(_original.Id));
        failing.Setup(p => p.UpdateAsync(It.IsAny<Mod>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("write failed"));
        var mods = new ModService(failing.Object, Path.Combine(_root, "storage"));
        var archive = Path.Combine(_root, "update.zip");
        await File.WriteAllBytesAsync(archive, _archive);
        await Assert.ThrowsAsync<IOException>(() => mods.ReplaceFromArchiveAsync(_original.Id, archive, _link, _file));
        await AssertOriginalAsync();
        AssertOnlyOriginalDirectory();
    }

    [Fact]
    public async Task CancellationBeforeCommit_ShouldKeepOriginalAndRemovePreparedDirectory()
    {
        using var cts = new CancellationTokenSource();
        var archive = Path.Combine(_root, "update.zip");
        await File.WriteAllBytesAsync(archive, _archive);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _mods.ReplaceFromArchiveAsync(_original.Id, archive, _link, _file,
            cts.Token, new InlineProgress<ModImportProgress>(p => { if (p.Percentage == 99) cts.Cancel(); })));
        await AssertOriginalAsync();
        AssertOnlyOriginalDirectory();
    }

    [Fact]
    public async Task DownloadCancellation_ShouldWaitForExecutionAndPreserveExistingDestination()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _files.Setup(p => p.DownloadFileAsync("16633", _file.Id, It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, IProgress<DownloadProgress>? _, CancellationToken token) =>
            { started.SetResult(); await Task.Delay(Timeout.Infinite, token); return (Stream)new MemoryStream(); });
        var target = Path.Combine(_root, "saved.zip");
        await File.WriteAllTextAsync(target, "keep existing");
        var download = _online.DownloadAsync(_original.Id, _link, _file, target, cancellationToken: cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Equal("keep existing", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(_root, "*.download"));
        await AssertOriginalAsync();
    }

    [Fact]
    public async Task CheckFailure_ShouldExposeErrorAndKeepPreviousCheckState()
    {
        _platform.Setup(p => p.GetFilesAsync("16633", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => _online.CheckAsync(_original.Id));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Null((await _repository.GetByIdAsync(_original.Id))!.LastUpdateCheck);
        _files.Setup(p => p.DownloadFileAsync("16633", _file.Id, It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(error);
        await Assert.ThrowsAsync<HttpRequestException>(() => _online.UpdateAsync(_original.Id, _link, _file));
        await AssertOriginalAsync();
    }

    [Fact]
    public async Task UnknownVersion_ShouldNotInventUpdateAndRejectReplacement()
    {
        _file.Version = "";
        var result = await _online.CheckAsync(_original.Id);
        Assert.Null(result.Mod.PlatformVersion);
        Assert.NotNull(result.Mod.LastUpdateCheck);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _online.UpdateAsync(_original.Id, _link, _file));
        await AssertOriginalAsync();
    }

    private async Task AssertOriginalAsync()
    {
        var saved = (await _repository.GetByIdAsync(_original.Id))!;
        Assert.Equal(_original.Path, saved.Path);
        Assert.Equal("1.0", saved.Version);
        Assert.Equal("old hash", saved.Hash);
        Assert.Null(saved.PlatformLink!.InstalledFileVersionId);
        Assert.Equal("original content", await File.ReadAllTextAsync(Path.Combine(saved.Path, Patch)));
    }
    private void AssertOnlyOriginalDirectory() => Assert.Equal(_original.Path,
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "storage")).Where(p => Path.GetFileName(p) != "thumbnails")));
    private static byte[] CreateZip(string name, string content)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write(content);
        return stream.ToArray();
    }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    public void Dispose() { _db.Dispose(); Directory.Delete(_root, true); }
}
