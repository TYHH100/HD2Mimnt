using System.Diagnostics;
using System.Text.Json;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interop;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.Interop;
using HD2Mimnt.Infrastructure.Repositories;
using HD2Mimnt.Infrastructure.Storage;

namespace HD2Mimnt.Tests.Services;

public sealed class OfflineInteropTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-interop-tests-" + Guid.NewGuid().ToString("N"));
    private HD2MimntDbContext _db = null!;
    private ModRepository _mods = null!;
    private ProfileRepository _profiles = null!;
    private DeploymentRepository _deployments = null!;
    private ModService _modService = null!;
    private ProfileService _profileService = null!;
    private DeploymentService _deploymentService = null!;
    private SettingsService _settings = null!;
    private StorageSettingsService _storage = null!;
    private BackupService _backups = null!;
    private OfflineInteropService _interop = null!;
    private OfflineInteropSnapshotReader _reader = null!;
    private string Bootstrap => Path.Combine(_root, "bootstrap");
    private string Library => Path.Combine(_root, "relocated", "library");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Library);
        var store = new StorageLocationStore(Bootstrap);
        await store.SaveAsync(new(new StorageLocations(Path.Combine(_root, "relocated", "app"), Library,
            Path.Combine(_root, "cache"))));
        _storage = new(store);
        Directory.CreateDirectory(_storage.Current.AppDataPath);
        _db = new(_storage.Current.DatabasePath);
        await _db.InitializeDatabaseAsync();
        _mods = new(_db); _profiles = new(_db); _deployments = new(_db);
        _deploymentService = new(_profiles, _mods, _deployments, [new CopyDeploymentMethod()]);
        _modService = new(_mods, Library, deploymentService: _deploymentService, tempDirectory: Path.Combine(_root, "temp"));
        _profileService = new(_profiles, _mods);
        _settings = new(new SettingsRepository(_storage.Current.DatabasePath), _storage.Current.AppDataPath);
        _backups = new(_storage.Current.AppDataPath, _db, _settings);
        _reader = new(_storage.Current.DatabasePath);
        _interop = new(_reader, new(Bootstrap), _storage, _settings, _modService, _profileService,
            _deploymentService, _backups);
    }

    public async Task DisposeAsync()
    {
        await _interop.DisposeAsync();
        _db.Dispose();
        // 仅清理本测试创建的独立目录，池已释放后不遗留 WAL/SHM 或输出。
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }

    private async Task<Mod> AddMod(string name, bool variants = false)
    {
        var id = Guid.NewGuid();
        var path = Path.Combine(Library, id.ToString());
        Directory.CreateDirectory(path);
        var manifest = new ModManifest { Guid = Guid.NewGuid(), Name = name, ManifestVersion = ManifestVersion.V1 };
        if (variants)
        {
            manifest.Options = [new ModOption { Name = "颜色", Include = ["red"], SubOptions =
                [new() { Name = "红", Include = ["red"] }, new() { Name = "蓝", Include = ["blue"] }] }];
            foreach (var color in new[] { "red", "blue" })
            {
                Directory.CreateDirectory(Path.Combine(path, color));
                await File.WriteAllTextAsync(Path.Combine(path, color, "0123456789abcdef.patch_0"), color);
            }
        }
        else await File.WriteAllTextAsync(Path.Combine(path, "0123456789abcdef.patch_0"), name);
        var mod = new Mod { Id = id, Path = path, Name = name, Version = "1.0.0", Manifest = manifest,
            BslRole = BslModRole.None, BslDetectionVersion = BslModDetector.DetectionVersion,
            ImportedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow, ResourceTypeIds = [ulong.MaxValue] };
        await _mods.AddAsync(mod);
        return mod;
    }

    private async Task<Profile> AddProfile(Mod mod, bool enabled = true)
    {
        var profile = new Profile { Id = Guid.NewGuid(), Name = "测试配置", IsActive = true,
            CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow };
        profile.Mods.Add(new() { ProfileId = profile.Id, ModId = mod.Id, Enabled = enabled, Order = 0,
            OptionsJson = new ProfileModOptionsState { OptionToggles = [true], OptionSelections = [0] }.ToJson() });
        await _profiles.AddAsync(profile);
        return profile;
    }

    private async Task<OfflineRuntimeV1> Read()
        => JsonSerializer.Deserialize<OfflineRuntimeV1>(await File.ReadAllTextAsync(_interop.RuntimePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public async Task Refresh_AllLibraryModsAndVariants_ExportsUndeployedWithoutCopyingLibrary()
    {
        var disabled = await AddMod("未启用中文模组", variants: true);
        var unassigned = await AddMod("未加入配置");
        await AddProfile(disabled, enabled: false);
        await _interop.RefreshAsync();
        var snapshot = await Read();
        Assert.Equal(2, snapshot.Library.Count);
        Assert.Contains(snapshot.Library, item => item.Id == unassigned.Id);
        Assert.Equal(2, snapshot.Library.Single(item => item.Id == disabled.Id).Files.Count);
        Assert.Equal("ffffffffffffffff", snapshot.Library[0].ResourceTypeIds![0]);
        Assert.False(snapshot.Profiles[0].Mods[0].Enabled);
        Assert.Empty(snapshot.Deployments);
        Assert.All(snapshot.Library, item => Assert.StartsWith(Library, item.Path));
        Assert.Equal(_storage.Current.ModsPath, snapshot.Storage.ModsPath);
        Assert.StartsWith(Bootstrap, _interop.RuntimePath);
        Assert.False(Directory.Exists(Path.Combine(Bootstrap, "interop", "mods")));
    }

    [Fact]
    public async Task Deploy_ChangingCurrentOptionsAndProfile_PreservesActualSnapshotAndCleanup()
    {
        var mod = await AddMod("选项模组", variants: true);
        var profile = await AddProfile(mod);
        var game = Path.Combine(_root, "game"); Directory.CreateDirectory(game);
        await _interop.StartAsync();
        var deployed = await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, game);
        Assert.Equal(DeploymentStatus.Completed, deployed.Status);
        await _profileService.SetModOptionsAsync(profile.Id, mod.Id,
            new() { OptionToggles = [true], OptionSelections = [1] });
        var other = await _profileService.CreateProfileAsync("尚未部署配置");
        await _profileService.SetActiveProfileAsync(other);
        await _interop.RefreshAsync();
        var snapshot = await Read();
        var record = Assert.Single(snapshot.Deployments);
        Assert.Equal(other, snapshot.ActiveProfileId);
        Assert.Equal(profile.Id, record.ProfileId);
        Assert.Equal(0, record.Options!.OptionSelections![0]);
        Assert.Equal(1, snapshot.Profiles.Single(item => item.Id == profile.Id).Mods[0].Options.OptionSelections![0]);
        Assert.Equal("1.0.0", record.ModVersion);
        Assert.InRange((record.StartedUtc - new DateTimeOffset(deployed.StartTime)).Duration(),
            TimeSpan.Zero, TimeSpan.FromSeconds(10));
        Assert.Equal(0, record.Order);
        Assert.True(record.IsComplete);
        Assert.Equal(3, record.Files.Count);
        Assert.Equal(2, record.Files.Count(file => file.IsPlaceholder));
        Assert.All(record.Files, file => Assert.True(File.Exists(file.TargetPath)));
        await _deploymentService.PreDeploymentCleanupAsync();
        await WaitFor(snapshot => snapshot.Deployments.Count == 0);
        Assert.Single((await Read()).Library);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Bootstrap, "interop"), "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Refresh_LegacyCancelledRecord_ListsOnlyExistingFilesAndKeepsOptionsUnknown()
    {
        var mod = await AddMod("遗留部署"); var profile = await AddProfile(mod);
        var present = Path.Combine(_root, "present.patch_0"); await File.WriteAllTextAsync(present, "partial");
        var missing = Path.Combine(_root, "missing.patch_1");
        var record = new DeploymentRecord { Id = Guid.NewGuid(), SessionId = Guid.NewGuid(), ModId = mod.Id,
            ProfileId = profile.Id, StartTime = DateTime.UtcNow, Status = "Cancelled", DeploymentMethod = "Copy" };
        record.Files = [new() { Id = Guid.NewGuid(), DeploymentId = record.Id, ModId = mod.Id,
            SourcePath = mod.Path, TargetPath = present, Method = "Copy" },
            new() { Id = Guid.NewGuid(), DeploymentId = record.Id, ModId = mod.Id, SourcePath = mod.Path,
                TargetPath = missing, Method = "Copy" }];
        await _deployments.AddAsync(record);
        await _interop.RefreshAsync();
        var exported = Assert.Single((await Read()).Deployments);
        Assert.Null(exported.Options); Assert.Null(exported.Order); Assert.Null(exported.ModVersion);
        Assert.Equal(present, Assert.Single(exported.Files).TargetPath);
        Assert.Equal(missing, Assert.Single(exported.MissingTargets)); Assert.False(exported.IsComplete);
    }

    [Fact]
    public async Task Events_MetadataAndProfileChanges_AutomaticallyPublishAndCloseLeavesReadableSnapshot()
    {
        var mod = await AddMod("原名"); var profile = await AddProfile(mod);
        await _interop.StartAsync();
        await _modService.UpdateModMetadataAsync(mod.Id, name: "修改后的名称");
        await _profileService.SetModEnabledAsync(profile.Id, mod.Id, false);
        await WaitFor(snapshot => snapshot.Library[0].Name == "修改后的名称" && !snapshot.Profiles[0].Mods[0].Enabled);
        await _interop.DisposeAsync();
        Assert.Equal("修改后的名称", (await Read()).Library[0].Name);
    }

    [Fact]
    public async Task Publish_FutureMajorVersion_KeepsPinnedV1AndAdvertisesBoth()
    {
        await AddMod("旧消费者仍可读"); await _interop.RefreshAsync();
        var snapshot = (await Read()) with { ManagerVersion = "99.0.0" };
        var publisher = new OfflineInteropPublisher(Bootstrap, [new FutureAdapter()]);
        await publisher.PublishAsync(snapshot);
        using var index = JsonDocument.Parse(await File.ReadAllTextAsync(publisher.IndexPath));
        Assert.Equal(2, index.RootElement.GetProperty("latestMajorVersion").GetInt32());
        Assert.Equal(1, index.RootElement.GetProperty("defaultMajorVersion").GetInt32());
        Assert.Equal(new[] { 1, 2 }, index.RootElement.GetProperty("versions").EnumerateArray()
            .Select(item => item.GetProperty("majorVersion").GetInt32()));
        var oldConsumer = await Read();
        Assert.Equal(1, oldConsumer.SchemaVersion);
        Assert.Equal("99.0.0", oldConsumer.ManagerVersion);
        Assert.Equal("旧消费者仍可读", oldConsumer.Library[0].Name);
        Assert.Throws<ArgumentException>(() => new OfflineInteropPublisher(Bootstrap, [new OfflineInteropV1Adapter()]));
    }

    [Fact]
    public async Task Publish_LockedTarget_PreservesOldJsonCleansTemporaryAndRecovers()
    {
        var mod = await AddMod("首次发布"); await _interop.StartAsync();
        var original = await File.ReadAllTextAsync(_interop.RuntimePath);
        using (var locked = new FileStream(_interop.RuntimePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await _modService.UpdateModMetadataAsync(mod.Id, name: "占用结束后发布");
            var timer = Stopwatch.StartNew();
            while (_interop.LastError == null && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(50);
            Assert.NotNull(_interop.LastError);
            Assert.Equal(original, await File.ReadAllTextAsync(_interop.RuntimePath));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(Bootstrap, "interop"), "*.tmp", SearchOption.AllDirectories));
        }
        await WaitFor(snapshot => snapshot.Library[0].Name == "占用结束后发布");
        Assert.Null(_interop.LastError);
    }

    [Fact]
    public async Task Read_SnapshotWhileWriterHasUncommittedChanges_DoesNotMixRows()
    {
        var mod = await AddMod("已提交名称"); var profile = await AddProfile(mod);
        using var connection = await _db.OpenConnectionAsync();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE Mods SET Name = '未提交名称'; UPDATE ProfileMods SET Enabled = 0;";
        await command.ExecuteNonQueryAsync();
        var old = await _reader.ReadAsync();
        Assert.Equal("已提交名称", old.Mods[0].Name); Assert.True(old.Profiles[0].Mods[0].Enabled);
        transaction.Commit();
        var current = await _reader.ReadAsync();
        Assert.Equal("未提交名称", current.Mods[0].Name); Assert.False(current.Profiles[0].Mods[0].Enabled);
    }

    [Fact]
    public async Task Refresh_MissingLibraryDirectory_ReportsIncompleteWithoutLosingMod()
    {
        var mod = await AddMod("缺失目录"); Directory.Delete(mod.Path, true);
        await _interop.RefreshAsync();
        var exported = Assert.Single((await Read()).Library);
        Assert.False(exported.FilesComplete); Assert.NotNull(exported.FilesError); Assert.Empty(exported.Files);
    }

    [Fact]
    public async Task Migration_MovingDefaultAppData_KeepsFixedInteropFilesAtBootstrap()
    {
        var store = new StorageLocationStore(Path.Combine(_root, "migration-bootstrap"));
        var service = new StorageSettingsService(store);
        var path = Path.Combine(store.BootstrapPath, "interop", "v1", "runtime.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "fixed-entry");
        using (var db = new HD2MimntDbContext(service.Current.DatabasePath)) await db.InitializeDatabaseAsync();
        var next = new StorageLocations(Path.Combine(_root, "new-app"), Path.Combine(_root, "new-mods"),
            Path.Combine(_root, "new-cache"));
        await service.SaveAsync(next);
        await service.InitializeAsync();
        Assert.Equal("fixed-entry", await File.ReadAllTextAsync(path));
        Assert.False(Directory.Exists(Path.Combine(next.AppDataPath, "interop")));
        Assert.True(File.Exists(next.DatabasePath));
        Assert.Equal(next, service.Current);
    }

    [Fact]
    public async Task Publish_PreCancelled_PreservesSnapshotAndLeavesNoTemporaryFiles()
    {
        await AddMod("取消前快照"); await _interop.RefreshAsync();
        var original = await File.ReadAllTextAsync(_interop.RuntimePath);
        var snapshot = await Read();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OfflineInteropPublisher(Bootstrap)
            .PublishAsync(snapshot with { SnapshotId = "unpublished" }, cancellation.Token));
        Assert.Equal(original, await File.ReadAllTextAsync(_interop.RuntimePath));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Bootstrap, "interop"), "*.tmp", SearchOption.AllDirectories));
    }

    private async Task WaitFor(Func<OfflineRuntimeV1, bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (predicate(await Read())) return;
            await Task.Delay(50);
        }
        Assert.Fail("未在限定时间内发布接口状态，错误：" + _interop.LastError);
    }

    private sealed class FutureAdapter : IOfflineInteropAdapter
    {
        public int MajorVersion => 2;
        public int MinorVersion => 0;
        public object Project(OfflineRuntimeV1 snapshot) => new { format = "future-format", schemaVersion = 2,
            mods = snapshot.Library.Select(mod => mod.Id) };
    }
}
