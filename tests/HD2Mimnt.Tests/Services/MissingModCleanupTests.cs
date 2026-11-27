using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HD2Mimnt.Tests.Services;

public sealed class MissingModCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2MimntMissingModCleanup",
        Guid.NewGuid().ToString("N"));
    private readonly HD2MimntDbContext _dbContext;
    private readonly ModRepository _modRepository;
    private readonly ProfileRepository _profileRepository;
    private readonly DeploymentRepository _deploymentRepository;
    private readonly string _library;

    public MissingModCleanupTests()
    {
        Directory.CreateDirectory(_root);
        _library = Path.Combine(_root, "library");
        _dbContext = new HD2MimntDbContext(Path.Combine(_root, "hd2mimnt.db"));
        _dbContext.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _modRepository = new ModRepository(_dbContext);
        _profileRepository = new ProfileRepository(_dbContext);
        _deploymentRepository = new DeploymentRepository(_dbContext);
    }

    [Fact]
    public async Task GetMissingModsAsync_ReturnsOnlyRecordsWhoseDirectoriesDoNotExist()
    {
        var missing = await AddModAsync("Missing", Path.Combine(_library, "missing"));
        var blank = await AddModAsync("Blank", string.Empty);
        var existingPath = Path.Combine(_library, "existing");
        Directory.CreateDirectory(existingPath);
        var existing = await AddModAsync("Existing", existingPath);
        var service = CreateService();

        var result = await service.GetMissingModsAsync();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, mod => mod.Id == blank.Id);
        Assert.Contains(result, mod => mod.Id == missing.Id);
        Assert.DoesNotContain(result, mod => mod.Id == existing.Id);
    }

    [Fact]
    public async Task CleanupMissingModsAsync_RemovesMissingRecordsAndPreservesRestoredDirectories()
    {
        var first = await AddModAsync("First", Path.Combine(_library, "first"));
        var second = await AddModAsync("Second", Path.Combine(_library, "second"));
        var restoredPath = Path.Combine(_library, "restored");
        Directory.CreateDirectory(restoredPath);
        var restored = await AddModAsync("Restored", restoredPath);
        var sentinel = Path.Combine(restoredPath, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        var service = CreateService();
        var changes = 0;
        service.ModsChanged += (_, _) => changes++;

        var result = await service.CleanupMissingModsAsync(
            [first.Id, second.Id, restored.Id, Guid.NewGuid()]);

        Assert.Equal(2, result.RemovedCount);
        Assert.Equal(2, result.SkippedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Empty(result.Errors);
        Assert.Null(await _modRepository.GetByIdAsync(first.Id));
        Assert.Null(await _modRepository.GetByIdAsync(second.Id));
        Assert.NotNull(await _modRepository.GetByIdAsync(restored.Id));
        Assert.True(File.Exists(sentinel));
        Assert.Equal(1, changes);
        Assert.Empty(await service.GetMissingModsAsync());
    }

    [Fact]
    public async Task CleanupMissingModsAsync_CleansProfileAndDeploymentReferences()
    {
        var missingPath = Path.Combine(_library, "missing");
        var missing = await AddModAsync("Missing", missingPath);
        var keptPath = Path.Combine(_library, "kept");
        Directory.CreateDirectory(keptPath);
        var kept = await AddModAsync("Kept", keptPath);
        var profiles = new ProfileService(_profileRepository, _modRepository);
        var profileId = await profiles.CreateProfileAsync("Cleanup");
        await profiles.AddModToProfileAsync(profileId, missing.Id);
        await profiles.AddModToProfileAsync(profileId, kept.Id);
        var separator = await profiles.AddSeparatorAsync(profileId, "Group", 0);
        await profiles.SetSeparatorCollapsedAsync(profileId, separator.Id, true);

        var deployment = new DeploymentService(_profileRepository, _modRepository, _deploymentRepository,
            [new CopyDeploymentMethod()]);
        var record = await deployment.CreateDeploymentRecordAsync(
            Guid.NewGuid(), profileId, missing.Id, DeploymentMethod.Copy);
        var gamePath = Path.Combine(_root, "game");
        Directory.CreateDirectory(gamePath);
        var target = Path.Combine(gamePath, "missing.patch_0");
        await File.WriteAllTextAsync(target, "deployed");
        await deployment.AddDeploymentFileAsync(record.Id, missing.Id,
            Path.Combine(missingPath, "missing.patch_0"), target, DeploymentMethod.Copy);
        var service = CreateService(deployment, profiles);

        var result = await service.CleanupMissingModsAsync([missing.Id]);

        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Null(await _modRepository.GetByIdAsync(missing.Id));
        Assert.False(File.Exists(target));
        Assert.Empty(await _deploymentRepository.GetByModIdAsync(missing.Id));
        var profile = await _profileRepository.GetByIdAsync(profileId);
        Assert.NotNull(profile);
        Assert.Equal(kept.Id, Assert.Single(profile.Mods).ModId);
        var savedSeparator = Assert.Single(profile.Separators);
        Assert.Equal(0, savedSeparator.Order);
        Assert.Equal(1, savedSeparator.MemberCount);
    }

    [Fact]
    public async Task CleanupMissingModsAsync_PreservesLibraryFilesForMissingRecords()
    {
        var mod = await AddModAsync("Missing", Path.Combine(_library, "missing"));
        Directory.CreateDirectory(_library);
        var thumbnail = Path.Combine(_library, "recovery.png");
        await File.WriteAllTextAsync(thumbnail, "recovery");
        mod.ThumbnailPath = thumbnail;
        await _modRepository.UpdateAsync(mod);
        var service = CreateService();

        var result = await service.CleanupMissingModsAsync([mod.Id]);

        Assert.Equal(1, result.RemovedCount);
        Assert.Null(await _modRepository.GetByIdAsync(mod.Id));
        Assert.True(File.Exists(thumbnail));
    }

    [Fact]
    public async Task CleanupMissingModsAsync_ContinuesAfterFailureAndKeepsFailedRecord()
    {
        var blocked = await AddModAsync("Blocked", Path.Combine(_library, "blocked"));
        var removable = await AddModAsync("Removable", Path.Combine(_library, "removable"));
        using (var connection = await _dbContext.OpenConnectionAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "CREATE TRIGGER block_missing_mod_delete BEFORE DELETE ON Mods WHEN OLD.Name = 'Blocked' " +
                "BEGIN SELECT RAISE(ABORT, 'blocked'); END";
            await command.ExecuteNonQueryAsync();
        }
        var service = CreateService();

        var result = await service.CleanupMissingModsAsync([blocked.Id, removable.Id]);

        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains(result.Errors, error => error.StartsWith("Blocked:", StringComparison.Ordinal));
        Assert.NotNull(await _modRepository.GetByIdAsync(blocked.Id));
        Assert.Null(await _modRepository.GetByIdAsync(removable.Id));
    }

    private ModService CreateService(DeploymentService? deployment = null, ProfileService? profiles = null)
        => new(_modRepository, _library, deploymentService: deployment, profileService: profiles);

    private async Task<Mod> AddModAsync(string name, string path)
    {
        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = name,
            Version = "1.0",
            Path = path,
            ImportedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };
        await _modRepository.AddAsync(mod);
        return mod;
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
