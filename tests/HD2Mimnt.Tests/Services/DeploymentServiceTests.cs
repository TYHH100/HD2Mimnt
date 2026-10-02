using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Tests.Services;

public class DeploymentServiceTests : IDisposable
{
    private readonly HD2MimntDbContext _dbContext;
    private readonly IModRepository _modRepository;
    private readonly IProfileRepository _profileRepository;
    private readonly IDeploymentRepository _deploymentRepository;
    private readonly DeploymentService _deploymentService;
    private readonly string _testDbPath;

    public DeploymentServiceTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_deployment_{Guid.NewGuid()}.db");
        _dbContext = new HD2MimntDbContext(_testDbPath);
        _dbContext.InitializeDatabaseAsync().Wait();

        _modRepository = new ModRepository(_dbContext);
        _profileRepository = new ProfileRepository(_dbContext);
        _deploymentRepository = new DeploymentRepository(_dbContext);

        // 创建 DeploymentService（不使用实际的 DeploymentMethod 实现）
        _deploymentService = new DeploymentService(
            _profileRepository,
            _modRepository,
            _deploymentRepository,
            Enumerable.Empty<IDeploymentMethod>());
    }

    [Fact]
    public async Task CreateDeploymentRecord_Should_Create_Successfully()
    {
        // Arrange
        var profile = await CreateTestProfile();
        var mod = await CreateTestMod();
        var sessionId = Guid.NewGuid();

        // Act
        var record = await _deploymentService.CreateDeploymentRecordAsync(
            sessionId,
            profile.Id,
            mod.Id,
            DeploymentMethod.Copy,
            "TestWorkflow");

        // Assert
        Assert.NotEqual(Guid.Empty, record.Id);
        Assert.Equal(sessionId, record.SessionId);
        Assert.Equal(profile.Id, record.ProfileId);
        Assert.Equal(mod.Id, record.ModId);
        Assert.Equal("Copy", record.DeploymentMethod);
        Assert.Equal("TestWorkflow", record.Workflow);
        Assert.Equal(DeploymentStatus.Pending.ToString(), record.Status);
    }

    [Fact]
    public async Task UpdateDeploymentStatus_Should_Update_Successfully()
    {
        // Arrange
        var profile = await CreateTestProfile();
        var mod = await CreateTestMod();
        var sessionId = Guid.NewGuid();
        var record = await _deploymentService.CreateDeploymentRecordAsync(
            sessionId, profile.Id, mod.Id, DeploymentMethod.Copy);

        // Act
        var endTime = DateTime.UtcNow;
        await _deploymentService.UpdateDeploymentStatusAsync(
            record.Id,
            DeploymentStatus.Completed,
            endTime);

        // Assert
        var updated = await _deploymentRepository.GetByIdAsync(record.Id);
        Assert.NotNull(updated);
        Assert.Equal(DeploymentStatus.Completed.ToString(), updated.Status);
        Assert.NotNull(updated.EndTime);
    }

    [Fact]
    public async Task AddDeploymentFile_Should_Add_Successfully()
    {
        // Arrange
        var profile = await CreateTestProfile();
        var mod = await CreateTestMod();
        var sessionId = Guid.NewGuid();
        var record = await _deploymentService.CreateDeploymentRecordAsync(
            sessionId, profile.Id, mod.Id, DeploymentMethod.Copy);

        // Act
        await _deploymentService.AddDeploymentFileAsync(
            record.Id,
            mod.Id,
            "C:\\Source\\file.pak",
            "C:\\Game\\file.pak",
            DeploymentMethod.Copy);

        // Assert
        var files = await _deploymentService.GetDeploymentFilesAsync(record.Id);
        Assert.Single(files);
        var file = files.First();
        Assert.Equal("C:\\Source\\file.pak", file.SourcePath);
        Assert.Equal("C:\\Game\\file.pak", file.TargetPath);
    }

    [Fact]
    public async Task AddDeploymentFiles_Should_Add_Multiple_Files()
    {
        // Arrange
        var profile = await CreateTestProfile();
        var mod = await CreateTestMod();
        var sessionId = Guid.NewGuid();
        var record = await _deploymentService.CreateDeploymentRecordAsync(
            sessionId, profile.Id, mod.Id, DeploymentMethod.Copy);

        var files = new[]
        {
            ("C:\\Source\\file1.pak", "C:\\Game\\file1.pak"),
            ("C:\\Source\\file2.pak", "C:\\Game\\file2.pak"),
            ("C:\\Source\\file3.pak", "C:\\Game\\file3.pak")
        };

        // Act
        await _deploymentService.AddDeploymentFilesAsync(
            record.Id,
            mod.Id,
            files,
            DeploymentMethod.Copy);

        // Assert
        var deployedFiles = await _deploymentService.GetDeploymentFilesAsync(record.Id);
        Assert.Equal(3, deployedFiles.Count());
    }

    [Fact]
    public async Task GetLatestDeployments_Should_Return_Latest_Session()
    {
        // Arrange
        var profile = await CreateTestProfile();
        var mod1 = await CreateTestMod("Mod1");
        var mod2 = await CreateTestMod("Mod2");

        var session1 = Guid.NewGuid();
        var session2 = Guid.NewGuid();

        await _deploymentService.CreateDeploymentRecordAsync(session1, profile.Id, mod1.Id, DeploymentMethod.Copy);
        await Task.Delay(100); // Ensure different timestamps
        await _deploymentService.CreateDeploymentRecordAsync(session2, profile.Id, mod2.Id, DeploymentMethod.Copy);

        // Act
        var latestSessionId = await _deploymentService.GetLatestDeploymentSessionIdAsync();
        var latestDeployments = await _deploymentService.GetLatestDeploymentsAsync();

        // Assert
        Assert.Equal(session2, latestSessionId);
        Assert.Single(latestDeployments);
        Assert.Equal(mod2.Id, latestDeployments.First().ModId);
    }

    [Fact]
    public async Task GetModDeploymentHistory_Should_Return_Mod_Records()
    {
        // Arrange
        var profile = await CreateTestProfile();
        var mod = await CreateTestMod();

        var session1 = Guid.NewGuid();
        var session2 = Guid.NewGuid();

        await _deploymentService.CreateDeploymentRecordAsync(session1, profile.Id, mod.Id, DeploymentMethod.Copy);
        await _deploymentService.CreateDeploymentRecordAsync(session2, profile.Id, mod.Id, DeploymentMethod.SymbolicLink);

        // Act
        var history = await _deploymentService.GetModDeploymentHistoryAsync(mod.Id);

        // Assert
        Assert.Equal(2, history.Count());
    }

    [Fact]
    public async Task CleanupDeploymentRecords_Should_Delete_Session_Records()
    {
        // Arrange
        var profile = await CreateTestProfile();
        var mod = await CreateTestMod();
        var sessionId = Guid.NewGuid();

        await _deploymentService.CreateDeploymentRecordAsync(sessionId, profile.Id, mod.Id, DeploymentMethod.Copy);

        // Act
        await _deploymentService.CleanupDeploymentRecordsAsync(sessionId);

        // Assert
        var records = await _deploymentRepository.GetBySessionIdAsync(sessionId);
        Assert.Empty(records);
    }

    [Fact]
    public async Task DeleteModDeploymentRecords_Should_Delete_All_Mod_Records()
    {
        // Arrange
        var profile = await CreateTestProfile();
        var mod = await CreateTestMod();

        var session1 = Guid.NewGuid();
        var session2 = Guid.NewGuid();

        await _deploymentService.CreateDeploymentRecordAsync(session1, profile.Id, mod.Id, DeploymentMethod.Copy);
        await _deploymentService.CreateDeploymentRecordAsync(session2, profile.Id, mod.Id, DeploymentMethod.Copy);

        // Act
        await _deploymentService.DeleteModDeploymentRecordsAsync(mod.Id);

        // Assert
        var records = await _deploymentRepository.GetByModIdAsync(mod.Id);
        Assert.Empty(records);
    }

    [Fact]
    public async Task GetDeploymentFileByTargetPath_Should_Find_File()
    {
        // Arrange
        var profile = await CreateTestProfile();
        var mod = await CreateTestMod();
        var sessionId = Guid.NewGuid();
        var record = await _deploymentService.CreateDeploymentRecordAsync(
            sessionId, profile.Id, mod.Id, DeploymentMethod.Copy);

        var targetPath = "C:\\Game\\test.pak";
        await _deploymentService.AddDeploymentFileAsync(
            record.Id, mod.Id, "C:\\Source\\test.pak", targetPath, DeploymentMethod.Copy);

        // Act
        var file = await _deploymentService.GetDeploymentFileByTargetPathAsync(targetPath);

        // Assert
        Assert.NotNull(file);
        Assert.Equal(targetPath, file.TargetPath);
    }

    private async Task<Profile> CreateTestProfile(string name = "Test Profile")
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = "Test",
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            IsActive = false
        };
        await _profileRepository.AddAsync(profile);
        return profile;
    }

    private async Task<Mod> CreateTestMod(string name = "Test Mod")
    {
        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = name,
            Version = "1.0.0",
            Author = "Test",
            Description = "Test",
            Path = "/test/path",
            Category = ModCategory.Gameplay,
            Tags = new List<string> { "test" },
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            ImportedAt = DateTime.UtcNow
        };
        await _modRepository.AddAsync(mod);
        return mod;
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        if (File.Exists(_testDbPath))
        {
            File.Delete(_testDbPath);
        }
    }
}
