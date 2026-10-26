using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace HD2Mimnt.Tests.Services;

public sealed class StorageSettingsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hd2mimnt-storage-test-" + Guid.NewGuid().ToString("N"));
    private readonly StorageLocationStore _store;
    private readonly StorageSettingsService _service;
    private StorageLocations Next => new(Path.Combine(_root, "new-data"), Path.Combine(_root, "new-mods"), Path.Combine(_root, "new-cache"));

    public StorageSettingsServiceTests()
    {
        _store = new(Path.Combine(_root, "old-data"));
        _service = new(_store);
    }

    [Fact]
    public async Task InitializeAsync_FirstStart_ShouldNotCreateMigrationFilesOrDirectories()
    {
        await _service.InitializeAsync();

        Assert.False(_service.HasPendingMigration);
        Assert.Null(_service.StartupMessage);
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeAsync_WithoutPending_ShouldLeaveLegacyCachesAndLocatorUntouched(bool customLocations)
    {
        if (customLocations) await _store.SaveAsync(new(Next));
        var current = _service.Current;
        var image = await WriteAsync(current.AppDataPath, "ImageCache/a.png", "image");
        var download = await WriteAsync(current.AppDataPath, "Downloads/a.part", "download");
        var locator = Path.Combine(_store.BootstrapPath, "storage-locations.json");
        var existed = File.Exists(locator);
        var lastWrite = File.GetLastWriteTimeUtc(locator);
        var content = existed ? await File.ReadAllTextAsync(locator) : null;
        var reopened = new StorageSettingsService(new StorageLocationStore(_store.BootstrapPath));

        await reopened.InitializeAsync();

        Assert.False(reopened.HasPendingMigration);
        Assert.Null(reopened.StartupMessage);
        Assert.Equal(current, reopened.Current);
        Assert.Equal("image", await File.ReadAllTextAsync(image));
        Assert.Equal("download", await File.ReadAllTextAsync(download));
        Assert.False(Directory.Exists(current.ModsPath));
        Assert.False(Directory.Exists(current.CachePath));
        Assert.Equal(existed, File.Exists(locator));
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(locator));
        if (existed) Assert.Equal(content, await File.ReadAllTextAsync(locator));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAsync_SameLocations_ShouldNotPlanMigrationOrRewriteLocatorOnStartup(bool equivalentPaths)
    {
        var current = _service.Current;
        var requested = equivalentPaths ? current with
        {
            AppDataPath = Path.Combine(current.AppDataPath, "."),
            ModsPath = current.ModsPath + Path.DirectorySeparatorChar,
            CachePath = current.CachePath.ToUpperInvariant()
        } : current;

        await _service.SaveAsync(requested);
        Assert.False(_service.HasPendingMigration);
        Assert.False(_service.RestartRequired);
        Assert.Null(_store.State.Pending);
        Assert.Null(_store.State.MigrationId);
        var locator = Path.Combine(_store.BootstrapPath, "storage-locations.json");
        var lastWrite = File.GetLastWriteTimeUtc(locator);
        var reopened = new StorageSettingsService(new StorageLocationStore(_store.BootstrapPath));

        await reopened.InitializeAsync();

        Assert.False(reopened.HasPendingMigration);
        Assert.Null(reopened.StartupMessage);
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(locator));
        Assert.False(Directory.Exists(current.ModsPath));
        Assert.False(Directory.Exists(current.CachePath));
    }

    [Fact]
    public async Task SaveAsync_ShouldOnlyPlanAndCancelPendingWithoutMovingFiles()
    {
        var path = await WriteAsync(_service.Current.ModsPath, "test.patch_0", "mod");
        await _service.SaveAsync(Next);
        Assert.True(_service.RestartRequired);
        Assert.True(File.Exists(path));
        Assert.False(Directory.Exists(Next.ModsPath));
        var reopened = new StorageLocationStore(_store.BootstrapPath);
        Assert.Equal(Next, reopened.State.Pending);
        Assert.Equal(_store.Defaults, reopened.State.Current);
        await _service.SaveAsync(_service.Current);
        Assert.False(_service.RestartRequired);
        Assert.False(_service.HasPendingMigration);
        Assert.Null(_store.State.MigrationId);
        await _service.InitializeAsync();
        Assert.Equal("mod", await File.ReadAllTextAsync(path));
        Assert.False(Directory.Exists(Next.ModsPath));
    }

    [Fact]
    public async Task InitializeAsync_ReadyToCommit_ShouldResumeConfirmedPlanWithoutRecopying()
    {
        var old = _service.Current;
        var source = await WriteAsync(old.ModsPath, "one.patch_0", "source");
        await _service.SaveAsync(Next);
        var target = await WriteAsync(Next.ModsPath, "one.patch_0", "prepared-copy");
        await WriteAsync(Next.ModsPath, ".hd2mimnt-migration", _store.State.MigrationId!);
        await _store.SaveAsync(_store.State with
        {
            ReadyToCommit = true,
            Moves = [new(old.ModsPath, Next.ModsPath)],
            CleanupFiles = [source]
        });
        var reopened = new StorageSettingsService(new StorageLocationStore(_store.BootstrapPath));
        Assert.True(reopened.HasPendingMigration);

        await reopened.InitializeAsync();

        Assert.Equal(Next, reopened.Current);
        Assert.False(reopened.HasPendingMigration);
        Assert.Null(reopened.StartupMessage);
        Assert.Equal("prepared-copy", await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(source));
        Assert.False(File.Exists(Path.Combine(Next.ModsPath, ".hd2mimnt-migration")));
    }

    [Fact]
    public async Task InitializeAsync_CommittedCleanup_ShouldRetryOldFilesWithoutMigratingCaches()
    {
        var source = await WriteAsync(_service.Current.ModsPath, "one.patch_0", "old-copy");
        var legacy = await WriteAsync(Next.AppDataPath, "ImageCache/a.png", "legacy");
        var id = Guid.NewGuid().ToString("N");
        var marker = await WriteAsync(Next.ModsPath, ".hd2mimnt-migration", id);
        await _store.SaveAsync(new(Next)
        {
            MigrationId = id,
            Moves = [new(_service.Current.ModsPath, Next.ModsPath)],
            CleanupFiles = [source]
        });
        Assert.False(_service.HasPendingMigration);

        using (var locked = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
            await _service.InitializeAsync();

        Assert.Contains("部分旧文件未能删除", _service.StartupMessage);
        Assert.False(File.Exists(marker));
        Assert.True(File.Exists(source));
        var reopened = new StorageSettingsService(new StorageLocationStore(_store.BootstrapPath));
        await reopened.InitializeAsync();

        Assert.Null(reopened.StartupMessage);
        Assert.False(reopened.HasPendingMigration);
        Assert.False(File.Exists(source));
        Assert.Equal("legacy", await File.ReadAllTextAsync(legacy));
        Assert.False(Directory.Exists(Next.CachePath));
        Assert.Empty(new StorageLocationStore(_store.BootstrapPath).State.CleanupFiles);
    }

    [Fact]
    public async Task InitializeAsync_ShouldMigrateAllFilesAndPathReferencesWithoutChangingIds()
    {
        var old = _service.Current;
        await SeedDatabaseAsync(old);
        await WriteAsync(old.ModsPath, "one/mod.patch_0", "mod");
        await WriteAsync(old.ModsPath, "thumbnails/one.png", "image");
        await WriteAsync(old.CachePath, "Downloads/one.part", "checkpoint");
        await WriteAsync(old.AppDataPath, "Backups/old.zip", "backup");
        await new SettingsService(old.AppDataPath).SetSettingAsync(SettingKeys.BackgroundImagePath, Path.Combine(old.ModsPath, "thumbnails/one.png"));
        await _service.SaveAsync(Next);
        await _service.InitializeAsync();

        Assert.Null(_service.StartupMessage);
        Assert.Equal(Next, _service.Current);
        Assert.False(_service.RestartRequired);
        Assert.Equal("mod", await File.ReadAllTextAsync(Path.Combine(Next.ModsPath, "one/mod.patch_0")));
        Assert.Equal("checkpoint", await File.ReadAllTextAsync(Path.Combine(Next.CachePath, "Downloads/one.part")));
        Assert.True(File.Exists(Path.Combine(Next.AppDataPath, "Backups/old.zip")));
        Assert.Equal(Path.Combine(Next.ModsPath, "one"), await ReadAsync(Next.DatabasePath, "SELECT Path FROM Mods WHERE Id='one'"));
        Assert.Equal(Path.GetFullPath(Path.Combine(Next.ModsPath, "thumbnails/one.png")), await ReadAsync(Next.DatabasePath, "SELECT ThumbnailPath FROM Mods WHERE Id='one'"));
        Assert.Equal(Path.GetFullPath(Path.Combine(Next.ModsPath, "one/mod.patch_0")), await ReadAsync(Next.DatabasePath, "SELECT SourcePath FROM DeploymentFiles"));
        Assert.Equal(Path.Combine(_root, "game/mod.patch_0"), await ReadAsync(Next.DatabasePath, "SELECT TargetPath FROM DeploymentFiles"));
        Assert.Equal(old.AppDataPath + "-other", await ReadAsync(Next.DatabasePath, "SELECT Path FROM Mods WHERE Id='outside'"));
        Assert.Equal(Path.GetFullPath(Path.Combine(Next.ModsPath, "thumbnails/one.png")), new SettingsService(Next.AppDataPath).GetSetting(SettingKeys.BackgroundImagePath, ""));
        Assert.False(File.Exists(old.DatabasePath));
        Assert.False(Directory.Exists(old.ModsPath));
        Assert.Equal(new[] { "storage-locations.json" }, Directory.GetFiles(_store.BootstrapPath).Select(Path.GetFileName));
        Assert.Empty(Directory.EnumerateFiles(_root, ".hd2mimnt-migration", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateDirectories(_root, ".migration-*", SearchOption.AllDirectories));
        Assert.Equal(Next, new StorageLocationStore(_store.BootstrapPath).State.Current);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InitializeAsync_ShouldMoveAppDataAndLibraryIndependently(bool appDataOnly)
    {
        var old = _service.Current;
        await SeedDatabaseAsync(old);
        await WriteAsync(old.ModsPath, "one/mod.patch_0", "mod");
        await WriteAsync(old.CachePath, "ImageCache/a.png", "cache");
        var target = appDataOnly ? old with { AppDataPath = Next.AppDataPath } : old with { ModsPath = Next.ModsPath };
        await _service.SaveAsync(target);
        await _service.InitializeAsync();

        Assert.Null(_service.StartupMessage);
        Assert.Equal(Path.Combine(target.ModsPath, "one"), await ReadAsync(target.DatabasePath, "SELECT Path FROM Mods WHERE Id='one'"));
        Assert.True(File.Exists(Path.Combine(target.ModsPath, "one/mod.patch_0")));
        Assert.True(File.Exists(Path.Combine(old.CachePath, "ImageCache/a.png")));
        Assert.False(File.Exists(Path.Combine(appDataOnly ? old.AppDataPath : old.ModsPath, appDataOnly ? "darktidemimnt.db" : "one/mod.patch_0")));
    }

    [Fact]
    public async Task InitializeAsync_ShouldMigrateLegacyCachesBeforeMovingRoots()
    {
        var old = _service.Current;
        await WriteAsync(old.AppDataPath, "ImageCache/a.png", "cache");
        await WriteAsync(old.AppDataPath, "Downloads/a.part", "resume");
        await _service.SaveAsync(Next);
        await _service.InitializeAsync();
        Assert.Null(_service.StartupMessage);
        Assert.Equal("cache", await File.ReadAllTextAsync(Path.Combine(Next.ImageCachePath, "a.png")));
        Assert.Equal("resume", await File.ReadAllTextAsync(Path.Combine(Next.DownloadsPath, "a.part")));
        Assert.False(Directory.Exists(old.CachePath));
    }

    [Fact]
    public async Task InitializeAsync_DestinationOccupiedAfterSave_ShouldKeepOriginalAndUnknownFiles()
    {
        var old = _service.Current;
        var mod = await WriteAsync(old.ModsPath, "one.patch_0", "original");
        await _service.SaveAsync(Next);
        var unknown = await WriteAsync(Next.ModsPath, "unknown.txt", "user");
        await _service.InitializeAsync();
        Assert.Contains("迁移失败", _service.StartupMessage);
        Assert.Equal(old, _service.Current);
        Assert.Equal("original", await File.ReadAllTextAsync(mod));
        Assert.Equal("user", await File.ReadAllTextAsync(unknown));
        Assert.True(_service.RestartRequired);
    }

    [Fact]
    public async Task InitializeAsync_InterruptedCopy_ShouldDiscardOwnedPartialFilesAndRetry()
    {
        await WriteAsync(_service.Current.ModsPath, "one.patch_0", "complete");
        await _service.SaveAsync(Next);
        await WriteAsync(Next.ModsPath, ".hd2mimnt-migration", _store.State.MigrationId!);
        await WriteAsync(Next.ModsPath, "one.patch_0", "partial");
        await _service.InitializeAsync();
        Assert.Null(_service.StartupMessage);
        Assert.Equal("complete", await File.ReadAllTextAsync(Path.Combine(Next.ModsPath, "one.patch_0")));
    }

    [Fact]
    public async Task InitializeAsync_CopyFailure_ShouldDeleteCopiesAndRetainSourceDatabase()
    {
        var old = _service.Current;
        await SeedDatabaseAsync(old);
        var mod = await WriteAsync(old.ModsPath, "one.patch_0", "original");
        await _service.SaveAsync(Next);
        using (var locked = new FileStream(mod, FileMode.Open, FileAccess.Read, FileShare.None))
            await _service.InitializeAsync();
        Assert.Contains("迁移失败", _service.StartupMessage);
        Assert.Equal(old, _service.Current);
        Assert.True(File.Exists(old.DatabasePath));
        Assert.False(Directory.Exists(Next.AppDataPath));
        Assert.False(Directory.Exists(Next.ModsPath));
        Assert.False(Directory.Exists(Next.CachePath));
    }

    [Fact]
    public async Task InitializeAsync_CanceledBeforeStart_ShouldRetainPendingAndSources()
    {
        var mod = await WriteAsync(_service.Current.ModsPath, "one.patch_0", "original");
        await _service.SaveAsync(Next);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.InitializeAsync(new CancellationToken(true)));
        Assert.True(File.Exists(mod));
        Assert.True(_service.RestartRequired);
        Assert.False(Directory.Exists(Next.ModsPath));
    }

    [Fact]
    public async Task SaveAsync_ShouldRejectOverlappingAndNonEmptyTargets()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SaveAsync(Next with { CachePath = Next.ModsPath }));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SaveAsync(Next with { ModsPath = Path.Combine(_service.Current.ModsPath, "nested") }));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SaveAsync(Next with { AppDataPath = "relative-folder" }));
        await WriteAsync(Next.ModsPath, "user.txt", "user");
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SaveAsync(Next));
        Assert.False(_service.RestartRequired);
    }

    [Fact]
    public async Task SaveAsync_ShouldRejectLibraryMoveWithSymbolicLinkDeployments()
    {
        await SeedDatabaseAsync(_service.Current, "SymbolicLink");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SaveAsync(Next));
        Assert.Contains("清理部署", ex.Message);
        Assert.False(_service.RestartRequired);
        // 数据目录变更不会改变链接源位置。
        await _service.SaveAsync(_service.Current with { AppDataPath = Next.AppDataPath });
        Assert.True(_service.RestartRequired);
    }

    private async Task SeedDatabaseAsync(StorageLocations locations, string method = "Copy")
    {
        Directory.CreateDirectory(locations.AppDataPath);
        using (var context = new HD2MimntDbContext(locations.DatabasePath))
            await context.InitializeDatabaseAsync();
        SqliteConnection.ClearAllPools();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = locations.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Mods (Id, Name, Version, Path, ImportedAt, ModifiedAt, ThumbnailPath)
            VALUES ('one','one','1',$mod,'2026-10-12','2026-10-12',$image), ('outside','outside','1',$outside,'2026-10-12','2026-10-12','');
            INSERT INTO Profiles (Id, Name, CreatedAt, ModifiedAt, IsActive) VALUES ('profile','profile','2026-10-12','2026-10-12',1);
            INSERT INTO DeploymentRecords (Id, ProfileId, ModId, SessionId, DeploymentMethod, StartTime, Status)
            VALUES ('deployment','profile','one','session',$method,'2026-10-12','Completed');
            INSERT INTO DeploymentFiles (Id, DeploymentId, ModId, SourcePath, TargetPath, Method)
            VALUES ('file','deployment','one',$source,$target,$method);
            """;
        command.Parameters.AddWithValue("$mod", Path.Combine(locations.ModsPath, "one"));
        command.Parameters.AddWithValue("$image", Path.Combine(locations.ModsPath, "thumbnails/one.png"));
        command.Parameters.AddWithValue("$outside", locations.AppDataPath + "-other");
        command.Parameters.AddWithValue("$source", Path.Combine(locations.ModsPath, "one/mod.patch_0"));
        command.Parameters.AddWithValue("$target", Path.Combine(_root, "game/mod.patch_0"));
        command.Parameters.AddWithValue("$method", method);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> WriteAsync(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        return path;
    }
    private static async Task<string> ReadAsync(string database, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync())!;
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
