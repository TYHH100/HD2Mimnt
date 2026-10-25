using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.Repositories;
using Moq;
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
        var progress = new ImmediateProgress<string>(msg => progressMessages.Add(msg));

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

    [Theory]
    [InlineData(DeploymentOrder.TopToBottom, "first", "second")]
    [InlineData(DeploymentOrder.BottomToTop, "second", "first")]
    public async Task DeployProfile_ShouldRenumberPatchesInData(
        DeploymentOrder order, string firstContent, string secondContent)
    {
        var first = await AddPatchModAsync("first", "0123456789abcdef.patch_7");
        var second = await AddPatchModAsync("second", "0123456789abcdef.patch_3");
        File.WriteAllText(Path.Combine(first.Path, "preview.png"), "preview");
        File.WriteAllText(Path.Combine(first.Path, "manifest.json"), "{}");
        Directory.CreateDirectory(Path.Combine(first.Path, "unused"));
        File.WriteAllText(Path.Combine(first.Path, "unused", "aaaaaaaaaaaaaaaa.patch_0"), "unused");
        var profile = await AddPatchProfileAsync(first, second);

        var result = await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy, order, _testTargetDir);

        Assert.Equal(DeploymentStatus.Completed, result.Status);
        Assert.Equal(2, result.TotalMods);
        Assert.Equal(2, result.CompletedMods);
        var data = Path.Combine(_testTargetDir, "data");
        Assert.Equal(firstContent, File.ReadAllText(Path.Combine(data, "0123456789abcdef.patch_0")));
        Assert.Equal(secondContent, File.ReadAllText(Path.Combine(data, "0123456789abcdef.patch_1")));
        // 缺失的伴随文件不生成 0 字节占位：游戏不认占位文件。
        Assert.Equal(2, Directory.GetFiles(data).Length);
        Assert.Empty(Directory.GetFiles(_testTargetDir));
        var records = (await _deploymentService.GetDeploymentsBySessionIdAsync(result.SessionId)).ToList();
        foreach (var record in records)
            Assert.Single(await _deploymentService.GetDeploymentFilesAsync(record.Id));
        var cleanup = await _deploymentService.PreDeploymentCleanupAsync();
        Assert.Equal(2, cleanup.FilesDeleted);
        Assert.Empty(Directory.GetFiles(data));
        Assert.Equal("first", File.ReadAllText(Path.Combine(first.Path, "0123456789abcdef.patch_7")));
    }

    [Theory]
    [InlineData(18)]
    [InlineData(32)]
    public async Task DeployProfile_MultipleBundles_ShouldAllocateOnlyTheirOwnIndexes(int secondIndex)
    {
        var mod = await AddPatchModAsync("multi-bundle", "9ba626afa44a3aa3.patch_32");
        File.WriteAllText(Path.Combine(mod.Path, $"f9ced2d898787705.patch_{secondIndex}"), "second bundle");
        File.WriteAllText(Path.Combine(mod.Path, "9ba626afa44a3aa3.patch_32.gpu_resources"), "gpu");
        var profile = await AddPatchProfileAsync(mod);
        var result = await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);
        Assert.Equal(DeploymentStatus.Completed, result.Status);
        var data = Path.Combine(_testTargetDir, "data");
        Assert.Equal(3, Directory.GetFiles(data).Length);
        Assert.Equal("multi-bundle", File.ReadAllText(Path.Combine(data, "9ba626afa44a3aa3.patch_0")));
        Assert.Equal("second bundle", File.ReadAllText(Path.Combine(data, "f9ced2d898787705.patch_0")));
        Assert.Equal("gpu", File.ReadAllText(Path.Combine(data, "9ba626afa44a3aa3.patch_0.gpu_resources")));
        var record = Assert.Single(await _deploymentService.GetDeploymentsBySessionIdAsync(result.SessionId));
        var files = (await _deploymentService.GetDeploymentFilesAsync(record.Id)).ToList();
        Assert.Equal(3, files.Count);
        Assert.All(files, file => Assert.False(string.IsNullOrEmpty(file.SourcePath)));
    }

    [Fact]
    public async Task DeployProfile_SparseIndexes_ShouldCompactInNumericOrderAndContinueAcrossMods()
    {
        var first = await AddPatchModAsync("first", "9ba626afa44a3aa3.patch_32");
        File.WriteAllText(Path.Combine(first.Path, "9ba626afa44a3aa3.patch_18"), "eighteen");
        File.WriteAllText(Path.Combine(first.Path, "9ba626afa44a3aa3.patch_18.stream"), "stream18");
        var second = await AddPatchModAsync("second", "9ba626afa44a3aa3.patch_7");
        File.WriteAllText(Path.Combine(second.Path, "f9ced2d898787705.patch_1"), "other");
        var profile = await AddPatchProfileAsync(first, second);

        var result = await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);

        Assert.Equal(DeploymentStatus.Completed, result.Status);
        var data = Path.Combine(_testTargetDir, "data");
        Assert.Equal(5, Directory.GetFiles(data).Length);
        Assert.Equal("eighteen", File.ReadAllText(Path.Combine(data, "9ba626afa44a3aa3.patch_0")));
        Assert.Equal("stream18", File.ReadAllText(Path.Combine(data, "9ba626afa44a3aa3.patch_0.stream")));
        Assert.Equal("first", File.ReadAllText(Path.Combine(data, "9ba626afa44a3aa3.patch_1")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(data, "9ba626afa44a3aa3.patch_2")));
        Assert.Equal("other", File.ReadAllText(Path.Combine(data, "f9ced2d898787705.patch_0")));
    }

    [Theory]
    [InlineData(".stream")]
    [InlineData(".gpu_resources")]
    [InlineData("")]
    public async Task DeployProfile_MissingOrEmptyBase_ShouldFailBeforeCleaningPreviousDeployment(string suffix)
    {
        var previousMod = await AddPatchModAsync("previous", "aaaaaaaaaaaaaaaa.patch_7");
        var profile = await AddPatchProfileAsync(previousMod);
        var previous = await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);
        Assert.Equal(DeploymentStatus.Completed, previous.Status);
        var invalid = await AddPatchModAsync("invalid", $"bbbbbbbbbbbbbbbb.patch_18{suffix}");
        if (suffix.Length == 0)
            File.WriteAllText(Path.Combine(invalid.Path, "bbbbbbbbbbbbbbbb.patch_18"), string.Empty);
        await new ProfileService(_profileRepository, _modRepository).AddModToProfileAsync(profile.Id, invalid.Id);

        var result = await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);

        Assert.Equal(DeploymentStatus.Failed, result.Status);
        Assert.Contains("bbbbbbbbbbbbbbbb.patch_18", result.ErrorMessage);
        Assert.Equal(0, result.FilesDeleted);
        Assert.NotEmpty(await _deploymentService.GetDeploymentsBySessionIdAsync(previous.SessionId));
        Assert.Empty(await _deploymentService.GetDeploymentsBySessionIdAsync(result.SessionId));
        var data = Path.Combine(_testTargetDir, "data");
        Assert.Single(Directory.GetFiles(data));
        Assert.Equal("previous", File.ReadAllText(Path.Combine(data, "aaaaaaaaaaaaaaaa.patch_0")));
    }

    [Fact]
    public async Task DeployProfile_SelectedDirectories_ShouldKeepIndependentTripletsAndIgnoreUnselectedFiles()
    {
        var mod = await AddPatchModAsync("options", "aaaaaaaaaaaaaaaa.patch_99");
        File.WriteAllText(Path.Combine(mod.Path, "aaaaaaaaaaaaaaaa.patch_99"), string.Empty);
        foreach (var directory in new[] { "first", "second", "unused" })
            Directory.CreateDirectory(Path.Combine(mod.Path, directory));
        File.WriteAllText(Path.Combine(mod.Path, "first", "aaaaaaaaaaaaaaaa.patch_32"), "first option");
        File.WriteAllText(Path.Combine(mod.Path, "second", "aaaaaaaaaaaaaaaa.patch_18"), "second option");
        File.WriteAllText(Path.Combine(mod.Path, "unused", "bbbbbbbbbbbbbbbb.patch_1.stream"), "unused orphan");
        mod.Manifest = new ModManifest
        {
            ManifestVersion = ManifestVersion.V1,
            Options = [new ModOption { Name = "selected", Include = ["first", "second"] },
                new ModOption { Name = "unused", Include = ["unused"], DefaultEnabled = false }]
        };
        await _modRepository.UpdateAsync(mod);
        var profile = await AddPatchProfileAsync(mod);

        var result = await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);

        Assert.Equal(DeploymentStatus.Completed, result.Status);
        var data = Path.Combine(_testTargetDir, "data");
        Assert.Equal(2, Directory.GetFiles(data).Length);
        Assert.Equal("first option", File.ReadAllText(Path.Combine(data, "aaaaaaaaaaaaaaaa.patch_0")));
        Assert.Equal("second option", File.ReadAllText(Path.Combine(data, "aaaaaaaaaaaaaaaa.patch_1")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeployProfile_MethodReportsSuccessWithoutValidBase_ShouldFailAndRemainTracked(bool createEmptyTarget)
    {
        var mod = await AddPatchModAsync("invalid-target", "aaaaaaaaaaaaaaaa.patch_7");
        var profile = await AddPatchProfileAsync(mod);
        var method = new Mock<HD2Mimnt.Core.Interfaces.IDeploymentMethod>();
        method.SetupGet(item => item.Name).Returns("Copy");
        method.Setup(item => item.CheckCompatibilityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));
        method.Setup(item => item.DeployFileAsync(It.IsAny<string>(), It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .Returns((string source, string target, IProgress<string>? progress, CancellationToken token) =>
            {
                if (createEmptyTarget) File.WriteAllText(target, string.Empty);
                return Task.FromResult(true);
            });
        var service = new DeploymentService(_profileRepository, _modRepository, _deploymentRepository, [method.Object]);

        var result = await service.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);

        Assert.Equal(DeploymentStatus.Failed, result.Status);
        Assert.Contains("主文件为空", result.ErrorMessage);
        Assert.Empty(await service.GetDeploymentsBySessionIdAsync(result.SessionId));
        Assert.Empty(Directory.GetFiles(Path.Combine(_testTargetDir, "data")));
    }

    [Fact]
    public async Task DeployProfile_ShouldReportSkippedAndDisabledModsAccurately()
    {
        var valid = await AddPatchModAsync("valid", "aaaaaaaaaaaaaaaa.patch_0");
        var empty = await AddPatchModAsync("empty", "readme.txt");
        var disabled = await AddPatchModAsync("disabled", "bbbbbbbbbbbbbbbb.patch_0");
        var profile = await AddPatchProfileAsync(valid, empty, disabled);
        await new ProfileService(_profileRepository, _modRepository).SetModEnabledAsync(profile.Id, disabled.Id, false);
        var result = await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);
        Assert.Equal(DeploymentStatus.PartiallyFailed, result.Status);
        Assert.Equal(2, result.TotalMods);
        Assert.Equal(1, result.CompletedMods);
        Assert.Equal(1, result.SkippedMods);
        Assert.Contains("empty", result.Message);
        Assert.Single(Directory.GetFiles(Path.Combine(_testTargetDir, "data")));
    }

    [Fact]
    public async Task DeployProfile_IncompatibleMethod_ShouldKeepPreviousDeployment()
    {
        var mod = await AddPatchModAsync("previous", "aaaaaaaaaaaaaaaa.patch_0");
        var profile = await AddPatchProfileAsync(mod);
        var previous = await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);
        var method = new Moq.Mock<HD2Mimnt.Core.Interfaces.IDeploymentMethod>();
        method.SetupGet(m => m.Name).Returns("HardLink");
        method.Setup(m => m.CheckCompatibilityAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<string>(), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "different volumes"));
        var service = new DeploymentService(_profileRepository, _modRepository, _deploymentRepository, new[] { method.Object });

        var result = await service.DeployProfileAsync(profile.Id, DeploymentMethod.HardLink,
            DeploymentOrder.TopToBottom, _testTargetDir);

        Assert.Equal(DeploymentStatus.Failed, result.Status);
        Assert.Contains("different volumes", result.ErrorMessage);
        Assert.NotEmpty(await service.GetDeploymentsBySessionIdAsync(previous.SessionId));
        Assert.Equal("previous", File.ReadAllText(Path.Combine(_testTargetDir, "data", "aaaaaaaaaaaaaaaa.patch_0")));
    }

    [Fact]
    public async Task DeployProfile_CancelDuringFileWrite_ShouldTrackPartialTargetForCleanup()
    {
        var mod = await AddPatchModAsync("cancel", "aaaaaaaaaaaaaaaa.patch_0");
        var profile = await AddPatchProfileAsync(mod);
        using var cts = new CancellationTokenSource();
        var method = new Moq.Mock<HD2Mimnt.Core.Interfaces.IDeploymentMethod>();
        method.SetupGet(m => m.Name).Returns("Copy");
        method.Setup(m => m.CheckCompatibilityAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<string>(), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));
        method.Setup(m => m.DeployFileAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<string>(), null, Moq.It.IsAny<CancellationToken>()))
            .Returns((string source, string target, IProgress<string>? progress, CancellationToken token) =>
            {
                File.WriteAllText(target, "partial");
                cts.Cancel();
                return Task.FromCanceled<bool>(token);
            });
        var service = new DeploymentService(_profileRepository, _modRepository, _deploymentRepository, new[] { method.Object });
        var result = await service.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir, cancellationToken: cts.Token);

        Assert.Equal(DeploymentStatus.Cancelled, result.Status);
        Assert.Equal(0, result.CompletedMods);
        // 取消发生在临时写入阶段：不登记记录，也不留下最终目标。
        Assert.Empty(await service.GetDeploymentsBySessionIdAsync(result.SessionId));
        var data = Path.Combine(_testTargetDir, "data");
        Assert.Empty(Directory.Exists(data) ? Directory.GetFiles(data) : Array.Empty<string>());
    }

    [Fact]
    public async Task DeployProfile_FileTrackingBatchFails_ShouldWriteNoTargetsAndRollbackTracking()
    {
        var mod = await AddPatchModAsync("tracking-failure", "aaaaaaaaaaaaaaaa.patch_0");
        var profile = await AddPatchProfileAsync(mod);
        using var connection = await _dbContext.OpenConnectionAsync();
        using var trigger = connection.CreateCommand();
        trigger.CommandText = """
            CREATE TRIGGER fail_tracking BEFORE INSERT ON DeploymentFiles
            BEGIN SELECT RAISE(ABORT, 'tracking failed'); END;
            """;
        await trigger.ExecuteNonQueryAsync();

        var result = await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);

        Assert.Equal(DeploymentStatus.Failed, result.Status);
        Assert.Empty(await _deploymentService.GetDeploymentsBySessionIdAsync(result.SessionId));
        Assert.Empty(Directory.GetFiles(Path.Combine(_testTargetDir, "data")));
    }

    [Theory]
    [InlineData(0, DeploymentOrder.TopToBottom, DeploymentMethod.Copy)]
    [InlineData(1, DeploymentOrder.TopToBottom, DeploymentMethod.Copy)]
    [InlineData(2, DeploymentOrder.TopToBottom, DeploymentMethod.Copy)]
    [InlineData(1, DeploymentOrder.BottomToTop, DeploymentMethod.Copy)]
    [InlineData(1, DeploymentOrder.TopToBottom, DeploymentMethod.HardLink)]
    public async Task DeleteMod_ShouldRemoveTargetsCompactTripletsAndUpdateTracking(
        int removedIndex, DeploymentOrder order, DeploymentMethod method)
    {
        var mods = new List<Mod>();
        for (var i = 0; i < 3; i++)
        {
            var mod = await AddPatchModAsync($"mod{i}", "aaaaaaaaaaaaaaaa.patch_7");
            File.WriteAllText(Path.Combine(mod.Path, "aaaaaaaaaaaaaaaa.patch_7.stream"), $"stream{i}");
            File.WriteAllText(Path.Combine(mod.Path, "bbbbbbbbbbbbbbbb.patch_7"), $"other{i}");
            mods.Add(mod);
        }
        var profile = await AddPatchProfileAsync(mods.ToArray());
        var service = new DeploymentService(_profileRepository, _modRepository, _deploymentRepository,
            new HD2Mimnt.Core.Interfaces.IDeploymentMethod[] { new CopyDeploymentMethod(), new HardLinkDeploymentMethod() });
        var result = await service.DeployProfileAsync(profile.Id, method, order, _testTargetDir);
        Assert.Equal(DeploymentStatus.Completed, result.Status);
        var removed = mods[removedIndex];
        var modService = new ModService(_modRepository, _testSourceDir, deploymentService: service);

        Assert.True(await modService.DeleteModAsync(removed.Id));

        Assert.Null(await _modRepository.GetByIdAsync(removed.Id));
        Assert.False(Directory.Exists(removed.Path));
        Assert.Empty(await service.GetModDeploymentHistoryAsync(removed.Id));
        var expected = (order == DeploymentOrder.TopToBottom ? mods : mods.AsEnumerable().Reverse())
            .Where(mod => mod.Id != removed.Id).ToList();
        var data = Path.Combine(_testTargetDir, "data");
        Assert.Equal(6, Directory.GetFiles(data).Length);
        for (var i = 0; i < expected.Count; i++)
        {
            var mod = expected[i];
            Assert.Equal(mod.Name, File.ReadAllText(Path.Combine(data, $"aaaaaaaaaaaaaaaa.patch_{i}")));
            Assert.Equal($"stream{mods.IndexOf(mod)}", File.ReadAllText(Path.Combine(data, $"aaaaaaaaaaaaaaaa.patch_{i}.stream")));
            Assert.Equal($"other{mods.IndexOf(mod)}", File.ReadAllText(Path.Combine(data, $"bbbbbbbbbbbbbbbb.patch_{i}")));
            var record = Assert.Single(await service.GetModDeploymentHistoryAsync(mod.Id));
            Assert.All(record.Files, file => Assert.Contains($".patch_{i}", file.TargetPath));
            Assert.Equal(mod.Name, File.ReadAllText(Path.Combine(mod.Path, "aaaaaaaaaaaaaaaa.patch_7")));
        }
        await service.PreDeploymentCleanupAsync();
        Assert.Empty(Directory.GetFiles(data));
        Assert.Empty(await service.GetDeploymentsBySessionIdAsync(result.SessionId));
    }

    [Fact]
    public async Task DeleteMod_ExternallyDeletedRemainingTargets_ShouldSucceedAndSyncTracking()
    {
        var first = await AddPatchModAsync("first", "aaaaaaaaaaaaaaaa.patch_0");
        var second = await AddPatchModAsync("second", "aaaaaaaaaaaaaaaa.patch_0");
        File.WriteAllText(Path.Combine(second.Path, "aaaaaaaaaaaaaaaa.patch_0.stream"), "second-stream");
        var profile = await AddPatchProfileAsync(first, second);
        await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy, DeploymentOrder.TopToBottom, _testTargetDir);
        var remainingRecord = Assert.Single(await _deploymentService.GetModDeploymentHistoryAsync(second.Id));
        Assert.All(remainingRecord.Files, file => Assert.Contains(".patch_1", file.TargetPath));
        // 模拟用户在游戏目录手动删除全部部署文件。
        Directory.Delete(Path.Combine(_testTargetDir, "data"), true);

        await _deploymentService.DeleteModDeploymentRecordsAsync(first.Id);

        Assert.Empty(await _deploymentService.GetModDeploymentHistoryAsync(first.Id));
        var compacted = Assert.Single(await _deploymentService.GetModDeploymentHistoryAsync(second.Id));
        Assert.All(compacted.Files, file =>
        {
            Assert.Contains(".patch_0", file.TargetPath);
            Assert.False(File.Exists(file.TargetPath));
        });
        // 缺失文件按清理成功计数并删除记录，不残留追踪。
        var (filesDeleted, recordsDeleted) = await _deploymentService.PreDeploymentCleanupAsync();
        Assert.Equal(2, filesDeleted);
        Assert.Equal(1, recordsDeleted);
        Assert.Empty(await _deploymentService.GetModDeploymentHistoryAsync(second.Id));
    }

    [Fact]
    public async Task DeleteMod_PartiallyDeletedRemainingTargets_ShouldMoveExistingAndSyncMissing()
    {
        var first = await AddPatchModAsync("first", "aaaaaaaaaaaaaaaa.patch_0");
        var second = await AddPatchModAsync("second", "aaaaaaaaaaaaaaaa.patch_0");
        File.WriteAllText(Path.Combine(second.Path, "aaaaaaaaaaaaaaaa.patch_0.stream"), "second-stream");
        var profile = await AddPatchProfileAsync(first, second);
        await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy, DeploymentOrder.TopToBottom, _testTargetDir);
        var remainingRecord = Assert.Single(await _deploymentService.GetModDeploymentHistoryAsync(second.Id));
        // 仅外部删除主文件，sidecar 仍在磁盘上。
        File.Delete(remainingRecord.Files.Single(file => file.TargetPath.EndsWith(".patch_1", StringComparison.Ordinal)).TargetPath);

        await _deploymentService.DeleteModDeploymentRecordsAsync(first.Id);

        var compacted = Assert.Single(await _deploymentService.GetModDeploymentHistoryAsync(second.Id));
        var stream = compacted.Files.Single(file => file.TargetPath.EndsWith(".stream", StringComparison.Ordinal));
        var baseFile = compacted.Files.Single(file => file.TargetPath.EndsWith(".patch_0", StringComparison.Ordinal));
        Assert.Equal("second-stream", File.ReadAllText(stream.TargetPath));
        Assert.False(File.Exists(baseFile.TargetPath));
    }

    [Fact]
    public async Task DeleteMod_CleanupFailure_ShouldKeepModFilesAndRecordsForRetry()
    {
        var mod = await AddPatchModAsync("keep", "aaaaaaaaaaaaaaaa.patch_0");
        var profile = await AddPatchProfileAsync(mod);
        await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy, DeploymentOrder.TopToBottom, _testTargetDir);
        var record = Assert.Single(await _deploymentService.GetModDeploymentHistoryAsync(mod.Id));
        var failing = new DeploymentService(_profileRepository, _modRepository, _deploymentRepository,
            new[] { new FailingCleanupDeploymentMethod() });
        var modService = new ModService(_modRepository, _testSourceDir, deploymentService: failing);

        await Assert.ThrowsAsync<IOException>(() => modService.DeleteModAsync(mod.Id));

        Assert.NotNull(await _modRepository.GetByIdAsync(mod.Id));
        Assert.True(Directory.Exists(mod.Path));
        Assert.All((await _deploymentRepository.GetByIdAsync(record.Id))!.Files, file => Assert.True(File.Exists(file.TargetPath)));
        Assert.True(await new ModService(_modRepository, _testSourceDir, deploymentService: _deploymentService).DeleteModAsync(mod.Id));
        Assert.Empty(Directory.GetFiles(Path.Combine(_testTargetDir, "data")));
    }

    [Fact]
    public async Task DeleteMod_RenameCollision_ShouldRestoreTrackingAndAllowRetry()
    {
        var first = await AddPatchModAsync("first", "aaaaaaaaaaaaaaaa.patch_0");
        var second = await AddPatchModAsync("second", "aaaaaaaaaaaaaaaa.patch_0");
        File.WriteAllText(Path.Combine(second.Path, "aaaaaaaaaaaaaaaa.patch_0.stream"), "second-stream");
        var profile = await AddPatchProfileAsync(first, second);
        await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy, DeploymentOrder.TopToBottom, _testTargetDir);
        var remainingRecord = Assert.Single(await _deploymentService.GetModDeploymentHistoryAsync(second.Id));
        var blocked = remainingRecord.Files.Single(file => file.TargetPath.EndsWith(".stream", StringComparison.Ordinal));
        // 移动时锁定后一个 sidecar，确保前面的移动也被回滚。
        using (var locked = new FileStream(blocked.TargetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => _deploymentService.DeleteModDeploymentRecordsAsync(first.Id));
        }
        Assert.NotNull(await _modRepository.GetByIdAsync(first.Id));
        Assert.Single(await _deploymentService.GetModDeploymentHistoryAsync(first.Id));
        var unchanged = (await _deploymentRepository.GetByIdAsync(remainingRecord.Id))!;
        Assert.All(unchanged.Files, file =>
        {
            Assert.Contains(".patch_1", file.TargetPath);
            Assert.True(File.Exists(file.TargetPath));
        });
        await _deploymentService.DeleteModDeploymentRecordsAsync(first.Id);
        var compacted = (await _deploymentRepository.GetByIdAsync(remainingRecord.Id))!;
        Assert.All(compacted.Files, file => Assert.Contains(".patch_0", file.TargetPath));
        await _deploymentService.PreDeploymentCleanupAsync();
        Assert.Empty(Directory.GetFiles(Path.Combine(_testTargetDir, "data")));
    }

    [Fact]
    public async Task DeleteMod_Canceled_ShouldKeepTargetsAndRecords()
    {
        var mod = await AddPatchModAsync("cancelDelete", "aaaaaaaaaaaaaaaa.patch_0");
        var profile = await AddPatchProfileAsync(mod);
        await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy, DeploymentOrder.TopToBottom, _testTargetDir);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModService(_modRepository,
            _testSourceDir, deploymentService: _deploymentService).DeleteModAsync(mod.Id, cancellation.Token));
        Assert.NotNull(await _modRepository.GetByIdAsync(mod.Id));
        var record = Assert.Single(await _deploymentService.GetModDeploymentHistoryAsync(mod.Id));
        Assert.All(record.Files, file => Assert.True(File.Exists(file.TargetPath)));
    }

    [Fact]
    public async Task DeleteMod_CommitFailure_ShouldRollbackFileRenamesAndDatabaseChanges()
    {
        var first = await AddPatchModAsync("commitFirst", "aaaaaaaaaaaaaaaa.patch_0");
        var second = await AddPatchModAsync("commitSecond", "aaaaaaaaaaaaaaaa.patch_0");
        var profile = await AddPatchProfileAsync(first, second);
        await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy, DeploymentOrder.TopToBottom, _testTargetDir);
        var secondRecord = Assert.Single(await _deploymentService.GetModDeploymentHistoryAsync(second.Id));
        using var connection = await _dbContext.OpenConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER FailTargetUpdate BEFORE UPDATE OF TargetPath ON DeploymentFiles BEGIN SELECT RAISE(ABORT, 'test commit failure'); END";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => _deploymentService.DeleteModDeploymentRecordsAsync(first.Id));
        Assert.Single(await _deploymentService.GetModDeploymentHistoryAsync(first.Id));
        Assert.All((await _deploymentRepository.GetByIdAsync(secondRecord.Id))!.Files, file =>
        {
            Assert.Contains(".patch_1", file.TargetPath);
            Assert.True(File.Exists(file.TargetPath));
        });
        command.CommandText = "DROP TRIGGER FailTargetUpdate";
        await command.ExecuteNonQueryAsync();
        await _deploymentService.DeleteModDeploymentRecordsAsync(first.Id);
        await _deploymentService.PreDeploymentCleanupAsync();
        Assert.Empty(Directory.GetFiles(Path.Combine(_testTargetDir, "data")));
    }

    [Fact]
    public async Task DeployProfile_SecondRunIdentical_ShouldSkipRedeployment()
    {
        var mod = await AddPatchModAsync("stable", "0123456789abcdef.patch_7");
        var profile = await AddPatchProfileAsync(mod);
        var first = await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);
        Assert.Equal(DeploymentStatus.Completed, first.Status);
        var data = Path.Combine(_testTargetDir, "data");
        var baseTarget = Path.Combine(data, "0123456789abcdef.patch_0");
        var lastWrite = File.GetLastWriteTimeUtc(baseTarget);

        var second = await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);

        Assert.Equal(DeploymentStatus.Completed, second.Status);
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal(1, second.PreservedFiles);
        Assert.Equal(0, second.FilesDeleted);
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(baseTarget));
        Assert.Equal("stable", File.ReadAllText(baseTarget));
        Assert.Single(await _deploymentService.GetDeploymentsBySessionIdAsync(first.SessionId));
        Assert.Single(Directory.GetFiles(data));
    }

    [Fact]
    public async Task DeployProfile_ChangedMod_ShouldPreserveUnchangedModFiles()
    {
        var first = await AddPatchModAsync("first", "0123456789abcdef.patch_7");
        var second = await AddPatchModAsync("second", "8888888888888888.patch_3");
        File.WriteAllText(Path.Combine(second.Path, "8888888888888888.patch_3.stream"), "second-stream");
        var profile = await AddPatchProfileAsync(first, second);
        var initial = await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);
        Assert.Equal(DeploymentStatus.Completed, initial.Status);
        var data = Path.Combine(_testTargetDir, "data");
        var secondBase = Path.Combine(data, "8888888888888888.patch_0");
        var secondStream = Path.Combine(data, "8888888888888888.patch_0.stream");
        var secondBaseWrite = File.GetLastWriteTimeUtc(secondBase);
        var secondStreamWrite = File.GetLastWriteTimeUtc(secondStream);

        File.WriteAllText(Path.Combine(first.Path, "0123456789abcdef.patch_7"), "first-updated");

        var result = await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);

        Assert.Equal(DeploymentStatus.Completed, result.Status);
        Assert.NotEqual(initial.SessionId, result.SessionId);
        // 只重写 first 的 base：second 的 base 与 stream 全部沿用。
        Assert.Equal(2, result.PreservedFiles);
        Assert.Equal(0, result.FilesDeleted);
        Assert.Equal(1, result.UpdatedFiles);
        Assert.Equal("first-updated", File.ReadAllText(Path.Combine(data, "0123456789abcdef.patch_0")));
        Assert.Equal("second", File.ReadAllText(secondBase));
        Assert.Equal(secondBaseWrite, File.GetLastWriteTimeUtc(secondBase));
        Assert.Equal("second-stream", File.ReadAllText(secondStream));
        Assert.Equal(secondStreamWrite, File.GetLastWriteTimeUtc(secondStream));
    }

    [Fact]
    public async Task DeployProfile_ExternallyModifiedTarget_ShouldRewriteTarget()
    {
        var mod = await AddPatchModAsync("guard", "0123456789abcdef.patch_7");
        File.WriteAllText(Path.Combine(mod.Path, "0123456789abcdef.patch_7"), "12345678");
        var profile = await AddPatchProfileAsync(mod);
        await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);
        var data = Path.Combine(_testTargetDir, "data");
        var baseTarget = Path.Combine(data, "0123456789abcdef.patch_0");
        File.WriteAllText(baseTarget, "87654321"); // 与源同长度的外部篡改，验证内容比对而非只看长度

        var result = await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);

        Assert.Equal(DeploymentStatus.Completed, result.Status);
        Assert.Equal(0, result.PreservedFiles);
        Assert.Equal("12345678", File.ReadAllText(baseTarget));
    }

    [Fact]
    public async Task DeployProfile_ReorderedMods_ShouldRenameDisplacedFilesInsteadOfRecopying()
    {
        var first = await AddPatchModAsync("first", "0123456789abcdef.patch_5");
        var second = await AddPatchModAsync("second", "0123456789abcdef.patch_9");
        var profile = await AddPatchProfileAsync(first, second);
        await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);
        var data = Path.Combine(_testTargetDir, "data");
        var firstTarget = Path.Combine(data, "0123456789abcdef.patch_0");
        var secondTarget = Path.Combine(data, "0123456789abcdef.patch_1");
        Assert.Equal("first", File.ReadAllText(firstTarget));
        var firstWrite = File.GetLastWriteTimeUtc(firstTarget);
        var secondWrite = File.GetLastWriteTimeUtc(secondTarget);

        await new ProfileService(_profileRepository, _modRepository).ReorderModsAsync(profile.Id, [second.Id, first.Id]);

        var result = await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);

        Assert.Equal(DeploymentStatus.Completed, result.Status);
        // 实体文件改名互换编号，零删除零重拷。
        Assert.Equal(2, result.RenamedFiles);
        Assert.Equal(2, result.PreservedFiles);
        Assert.Equal(0, result.FilesDeleted);
        Assert.Equal("second", File.ReadAllText(firstTarget));
        Assert.Equal("first", File.ReadAllText(secondTarget));
        Assert.Equal(secondWrite, File.GetLastWriteTimeUtc(firstTarget));
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(secondTarget));
        Assert.Empty(Directory.GetFiles(data, ".hd2mimnt_stage_*"));
    }

    [Fact]
    public async Task DeployProfile_HardLinkSecondRunIdentical_ShouldPreserveLinkedFiles()
    {
        var mod = await AddPatchModAsync("linked", "0123456789abcdef.patch_7");
        var profile = await AddPatchProfileAsync(mod);
        var service = new DeploymentService(_profileRepository, _modRepository, _deploymentRepository,
            new HD2Mimnt.Core.Interfaces.IDeploymentMethod[] { new CopyDeploymentMethod(), new HardLinkDeploymentMethod() });
        var first = await service.DeployProfileAsync(profile.Id, DeploymentMethod.HardLink,
            DeploymentOrder.TopToBottom, _testTargetDir);
        Assert.Equal(DeploymentStatus.Completed, first.Status);
        var data = Path.Combine(_testTargetDir, "data");
        var baseTarget = Path.Combine(data, "0123456789abcdef.patch_0");
        var source = Path.Combine(mod.Path, "0123456789abcdef.patch_7");
        Assert.True(HD2Mimnt.Core.IO.FileIdentity.IsSameFile(source, baseTarget));

        var second = await service.DeployProfileAsync(profile.Id, DeploymentMethod.HardLink,
            DeploymentOrder.TopToBottom, _testTargetDir);

        Assert.Equal(DeploymentStatus.Completed, second.Status);
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal(1, second.PreservedFiles);
        Assert.True(HD2Mimnt.Core.IO.FileIdentity.IsSameFile(source, baseTarget));
    }

    [Fact]
    public async Task DeployProfile_MethodChanged_ShouldRewriteWithNewMethod()
    {
        var mod = await AddPatchModAsync("switch", "0123456789abcdef.patch_7");
        var profile = await AddPatchProfileAsync(mod);
        var service = new DeploymentService(_profileRepository, _modRepository, _deploymentRepository,
            new HD2Mimnt.Core.Interfaces.IDeploymentMethod[] { new CopyDeploymentMethod(), new HardLinkDeploymentMethod() });
        await service.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);
        var data = Path.Combine(_testTargetDir, "data");
        var baseTarget = Path.Combine(data, "0123456789abcdef.patch_0");
        var source = Path.Combine(mod.Path, "0123456789abcdef.patch_7");
        Assert.False(HD2Mimnt.Core.IO.FileIdentity.IsSameFile(source, baseTarget));

        var result = await service.DeployProfileAsync(profile.Id, DeploymentMethod.HardLink,
            DeploymentOrder.TopToBottom, _testTargetDir);

        Assert.Equal(DeploymentStatus.Completed, result.Status);
        // 登记方法与本次不同，全部按新方法重建。
        Assert.Equal(0, result.RenamedFiles);
        Assert.Equal(0, result.PreservedFiles);
        Assert.True(HD2Mimnt.Core.IO.FileIdentity.IsSameFile(source, baseTarget));
    }

    [Fact]
    public async Task PreDeploymentCleanup_WithPreserveTargets_ShouldKeepFilesButDeleteRecords()
    {
        var mod = await AddPatchModAsync("preserve", "0123456789abcdef.patch_7");
        File.WriteAllText(Path.Combine(mod.Path, "0123456789abcdef.patch_7.stream"), "preserve-stream");
        var profile = await AddPatchProfileAsync(mod);
        await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);
        var data = Path.Combine(_testTargetDir, "data");
        var baseTarget = Path.Combine(data, "0123456789abcdef.patch_0");

        var cleanup = await _deploymentService.PreDeploymentCleanupAsync(preserveTargets: [baseTarget]);

        Assert.Equal(1, cleanup.FilesDeleted);
        Assert.True(File.Exists(baseTarget));
        Assert.False(File.Exists(Path.Combine(data, "0123456789abcdef.patch_0.stream")));
        Assert.Null(await _deploymentService.GetLatestDeploymentSessionIdAsync());
    }

    [Fact]
    public async Task DeployProfile_TwoGamePaths_ShouldKeepEachInstallStateIndependent()
    {
        var mod = await AddPatchModAsync("isolated", "0123456789abcdef.patch_7");
        var profile = await AddPatchProfileAsync(mod);
        var secondRoot = Path.Combine(Path.GetTempPath(), $"test_target_second_{Guid.NewGuid():N}");
        Directory.CreateDirectory(secondRoot);
        try
        {
            var first = await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
                DeploymentOrder.TopToBottom, _testTargetDir);
            var firstFile = Path.Combine(_testTargetDir, "data", "0123456789abcdef.patch_0");
            Assert.Equal(DeploymentStatus.Completed, first.Status);
            Assert.Equal("isolated", File.ReadAllText(firstFile));

            var second = await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
                DeploymentOrder.TopToBottom, secondRoot);
            var secondFile = Path.Combine(secondRoot, "data", "0123456789abcdef.patch_0");
            Assert.Equal(DeploymentStatus.Completed, second.Status);
            Assert.Equal("isolated", File.ReadAllText(secondFile));
            Assert.True(File.Exists(firstFile));

            var firstRecords = await _deploymentService.GetDeploymentsForGameAsync(_testTargetDir);
            var secondRecords = await _deploymentService.GetDeploymentsForGameAsync(secondRoot);
            var firstRecord = Assert.Single(firstRecords);
            var secondRecord = Assert.Single(secondRecords);
            Assert.NotEqual(firstRecord.Id, secondRecord.Id);
            Assert.Equal(firstRecord.ModId, secondRecord.ModId);

            var third = await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
                DeploymentOrder.TopToBottom, _testTargetDir);
            Assert.Equal(first.SessionId, third.SessionId);
            Assert.Equal("isolated", File.ReadAllText(secondFile));
        }
        finally
        {
            if (Directory.Exists(secondRoot)) Directory.Delete(secondRoot, true);
        }
    }

    [Fact]
    public async Task DeployProfile_HardLinkToCopy_ShouldRewriteTargetEvenWhenContentMatches()
    {
        var mod = await AddPatchModAsync("switch", "0123456789abcdef.patch_7");
        var profile = await AddPatchProfileAsync(mod);
        var service = new DeploymentService(_profileRepository, _modRepository, _deploymentRepository,
            new HD2Mimnt.Core.Interfaces.IDeploymentMethod[]
            {
                new CopyDeploymentMethod(), new HardLinkDeploymentMethod()
            });
        var source = Path.Combine(mod.Path, "0123456789abcdef.patch_7");
        var target = Path.Combine(_testTargetDir, "data", "0123456789abcdef.patch_0");

        var hardLink = await service.DeployProfileAsync(profile.Id, DeploymentMethod.HardLink,
            DeploymentOrder.TopToBottom, _testTargetDir);
        Assert.Equal(DeploymentStatus.Completed, hardLink.Status);
        Assert.True(HD2Mimnt.Core.IO.FileIdentity.IsSameFile(source, target));

        var copy = await service.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);

        Assert.Equal(DeploymentStatus.Completed, copy.Status);
        Assert.False(HD2Mimnt.Core.IO.FileIdentity.IsSameFile(source, target));
        Assert.Equal("switch", File.ReadAllText(target));
        var record = Assert.Single(await service.GetDeploymentsForGameAsync(_testTargetDir));
        Assert.All(record.Files, file => Assert.Equal(DeploymentMethod.Copy.ToString(), file.Method));
    }

    [Fact]
    public async Task DeployProfile_ShouldAdoptIdenticalUntrackedTargetWithoutRewriting()
    {
        var mod = await AddPatchModAsync("adopt", "0123456789abcdef.patch_7");
        var profile = await AddPatchProfileAsync(mod);
        var data = Path.Combine(_testTargetDir, "data");
        Directory.CreateDirectory(data);
        var target = Path.Combine(data, "0123456789abcdef.patch_0");
        File.WriteAllText(target, "adopt");
        var before = File.GetLastWriteTimeUtc(target);

        var result = await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);

        Assert.Equal(DeploymentStatus.Completed, result.Status);
        Assert.Equal(1, result.AddedFiles);
        Assert.Equal(0, result.UpdatedFiles);
        Assert.Equal(before, File.GetLastWriteTimeUtc(target));
        var record = Assert.Single(await _deploymentService.GetDeploymentsForGameAsync(_testTargetDir));
        var file = Assert.Single(record.Files);
        Assert.Equal(target, file.TargetPath, ignoreCase: true);
    }

    [Fact]
    public async Task DeployProfile_ChangedFile_ShouldKeepCurrentRecordIdentity()
    {
        var mod = await AddPatchModAsync("identity", "0123456789abcdef.patch_7");
        var profile = await AddPatchProfileAsync(mod);
        var first = await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);
        var firstRecord = Assert.Single(await _deploymentService.GetDeploymentsForGameAsync(_testTargetDir));
        File.WriteAllText(Path.Combine(mod.Path, "0123456789abcdef.patch_7"), "identity-updated");

        var second = await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);

        Assert.Equal(DeploymentStatus.Completed, second.Status);
        Assert.NotEqual(first.SessionId, second.SessionId);
        var record = Assert.Single(await _deploymentService.GetDeploymentsForGameAsync(_testTargetDir));
        Assert.Equal(firstRecord.Id, record.Id);
        Assert.Equal("identity-updated",
            File.ReadAllText(Path.Combine(_testTargetDir, "data", "0123456789abcdef.patch_0")));
    }

    [Fact]
    public async Task ReconcileAfterModUpdate_ShouldUpdateOnlyChangedModAndIgnorePendingProfileMods()
    {
        var first = await AddPatchModAsync("first", "0123456789abcdef.patch_7");
        var second = await AddPatchModAsync("second", "8888888888888888.patch_3");
        var profile = await AddPatchProfileAsync(first, second);
        await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);
        var data = Path.Combine(_testTargetDir, "data");
        var firstTarget = Path.Combine(data, "0123456789abcdef.patch_0");
        var secondTarget = Path.Combine(data, "8888888888888888.patch_0");
        var secondWrite = File.GetLastWriteTimeUtc(secondTarget);

        // 更新后内容落在新的 GUID 目录；这是之前增量匹配失效的根因。
        var updatedPath = Path.Combine(_testSourceDir, "first-updated");
        Directory.CreateDirectory(updatedPath);
        File.WriteAllText(Path.Combine(updatedPath, "0123456789abcdef.patch_7"), "first-updated-content");
        first.Path = updatedPath;
        await _modRepository.UpdateAsync(first);
        // 配置页新增一个未部署模组，自动部署不得顺带应用它。
        var pending = await AddPatchModAsync("pending", "bbbbbbbbbbbbbbbb.patch_1");
        await new ProfileService(_profileRepository, _modRepository).AddModToProfileAsync(profile.Id, pending.Id);

        var result = await _deploymentService.ReconcileAfterModUpdateAsync(first.Id, _testTargetDir);

        Assert.NotNull(result);
        Assert.Equal(DeploymentStatus.Completed, result!.Status);
        Assert.Equal(1, result.UpdatedFiles);
        Assert.Equal(1, result.UnchangedFiles);
        Assert.Equal("first-updated-content", File.ReadAllText(firstTarget));
        Assert.Equal("second", File.ReadAllText(secondTarget));
        Assert.Equal(secondWrite, File.GetLastWriteTimeUtc(secondTarget));
        Assert.False(File.Exists(Path.Combine(data, "bbbbbbbbbbbbbbbb.patch_0")));
        var records = await _deploymentService.GetDeploymentsForGameAsync(_testTargetDir);
        Assert.Equal(2, records.Count);
        var firstRecord = records.Single(record => record.ModId == first.Id);
        Assert.Contains("first-updated", Assert.Single(firstRecord.Files).SourcePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconcileAfterModUpdate_IdenticalContentNewSourceFolder_ShouldKeepTargetFile()
    {
        var mod = await AddPatchModAsync("stable", "0123456789abcdef.patch_7");
        var profile = await AddPatchProfileAsync(mod);
        await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);
        var target = Path.Combine(_testTargetDir, "data", "0123456789abcdef.patch_0");
        var before = File.GetLastWriteTimeUtc(target);
        var updatedPath = Path.Combine(_testSourceDir, "stable-new-folder");
        Directory.CreateDirectory(updatedPath);
        File.WriteAllText(Path.Combine(updatedPath, "0123456789abcdef.patch_7"), "stable");
        mod.Path = updatedPath;
        await _modRepository.UpdateAsync(mod);

        var result = await _deploymentService.ReconcileAfterModUpdateAsync(mod.Id, _testTargetDir);

        Assert.NotNull(result);
        Assert.Equal(DeploymentStatus.Completed, result!.Status);
        Assert.Equal(0, result.UpdatedFiles);
        Assert.Equal(1, result.UnchangedFiles);
        Assert.Equal(before, File.GetLastWriteTimeUtc(target));
        var record = Assert.Single(await _deploymentService.GetDeploymentsForGameAsync(_testTargetDir));
        Assert.Contains("stable-new-folder", Assert.Single(record.Files).SourcePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconcileAfterModUpdate_ShouldSkipWhenModDisabledInDeployedProfile()
    {
        var mod = await AddPatchModAsync("disabled", "0123456789abcdef.patch_7");
        var profile = await AddPatchProfileAsync(mod);
        await _deploymentService.DeployProfileAsync(profile.Id, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, _testTargetDir);
        var target = Path.Combine(_testTargetDir, "data", "0123456789abcdef.patch_0");
        File.WriteAllText(Path.Combine(mod.Path, "0123456789abcdef.patch_7"), "disabled-updated");
        await new ProfileService(_profileRepository, _modRepository)
            .SetModEnabledAsync(profile.Id, mod.Id, false);

        var result = await _deploymentService.ReconcileAfterModUpdateAsync(mod.Id, _testTargetDir);

        Assert.Null(result);
        Assert.Equal("disabled", File.ReadAllText(target));
    }

    private async Task<Mod> AddPatchModAsync(string name, string fileName)
    {
        var path = Path.Combine(_testSourceDir, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, fileName), name);
        var mod = new Mod { Id = Guid.NewGuid(), Name = name, Path = path };
        await _modRepository.AddAsync(mod);
        return mod;
    }

    private async Task<Profile> AddPatchProfileAsync(params Mod[] mods)
    {
        var profile = new Profile { Id = Guid.NewGuid(), Name = "Patch profile" };
        await _profileRepository.AddAsync(profile);
        foreach (var mod in mods)
            await new ProfileService(_profileRepository, _modRepository).AddModToProfileAsync(profile.Id, mod.Id);
        return profile;
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

        public Task<bool> IsUpToDateAsync(string sourcePath, string targetPath,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> CleanupFileAsync(
            string targetPath,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class ImmediateProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public ImmediateProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }
}
