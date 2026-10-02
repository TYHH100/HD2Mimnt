using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Tests.Deployment;

public class CopyDeploymentMethodTests : IDisposable
{
    private readonly string _testSourceDir;
    private readonly string _testTargetDir;
    private readonly CopyDeploymentMethod _copyMethod;

    public CopyDeploymentMethodTests()
    {
        _testSourceDir = Path.Combine(Path.GetTempPath(), $"hd2_test_source_{Guid.NewGuid()}");
        _testTargetDir = Path.Combine(Path.GetTempPath(), $"hd2_test_target_{Guid.NewGuid()}");

        Directory.CreateDirectory(_testSourceDir);
        Directory.CreateDirectory(_testTargetDir);

        _copyMethod = new CopyDeploymentMethod();
    }

    [Fact]
    public async Task CheckCompatibility_Should_Succeed_When_Directories_Valid()
    {
        // Act
        var (canDeploy, errorMessage) = await _copyMethod.CheckCompatibilityAsync(
            _testSourceDir,
            _testTargetDir);

        // Assert
        Assert.True(canDeploy);
        Assert.Null(errorMessage);
    }

    [Fact]
    public async Task CheckCompatibility_Should_Fail_When_Source_Not_Exists()
    {
        // Arrange
        var nonExistentDir = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid()}");

        // Act
        var (canDeploy, errorMessage) = await _copyMethod.CheckCompatibilityAsync(
            nonExistentDir,
            _testTargetDir);

        // Assert
        Assert.False(canDeploy);
        Assert.NotNull(errorMessage);
        Assert.Contains("does not exist", errorMessage);
    }

    [Fact]
    public async Task DeployFile_Should_Copy_Small_File_Successfully()
    {
        // Arrange
        var sourceFile = Path.Combine(_testSourceDir, "small_test.txt");
        var targetFile = Path.Combine(_testTargetDir, "small_test.txt");
        var testContent = "This is a small test file.";
        await File.WriteAllTextAsync(sourceFile, testContent);

        // Act
        var result = await _copyMethod.DeployFileAsync(sourceFile, targetFile);

        // Assert
        Assert.True(result);
        Assert.True(File.Exists(targetFile));
        var copiedContent = await File.ReadAllTextAsync(targetFile);
        Assert.Equal(testContent, copiedContent);
    }

    [Fact]
    public async Task DeployFile_Should_Copy_Large_File_Successfully()
    {
        // Arrange
        var sourceFile = Path.Combine(_testSourceDir, "large_test.bin");
        var targetFile = Path.Combine(_testTargetDir, "large_test.bin");

        // 创建一个 15MB 的文件（大于 10MB 阈值）
        var largeData = new byte[15 * 1024 * 1024];
        new Random().NextBytes(largeData);
        await File.WriteAllBytesAsync(sourceFile, largeData);

        // Act
        var result = await _copyMethod.DeployFileAsync(sourceFile, targetFile);

        // Assert
        Assert.True(result);
        Assert.True(File.Exists(targetFile));
        var copiedData = await File.ReadAllBytesAsync(targetFile);
        Assert.Equal(largeData.Length, copiedData.Length);
    }

    [Fact]
    public async Task DeployFile_Should_Overwrite_Existing_File()
    {
        // Arrange
        var sourceFile = Path.Combine(_testSourceDir, "overwrite_test.txt");
        var targetFile = Path.Combine(_testTargetDir, "overwrite_test.txt");

        await File.WriteAllTextAsync(sourceFile, "New content");
        await File.WriteAllTextAsync(targetFile, "Old content");

        // Act
        var result = await _copyMethod.DeployFileAsync(sourceFile, targetFile);

        // Assert
        Assert.True(result);
        var content = await File.ReadAllTextAsync(targetFile);
        Assert.Equal("New content", content);
    }

    [Fact]
    public async Task DeployFile_Should_Fail_When_Source_Not_Exists()
    {
        // Arrange
        var nonExistentSource = Path.Combine(_testSourceDir, "nonexistent.txt");
        var targetFile = Path.Combine(_testTargetDir, "target.txt");

        // Act
        var result = await _copyMethod.DeployFileAsync(nonExistentSource, targetFile);

        // Assert
        Assert.False(result);
        Assert.False(File.Exists(targetFile));
    }

    [Fact]
    public async Task DeployFile_Should_Create_Target_Directory_If_Not_Exists()
    {
        // Arrange
        var sourceFile = Path.Combine(_testSourceDir, "test.txt");
        var nestedTargetDir = Path.Combine(_testTargetDir, "nested", "deep", "directory");
        var targetFile = Path.Combine(nestedTargetDir, "test.txt");

        await File.WriteAllTextAsync(sourceFile, "Test content");

        // Act
        var result = await _copyMethod.DeployFileAsync(sourceFile, targetFile);

        // Assert
        Assert.True(result);
        Assert.True(Directory.Exists(nestedTargetDir));
        Assert.True(File.Exists(targetFile));
    }

    [Fact]
    public async Task DeployFile_Should_Report_Progress()
    {
        // Arrange
        var sourceFile = Path.Combine(_testSourceDir, "progress_test.txt");
        var targetFile = Path.Combine(_testTargetDir, "progress_test.txt");
        await File.WriteAllTextAsync(sourceFile, "Test content");

        var progressReports = new List<string>();
        var progress = new Progress<string>(report => progressReports.Add(report));

        // Act
        var result = await _copyMethod.DeployFileAsync(sourceFile, targetFile, progress);

        // Assert
        Assert.True(result);
        Assert.NotEmpty(progressReports);
        Assert.Contains(progressReports, r => r.Contains("Copying"));
        Assert.Contains(progressReports, r => r.Contains("Completed"));
    }

    [Fact]
    public async Task DeployFile_Should_Handle_Cancellation()
    {
        // Arrange
        var sourceFile = Path.Combine(_testSourceDir, "cancel_test.bin");
        var targetFile = Path.Combine(_testTargetDir, "cancel_test.bin");

        // 创建一个 20MB 的文件
        var largeData = new byte[20 * 1024 * 1024];
        await File.WriteAllBytesAsync(sourceFile, largeData);

        var cts = new CancellationTokenSource();
        cts.CancelAfter(10); // 10ms 后取消

        // Act
        var result = await _copyMethod.DeployFileAsync(sourceFile, targetFile, null, cts.Token);

        // Assert
        Assert.False(result);
        // 如果文件存在，应该是不完整的或已被清理
        if (File.Exists(targetFile))
        {
            var copiedSize = new FileInfo(targetFile).Length;
            Assert.True(copiedSize < largeData.Length, "File should be incomplete or cleaned up");
        }
    }

    [Fact]
    public async Task CleanupFile_Should_Delete_Existing_File()
    {
        // Arrange
        var targetFile = Path.Combine(_testTargetDir, "cleanup_test.txt");
        await File.WriteAllTextAsync(targetFile, "Test content");
        Assert.True(File.Exists(targetFile));

        // Act
        var result = await _copyMethod.CleanupFileAsync(targetFile);

        // Assert
        Assert.True(result);
        Assert.False(File.Exists(targetFile));
    }

    [Fact]
    public async Task CleanupFile_Should_Succeed_When_File_Not_Exists()
    {
        // Arrange
        var nonExistentFile = Path.Combine(_testTargetDir, "nonexistent.txt");

        // Act
        var result = await _copyMethod.CleanupFileAsync(nonExistentFile);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task DeployFile_Should_Handle_Multiple_Files_In_Parallel()
    {
        // Arrange
        var files = new List<(string source, string target)>();
        for (int i = 0; i < 10; i++)
        {
            var sourceFile = Path.Combine(_testSourceDir, $"parallel_test_{i}.txt");
            var targetFile = Path.Combine(_testTargetDir, $"parallel_test_{i}.txt");
            await File.WriteAllTextAsync(sourceFile, $"Content {i}");
            files.Add((sourceFile, targetFile));
        }

        // Act
        var tasks = files.Select(f => _copyMethod.DeployFileAsync(f.source, f.target));
        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.All(results, result => Assert.True(result));
        Assert.All(files, f => Assert.True(File.Exists(f.target)));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testSourceDir))
            {
                Directory.Delete(_testSourceDir, recursive: true);
            }
        }
        catch { }

        try
        {
            if (Directory.Exists(_testTargetDir))
            {
                Directory.Delete(_testTargetDir, recursive: true);
            }
        }
        catch { }
    }
}
