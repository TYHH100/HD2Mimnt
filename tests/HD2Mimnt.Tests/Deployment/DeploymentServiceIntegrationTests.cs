using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.Repositories;
using Xunit;

namespace HD2Mimnt.Tests.Deployment;

/// <summary>
/// DeploymentService 集成测试
/// 测试 Phase 11 的功能：Pre-Deployment Cleanup 和 Profile Deployment
/// </summary>
public class DeploymentServiceIntegrationTests : IDisposable
{
    private readonly HD2MimntDbContext _dbContext;
    private readonly DeploymentService _deploymentService;
    private readonly ProfileRepository _profileRepository;
    private readonly ModRepository _modRepository;
    private readonly DeploymentRepository _deploymentRepository;
    private readonly string _testDbPath;
    private readonly string _testSourceDir;
    private readonly string _testTargetDir;

    public DeploymentServiceIntegrationTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_deployment_{Guid.NewGuid()}.db");
        _testSourceDir = Path.Combine(Path.GetTempPath(), $"test_source_{Guid.NewGuid()}");
        _testTargetDir = Path.Combine(Path.GetTempPath(), $"test_target_{Guid.NewGuid()}");

        Directory.CreateDirectory(_testSourceDir);
        Directory.CreateDirectory(_testTargetDir);

        _dbContext = new HD2MimntDbContext(_testDbPath);
        _dbContext.InitializeDatabaseAsync().Wait();

        _profileRepository = new ProfileRepository(_dbContext);
        _modRepository = new ModRepository(_dbContext);
        _deploymentRepository = new DeploymentRepository(_dbContext);

        var deploymentMethods = new List<HD2Mimnt.Core.Interfaces.IDeploymentMethod>
        {
            new CopyDeploymentMethod()
        };

        _deploymentService = new DeploymentService(
            _profileRepository,
            _modRepository,
            _deploymentRepository,
            deploymentMethods);
    }

    public void Dispose()
    {
        _dbContext?.Dispose();
        if (File.Exists(_testDbPath))
            File.Delete(_testDbPath);
        if (Directory.Exists(_testSourceDir))
            Directory.Delete(_testSourceDir, true);
        if (Directory.Exists(_testTargetDir))
            Directory.Delete(_testTargetDir, true);
    }

    [Fact]
    public async Task PreDeploymentCleanup_Should_Return_Zero_When_No_Previous_Deployment()
    {
        // Act
        var (filesDeleted, recordsDeleted) = await _deploymentService.PreDeploymentCleanupAsync();

        // Assert
        Assert.Equal(0, filesDeleted);
        Assert.Equal(0, recordsDeleted);
    }

    [Fact]
    public async Task PreDeploymentCleanup_Should_Delete_Previous_Deployment_Files()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Test Profile",
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };
        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod",
            Version = "1.0.0",
            Path = _testSourceDir,
            Category = ModCategory.Gameplay,
            ImportedAt = DateTime.UtcNow
        };

        await _profileRepository.AddAsync(profile);
        await _modRepository.AddAsync(mod);

        // Create test files
        var testFile1 = Path.Combine(_testTargetDir, "test1.txt");
        var testFile2 = Path.Combine(_testTargetDir, "test2.txt");
        await File.WriteAllTextAsync(testFile1, "test content 1");
        await File.WriteAllTextAsync(testFile2, "test content 2");

        // Create deployment record
        var record = await _deploymentService.CreateDeploymentRecordAsync(
            sessionId, profile.Id, mod.Id, DeploymentMethod.Copy);

        // Add deployment files
        await _deploymentService.AddDeploymentFileAsync(
            record.Id, mod.Id, "source1.txt", testFile1, DeploymentMethod.Copy);
        await _deploymentService.AddDeploymentFileAsync(
            record.Id, mod.Id, "source2.txt", testFile2, DeploymentMethod.Copy);

        // Verify files exist
        Assert.True(File.Exists(testFile1));
        Assert.True(File.Exists(testFile2));

        // Act
        var (filesDeleted, recordsDeleted) = await _deploymentService.PreDeploymentCleanupAsync();

        // Assert
        Assert.Equal(2, filesDeleted);
        Assert.Equal(1, recordsDeleted);
        Assert.False(File.Exists(testFile1));
        Assert.False(File.Exists(testFile2));

        // Verify session is cleaned up
        var latestSessionId = await _deploymentService.GetLatestDeploymentSessionIdAsync();
        Assert.Null(latestSessionId);
    }

    [Fact]
    public async Task PreDeploymentCleanup_Should_Handle_Missing_Files_Gracefully()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Test Profile",
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };
        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod",
            Version = "1.0.0",
            Path = _testSourceDir,
            Category = ModCategory.Gameplay,
            ImportedAt = DateTime.UtcNow
        };

        await _profileRepository.AddAsync(profile);
        await _modRepository.AddAsync(mod);

        // Create deployment record
        var record = await _deploymentService.CreateDeploymentRecordAsync(
            sessionId, profile.Id, mod.Id, DeploymentMethod.Copy);

        // Add deployment files that don't exist
        await _deploymentService.AddDeploymentFileAsync(
            record.Id, mod.Id, "source1.txt", Path.Combine(_testTargetDir, "nonexistent1.txt"), DeploymentMethod.Copy);
        await _deploymentService.AddDeploymentFileAsync(
            record.Id, mod.Id, "source2.txt", Path.Combine(_testTargetDir, "nonexistent2.txt"), DeploymentMethod.Copy);

        // Act
        var (filesDeleted, recordsDeleted) = await _deploymentService.PreDeploymentCleanupAsync();

        // Assert
        Assert.Equal(2, filesDeleted); // CleanupFileAsync succeeds even if file doesn't exist
        Assert.Equal(1, recordsDeleted);
    }

    [Fact]
    public async Task PreDeploymentCleanup_Should_Clean_Multiple_Mods_In_Session()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Test Profile",
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };
        var mod1 = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod 1",
            Version = "1.0.0",
            Path = _testSourceDir,
            Category = ModCategory.Gameplay,
            ImportedAt = DateTime.UtcNow
        };
        var mod2 = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod 2",
            Version = "1.0.0",
            Path = _testSourceDir,
            Category = ModCategory.Gameplay,
            ImportedAt = DateTime.UtcNow
        };

        await _profileRepository.AddAsync(profile);
        await _modRepository.AddAsync(mod1);
        await _modRepository.AddAsync(mod2);

        // Create test files for mod1
        var mod1File = Path.Combine(_testTargetDir, "mod1.txt");
        await File.WriteAllTextAsync(mod1File, "mod1 content");

        // Create test files for mod2
        var mod2File = Path.Combine(_testTargetDir, "mod2.txt");
        await File.WriteAllTextAsync(mod2File, "mod2 content");

        // Create deployment records
        var record1 = await _deploymentService.CreateDeploymentRecordAsync(
            sessionId, profile.Id, mod1.Id, DeploymentMethod.Copy);
        var record2 = await _deploymentService.CreateDeploymentRecordAsync(
            sessionId, profile.Id, mod2.Id, DeploymentMethod.Copy);

        await _deploymentService.AddDeploymentFileAsync(
            record1.Id, mod1.Id, "source.txt", mod1File, DeploymentMethod.Copy);
        await _deploymentService.AddDeploymentFileAsync(
            record2.Id, mod2.Id, "source.txt", mod2File, DeploymentMethod.Copy);

        // Act
        var (filesDeleted, recordsDeleted) = await _deploymentService.PreDeploymentCleanupAsync();

        // Assert
        Assert.Equal(2, filesDeleted);
        Assert.Equal(2, recordsDeleted);
        Assert.False(File.Exists(mod1File));
        Assert.False(File.Exists(mod2File));
    }

    [Fact]
    public async Task DeployProfile_Should_Return_Failed_When_Profile_Not_Found()
    {
        // Arrange
        var nonExistentProfileId = Guid.NewGuid();

        // Act
        var result = await _deploymentService.DeployProfileAsync(
            nonExistentProfileId,
            DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom,
            _testTargetDir);

        // Assert
        Assert.Equal(DeploymentStatus.Failed, result.Status);
        Assert.Contains("not found", result.ErrorMessage);
    }

    [Fact]
    public async Task DeployProfile_Should_Complete_When_No_Enabled_Mods()
    {
        // Arrange
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Test Profile",
            Description = "Test",
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            IsActive = true,
            Mods = new List<ProfileMod>()
        };

        await _profileRepository.AddAsync(profile);

        // Act
        var result = await _deploymentService.DeployProfileAsync(
            profile.Id,
            DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom,
            _testTargetDir);

        // Assert
        Assert.Equal(DeploymentStatus.Completed, result.Status);
        Assert.True(string.IsNullOrEmpty(result.ErrorMessage), $"Expected no error, but got: {result.ErrorMessage}");
        Assert.Equal(0, result.TotalMods);
        Assert.Equal(0, result.CompletedMods);
    }

    [Fact]
    public async Task GetLatestSessionId_Should_Return_Null_When_No_Deployments()
    {
        // Act
        var sessionId = await _deploymentService.GetLatestDeploymentSessionIdAsync();

        // Assert
        Assert.Null(sessionId);
    }

    [Fact]
    public async Task GetLatestSessionId_Should_Return_Most_Recent_Session()
    {
        // Arrange
        var session1 = Guid.NewGuid();
        var session2 = Guid.NewGuid();
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Test Profile",
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };
        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod",
            Version = "1.0.0",
            Path = _testSourceDir,
            Category = ModCategory.Gameplay,
            ImportedAt = DateTime.UtcNow
        };

        await _profileRepository.AddAsync(profile);
        await _modRepository.AddAsync(mod);

        // Create first session (older)
        await _deploymentService.CreateDeploymentRecordAsync(
            session1, profile.Id, mod.Id, DeploymentMethod.Copy);

        await Task.Delay(100); // Ensure time difference

        // Create second session (newer)
        await _deploymentService.CreateDeploymentRecordAsync(
            session2, profile.Id, mod.Id, DeploymentMethod.Copy);

        // Act
        var latestSessionId = await _deploymentService.GetLatestDeploymentSessionIdAsync();

        // Assert
        Assert.Equal(session2, latestSessionId);
    }

    [Fact]
    public async Task CleanupDeployment_Should_Delete_Specific_Deployment()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Test Profile",
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };
        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod",
            Version = "1.0.0",
            Path = _testSourceDir,
            Category = ModCategory.Gameplay,
            ImportedAt = DateTime.UtcNow
        };

        await _profileRepository.AddAsync(profile);
        await _modRepository.AddAsync(mod);

        var testFile = Path.Combine(_testTargetDir, "test.txt");
        await File.WriteAllTextAsync(testFile, "test content");

        var record = await _deploymentService.CreateDeploymentRecordAsync(
            sessionId, profile.Id, mod.Id, DeploymentMethod.Copy);

        await _deploymentService.AddDeploymentFileAsync(
            record.Id, mod.Id, "source.txt", testFile, DeploymentMethod.Copy);

        // Act
        var (filesDeleted, recordDeleted) = await _deploymentService.CleanupDeploymentAsync(record.Id);

        // Assert
        Assert.Equal(1, filesDeleted);
        Assert.True(recordDeleted);
        Assert.False(File.Exists(testFile));

        // Verify record is deleted
        var deployments = await _deploymentService.GetDeploymentsBySessionIdAsync(sessionId);
        Assert.Empty(deployments);
    }

    [Fact]
    public async Task PreDeploymentCleanup_Should_Report_Progress()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Test Profile",
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };
        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod",
            Version = "1.0.0",
            Path = _testSourceDir,
            Category = ModCategory.Gameplay,
            ImportedAt = DateTime.UtcNow
        };

        await _profileRepository.AddAsync(profile);
        await _modRepository.AddAsync(mod);

        var testFile = Path.Combine(_testTargetDir, "test.txt");
        await File.WriteAllTextAsync(testFile, "test");

        var record = await _deploymentService.CreateDeploymentRecordAsync(
            sessionId, profile.Id, mod.Id, DeploymentMethod.Copy);
        await _deploymentService.AddDeploymentFileAsync(
            record.Id, mod.Id, "source.txt", testFile, DeploymentMethod.Copy);

        var progressMessages = new List<string>();
        var progress = new Progress<string>(msg => progressMessages.Add(msg));

        // Act
        await _deploymentService.PreDeploymentCleanupAsync(progress);

        // Assert
        Assert.NotEmpty(progressMessages);
        Assert.Contains(progressMessages, m => m.Contains("Pre-Deployment Cleanup"));
        Assert.Contains(progressMessages, m => m.Contains(sessionId.ToString()));
    }

    [Fact]
    public async Task GetDeploymentsBySessionId_Should_Return_All_Session_Deployments()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Test Profile",
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };
        var mod1 = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod 1",
            Version = "1.0.0",
            Path = _testSourceDir,
            Category = ModCategory.Gameplay,
            ImportedAt = DateTime.UtcNow
        };
        var mod2 = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod 2",
            Version = "1.0.0",
            Path = _testSourceDir,
            Category = ModCategory.Gameplay,
            ImportedAt = DateTime.UtcNow
        };
        var mod3 = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod 3",
            Version = "1.0.0",
            Path = _testSourceDir,
            Category = ModCategory.Gameplay,
            ImportedAt = DateTime.UtcNow
        };

        await _profileRepository.AddAsync(profile);
        await _modRepository.AddAsync(mod1);
        await _modRepository.AddAsync(mod2);
        await _modRepository.AddAsync(mod3);

        await _deploymentService.CreateDeploymentRecordAsync(
            sessionId, profile.Id, mod1.Id, DeploymentMethod.Copy);
        await _deploymentService.CreateDeploymentRecordAsync(
            sessionId, profile.Id, mod2.Id, DeploymentMethod.Copy);
        await _deploymentService.CreateDeploymentRecordAsync(
            sessionId, profile.Id, mod3.Id, DeploymentMethod.Copy);

        // Act
        var deployments = await _deploymentService.GetDeploymentsBySessionIdAsync(sessionId);
        var deploymentList = deployments.ToList();

        // Assert
        Assert.Equal(3, deploymentList.Count);
        Assert.All(deploymentList, d => Assert.Equal(sessionId, d.SessionId));
    }

    [Fact]
    public async Task UpdateDeploymentStatus_Should_Update_Record()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Test Profile",
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };
        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod",
            Version = "1.0.0",
            Path = _testSourceDir,
            Category = ModCategory.Gameplay,
            ImportedAt = DateTime.UtcNow
        };

        await _profileRepository.AddAsync(profile);
        await _modRepository.AddAsync(mod);

        var record = await _deploymentService.CreateDeploymentRecordAsync(
            sessionId, profile.Id, mod.Id, DeploymentMethod.Copy);

        var endTime = DateTime.UtcNow;

        // Act
        await _deploymentService.UpdateDeploymentStatusAsync(
            record.Id, DeploymentStatus.Completed, endTime);

        // Verify
        var updatedRecord = await _deploymentRepository.GetByIdAsync(record.Id);
        Assert.NotNull(updatedRecord);
        Assert.Equal(DeploymentStatus.Completed.ToString(), updatedRecord.Status);
        Assert.NotNull(updatedRecord.EndTime);
    }

    [Fact]
    public async Task AddDeploymentFiles_Should_Add_Multiple_Files()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Test Profile",
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };
        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Test Mod",
            Version = "1.0.0",
            Path = _testSourceDir,
            Category = ModCategory.Gameplay,
            ImportedAt = DateTime.UtcNow
        };

        await _profileRepository.AddAsync(profile);
        await _modRepository.AddAsync(mod);

        var record = await _deploymentService.CreateDeploymentRecordAsync(
            sessionId, profile.Id, mod.Id, DeploymentMethod.Copy);

        var files = new List<(string SourcePath, string TargetPath)>
        {
            ("source1.txt", "target1.txt"),
            ("source2.txt", "target2.txt"),
            ("source3.txt", "target3.txt")
        };

        // Act
        await _deploymentService.AddDeploymentFilesAsync(
            record.Id, mod.Id, files, DeploymentMethod.Copy);

        // Assert
        var deploymentFiles = await _deploymentService.GetDeploymentFilesAsync(record.Id);
        var fileList = deploymentFiles.ToList();
        Assert.Equal(3, fileList.Count);
    }

    [Fact]
    public async Task CleanupDeployment_ShouldKeepRecordWhenFileCleanupFails()
    {
        var sessionId = Guid.NewGuid();
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            Name = "Failing Cleanup Profile",
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };
        var mod = new Mod
        {
            Id = Guid.NewGuid(),
            Name = "Failing Cleanup Mod",
            Version = "1.0.0",
            Path = _testSourceDir,
            ImportedAt = DateTime.UtcNow
        };
        await _profileRepository.AddAsync(profile);
        await _modRepository.AddAsync(mod);

        var failingService = new DeploymentService(
            _profileRepository,
            _modRepository,
            _deploymentRepository,
            new[] { new FailingCleanupDeploymentMethod() });
        var record = await failingService.CreateDeploymentRecordAsync(
            sessionId, profile.Id, mod.Id, DeploymentMethod.Copy);
        await failingService.AddDeploymentFileAsync(
            record.Id, mod.Id, "source.txt", Path.Combine(_testTargetDir, "locked.txt"), DeploymentMethod.Copy);

        var (filesDeleted, recordDeleted) = await failingService.CleanupDeploymentAsync(record.Id);

        Assert.Equal(0, filesDeleted);
        Assert.False(recordDeleted);
        Assert.NotEmpty(await failingService.GetDeploymentsBySessionIdAsync(sessionId));
        Assert.Single(await failingService.GetDeploymentFilesAsync(record.Id));
    }

    private sealed class FailingCleanupDeploymentMethod : HD2Mimnt.Core.Interfaces.IDeploymentMethod
    {
        public string Name => "Copy";

        public Task<(bool CanDeploy, string? ErrorMessage)> CheckCompatibilityAsync(
            string sourceDirectory,
            string targetDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult((true, (string?)null));

        public Task<bool> DeployFileAsync(
            string sourcePath,
            string targetPath,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> CleanupFileAsync(
            string targetPath,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
