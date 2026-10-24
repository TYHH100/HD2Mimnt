using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HD2Mimnt.Tests.Repositories;

public class DeploymentRepositoryTests : IDisposable
{
    private readonly HD2MimntDbContext _dbContext;
    private readonly DeploymentRepository _repository;
    private readonly string _testDbPath;
    private readonly Guid _profileId = Guid.NewGuid();
    private readonly Guid _modId = Guid.NewGuid();

    public DeploymentRepositoryTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_deployment_{Guid.NewGuid()}.db");
        _dbContext = new HD2MimntDbContext(_testDbPath);
        _dbContext.InitializeDatabaseAsync().Wait();
        _repository = new DeploymentRepository(_dbContext);
        InsertPrerequisiteRows();
    }

    /// <summary>DeploymentRecords/DeploymentFiles 的外键要求 Profiles 与 Mods 中存在对应行。</summary>
    private void InsertPrerequisiteRows()
    {
        using var connection = _dbContext.OpenConnectionAsync().GetAwaiter().GetResult();
        using var profileCommand = connection.CreateCommand();
        profileCommand.CommandText = @"
            INSERT INTO Profiles (Id, Name, CreatedAt, ModifiedAt, IsActive)
            VALUES (@Id, @Name, @CreatedAt, @ModifiedAt, 0)";
        profileCommand.Parameters.AddWithValue("@Id", _profileId.ToString());
        profileCommand.Parameters.AddWithValue("@Name", "Test Profile");
        profileCommand.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow.ToString("O"));
        profileCommand.Parameters.AddWithValue("@ModifiedAt", DateTime.UtcNow.ToString("O"));
        profileCommand.ExecuteNonQuery();

        using var modCommand = connection.CreateCommand();
        modCommand.CommandText = @"
            INSERT INTO Mods (Id, Name, Version, Path, ImportedAt, ModifiedAt)
            VALUES (@Id, @Name, @Version, @Path, @ImportedAt, @ModifiedAt)";
        modCommand.Parameters.AddWithValue("@Id", _modId.ToString());
        modCommand.Parameters.AddWithValue("@Name", "Test Mod");
        modCommand.Parameters.AddWithValue("@Version", "1.0.0");
        modCommand.Parameters.AddWithValue("@Path", @"C:\mods\test");
        modCommand.Parameters.AddWithValue("@ImportedAt", DateTime.UtcNow.ToString("O"));
        modCommand.Parameters.AddWithValue("@ModifiedAt", DateTime.UtcNow.ToString("O"));
        modCommand.ExecuteNonQuery();
    }

    [Fact]
    public async Task GetBySessionIdAsync_LoadsFilesForAllRecords()
    {
        var sessionId = Guid.NewGuid();
        await _repository.AddAsync(CreateRecord(sessionId, modNumber: 1, fileCount: 2));
        await _repository.AddAsync(CreateRecord(sessionId, modNumber: 2, fileCount: 1));
        await _repository.AddAsync(CreateRecord(Guid.NewGuid(), modNumber: 3, fileCount: 3));

        var records = await _repository.GetBySessionIdAsync(sessionId);

        Assert.Equal(2, records.Count());
        foreach (var record in records)
        {
            Assert.NotEmpty(record.Files);
            Assert.All(record.Files, file => Assert.Equal(record.Id, file.DeploymentId));
        }
        Assert.Equal(2, records.ElementAt(0).Files.Count);
        Assert.Equal(1, records.ElementAt(1).Files.Count);
    }

    [Fact]
    public async Task GetByModIdAsync_LoadsFiles()
    {
        var sessionId = Guid.NewGuid();
        var record = CreateRecord(sessionId, modNumber: 7, fileCount: 2);
        await _repository.AddAsync(record);

        var results = await _repository.GetByModIdAsync(record.ModId);

        var result = Assert.Single(results);
        Assert.Equal(2, result.Files.Count);
        Assert.All(result.Files, file => Assert.Equal(record.Id, file.DeploymentId));
    }

    [Fact]
    public async Task GetLatestDeploymentsAsync_ReturnsLatestSessionWithFiles()
    {
        var earlierSession = Guid.NewGuid();
        var laterSession = Guid.NewGuid();
        var earlier = CreateRecord(earlierSession, modNumber: 1, fileCount: 1);
        var later = CreateRecord(laterSession, modNumber: 2, fileCount: 2);
        later.StartTime = earlier.StartTime.AddMinutes(5);
        await _repository.AddAsync(earlier);
        await _repository.AddAsync(later);

        var latest = await _repository.GetLatestDeploymentsAsync();

        var record = Assert.Single(latest);
        Assert.Equal(laterSession, record.SessionId);
        Assert.Equal(2, record.Files.Count);
    }

    private DeploymentRecord CreateRecord(Guid sessionId, int modNumber, int fileCount)
    {
        var record = new DeploymentRecord
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            ProfileId = _profileId,
            ModId = _modId,
            Workflow = "Default",
            DeploymentMethod = "Copy",
            StartTime = DateTime.UtcNow.AddMinutes(-modNumber),
            Status = "Completed"
        };
        record.Files = Enumerable.Range(0, fileCount)
            .Select(i => new DeploymentFile
            {
                Id = Guid.NewGuid(),
                DeploymentId = record.Id,
                ModId = record.ModId,
                SourcePath = $@"store\mod{modNumber}\file{i}.pak",
                TargetPath = $@"data\mod{modNumber}\file{i}.pak",
                Method = "Copy"
            })
            .ToList();
        return record;
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        try
        {
            if (File.Exists(_testDbPath))
            {
                File.Delete(_testDbPath);
            }
        }
        catch
        {
            // 忽略清理错误
        }
    }
}
