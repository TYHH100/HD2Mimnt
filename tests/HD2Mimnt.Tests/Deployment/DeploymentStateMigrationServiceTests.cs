using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HD2Mimnt.Tests.Deployment;

public class DeploymentStateMigrationServiceTests : IDisposable
{
    private readonly HD2MimntDbContext _dbContext;
    private readonly DeploymentRepository _repository;
    private readonly DeploymentStateMigrationService _migration;
    private readonly string _dbPath;
    private readonly string _gameRoot = Path.Combine(Path.GetTempPath(), $"deploy_game_{Guid.NewGuid():N}");
    private readonly Guid _profileId = Guid.NewGuid();
    private readonly Guid _modId = Guid.NewGuid();

    public DeploymentStateMigrationServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"deploy_state_{Guid.NewGuid():N}.db");
        _dbContext = new HD2MimntDbContext(_dbPath);
        _dbContext.InitializeDatabaseAsync().Wait();
        _repository = new DeploymentRepository(_dbContext);
        _migration = new DeploymentStateMigrationService(_dbContext);
        InsertPrerequisites();
    }

    private void InsertPrerequisites()
    {
        using var connection = _dbContext.OpenConnectionAsync().GetAwaiter().GetResult();
        using (var profile = connection.CreateCommand())
        {
            profile.CommandText = @"
                INSERT INTO Profiles (Id, Name, CreatedAt, ModifiedAt, IsActive)
                VALUES (@Id, @Name, @CreatedAt, @ModifiedAt, 0)";
            profile.Parameters.AddWithValue("@Id", _profileId.ToString());
            profile.Parameters.AddWithValue("@Name", "Migration Profile");
            profile.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow.ToString("O"));
            profile.Parameters.AddWithValue("@ModifiedAt", DateTime.UtcNow.ToString("O"));
            profile.ExecuteNonQuery();
        }
        using var mod = connection.CreateCommand();
        mod.CommandText = @"
            INSERT INTO Mods (Id, Name, Version, Path, ImportedAt, ModifiedAt)
            VALUES (@Id, @Name, @Version, @Path, @ImportedAt, @ModifiedAt)";
        mod.Parameters.AddWithValue("@Id", _modId.ToString());
        mod.Parameters.AddWithValue("@Name", "Migration Mod");
        mod.Parameters.AddWithValue("@Version", "1.0.0");
        mod.Parameters.AddWithValue("@Path", @"C:\mods\migration");
        mod.Parameters.AddWithValue("@ImportedAt", DateTime.UtcNow.ToString("O"));
        mod.Parameters.AddWithValue("@ModifiedAt", DateTime.UtcNow.ToString("O"));
        mod.ExecuteNonQuery();
    }

    [Fact]
    public async Task EnsureMigrated_AdoptsPatchTargetRoot()
    {
        var record = CreateRecord(_modId, DateTime.UtcNow, PatchTarget("0123456789abcdef.patch_0"));

        var result = await _migration.EnsureMigratedAsync();

        var current = Assert.Single(await _repository.GetCurrentDeploymentsAsync(_gameRoot));
        Assert.Equal(record.Id, current.Id);
        Assert.Equal(_gameRoot, current.GamePath, ignoreCase: true);
        Assert.All(current.Files, file => Assert.Equal(_gameRoot, file.GamePath, ignoreCase: true));
        Assert.Equal(1, result.AdoptedRecords);
    }

    [Fact]
    public async Task EnsureMigrated_AdoptsNativeTargetRoot()
    {
        CreateRecord(_modId, DateTime.UtcNow, Path.Combine(_gameRoot, "bin", "HD2IndependentRig", "mod.rigbin"));

        await _migration.EnsureMigratedAsync();

        var current = Assert.Single(await _repository.GetCurrentDeploymentsAsync(_gameRoot));
        Assert.Equal(_gameRoot, current.GamePath, ignoreCase: true);
    }

    [Fact]
    public async Task EnsureMigrated_MergesDuplicateSessionsAndKeepsNewestRecord()
    {
        var older = CreateRecord(_modId, DateTime.UtcNow.AddHours(-2), PatchTarget("aaaaaaaaaaaaaaaa.patch_0"),
            PatchTarget("aaaaaaaaaaaaaaaa.patch_1"));
        var newer = CreateRecord(_modId, DateTime.UtcNow, PatchTarget("aaaaaaaaaaaaaaaa.patch_0"),
            PatchTarget("aaaaaaaaaaaaaaaa.patch_2"));

        var result = await _migration.EnsureMigratedAsync();

        var current = Assert.Single(await _repository.GetCurrentDeploymentsAsync(_gameRoot));
        Assert.Equal(newer.Id, current.Id);
        Assert.Equal(3, current.Files.Count);
        Assert.Equal(3, current.Files.Select(file => file.TargetPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Empty(await _repository.GetBySessionIdAsync(older.SessionId));
        Assert.Equal(1, result.MergedRecords);
    }

    [Fact]
    public async Task EnsureMigrated_RemovesOrphanDeploymentFiles()
    {
        CreateRecord(_modId, DateTime.UtcNow, PatchTarget("bbbbbbbbbbbbbbbb.patch_0"));
        // 旧库可能留下孤儿行；当前连接默认启用外键，测试用关闭外键的独立连接模拟历史数据。
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                     {
                         DataSource = _dbPath, ForeignKeys = false, Pooling = false
                     }.ToString()))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO DeploymentFiles (Id, DeploymentId, ModId, SourcePath, TargetPath, Method)
                VALUES (@Id, @DeploymentId, @ModId, @SourcePath, @TargetPath, 'Copy')";
            command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("@DeploymentId", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("@ModId", _modId.ToString());
            command.Parameters.AddWithValue("@SourcePath", "orphan-source");
            command.Parameters.AddWithValue("@TargetPath", PatchTarget("cccccccccccccccc.patch_0"));
            await command.ExecuteNonQueryAsync();
        }

        var result = await _migration.EnsureMigratedAsync();

        await using var verify = await _dbContext.OpenConnectionAsync();
        using var check = verify.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM DeploymentFiles WHERE DeploymentId NOT IN (SELECT Id FROM DeploymentRecords)";
        Assert.Equal(0L, (long)(await check.ExecuteScalarAsync())!);
        Assert.Equal(1, result.RemovedOrphanFiles);
    }

    [Fact]
    public async Task EnsureMigrated_LeavesMixedInstallRootsUnassigned()
    {
        var otherRoot = Path.Combine(Path.GetTempPath(), $"deploy_other_{Guid.NewGuid():N}");
        var record = CreateRecord(_modId, DateTime.UtcNow,
            PatchTarget("dddddddddddddddd.patch_0"),
            Path.Combine(otherRoot, "data", "eeeeeeeeeeeeeeee.patch_0"));

        var result = await _migration.EnsureMigratedAsync();

        Assert.Null((await _repository.GetByIdAsync(record.Id))!.GamePath);
        Assert.Equal(1, result.AmbiguousRecords);
        Assert.Empty(await _repository.GetCurrentDeploymentsAsync(_gameRoot));
    }

    [Fact]
    public async Task EnsureMigrated_UsesEffectiveGamePathForUninferrableTarget()
    {
        var target = Path.Combine(_gameRoot, "custom", "unknown.bin");
        CreateRecord(_modId, DateTime.UtcNow, target);

        await _migration.EnsureMigratedAsync(_gameRoot);

        var current = Assert.Single(await _repository.GetCurrentDeploymentsAsync(_gameRoot));
        Assert.Equal(_gameRoot, current.GamePath, ignoreCase: true);
    }

    private string PatchTarget(string fileName) => Path.Combine(_gameRoot, "data", fileName);

    private DeploymentRecord CreateRecord(Guid modId, DateTime startTime, params string[] targets)
    {
        var record = new DeploymentRecord
        {
            Id = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            ProfileId = _profileId,
            ModId = modId,
            Workflow = "Default",
            DeploymentMethod = "Copy",
            StartTime = startTime,
            Status = "Completed"
        };
        record.Files = targets.Select((target, index) => new DeploymentFile
        {
            Id = Guid.NewGuid(),
            DeploymentId = record.Id,
            ModId = modId,
            SourcePath = $@"C:\mods\{modId:N}\file{index}.pak",
            TargetPath = target,
            Method = "Copy"
        }).ToList();
        _repository.AddAsync(record).GetAwaiter().GetResult();
        return record;
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
