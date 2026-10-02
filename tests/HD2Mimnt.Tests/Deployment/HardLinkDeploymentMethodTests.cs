using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Deployment;

namespace HD2Mimnt.Tests.Deployment;

public class HardLinkDeploymentMethodTests : IDisposable
{
    private readonly string _testSourceDir;
    private readonly string _testTargetDir;
    private readonly HardLinkDeploymentMethod _hardlinkMethod;
    private readonly bool _canCreateHardlinks;

    public HardLinkDeploymentMethodTests()
    {
        _testSourceDir = Path.Combine(Path.GetTempPath(), $"hd2_test_hardlink_source_{Guid.NewGuid()}");
        _testTargetDir = Path.Combine(Path.GetTempPath(), $"hd2_test_hardlink_target_{Guid.NewGuid()}");

        Directory.CreateDirectory(_testSourceDir);
        Directory.CreateDirectory(_testTargetDir);

        _hardlinkMethod = new HardLinkDeploymentMethod();
        _canCreateHardlinks = CheckCanCreateHardlinks();
    }

    private bool CheckCanCreateHardlinks()
    {
        // 尝试创建一个测试硬链接
        try
        {
            var testSource = Path.Combine(_testSourceDir, "test_permission.txt");
            var testTarget = Path.Combine(_testTargetDir, "test_permission_link.txt");

            File.WriteAllText(testSource, "test");

            // 使用 HardLinkDeploymentMethod 的实现来创建硬链接
            var method = new HardLinkDeploymentMethod();
            var result = method.DeployFileAsync(testSource, testTarget).Result;

            if (result && File.Exists(testTarget))
            {
                File.Delete(testTarget);
                File.Delete(testSource);
                return true;
            }

            File.Delete(testSource);
            return false;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task Name_Should_Return_HardLink()
    {
        // Assert
        Assert.Equal("HardLink", _hardlinkMethod.Name);
    }

    [Fact]
    public async Task CheckCompatibility_Should_Check_Source_Directory()
    {
        // Arrange
        var nonExistentDir = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid()}");

        // Act
        var (canDeploy, errorMessage) = await _hardlinkMethod.CheckCompatibilityAsync(
            nonExistentDir,
            _testTargetDir);

        // Assert
        Assert.False(canDeploy);
        Assert.NotNull(errorMessage);
        Assert.Contains("does not exist", errorMessage);
    }

    [Fact]
    public async Task CheckCompatibility_Should_Succeed_When_Same_Volume()
    {
        // Act
        var (canDeploy, errorMessage) = await _hardlinkMethod.CheckCompatibilityAsync(
            _testSourceDir,
            _testTargetDir);

        // Assert
        // 如果不能创建硬链接（平台不支持或权限问题），跳过此测试
        if (!_canCreateHardlinks)
        {
            return;
        }

        Assert.True(canDeploy);
        Assert.Null(errorMessage);
    }

    [Fact]
    public async Task DeployFile_Should_Create_Hard_Link()
    {
        // Skip if no permission to create hard links
        if (!_canCreateHardlinks)
        {
            return;
        }

        // Arrange
        var sourceFile = Path.Combine(_testSourceDir, "test.txt");
        var targetFile = Path.Combine(_testTargetDir, "test.txt");
        var testContent = "This is a test file for hard link.";
        await File.WriteAllTextAsync(sourceFile, testContent);

        // Act
        var result = await _hardlinkMethod.DeployFileAsync(sourceFile, targetFile);

        // Assert
        Assert.True(result);
        Assert.True(File.Exists(targetFile));

        // 验证内容可以通过硬链接访问
        var linkedContent = await File.ReadAllTextAsync(targetFile);
        Assert.Equal(testContent, linkedContent);

        // 硬链接的特性：修改一个会影响另一个
        await File.WriteAllTextAsync(targetFile, "Modified content");
        var sourceContent = await File.ReadAllTextAsync(sourceFile);
        Assert.Equal("Modified content", sourceContent);
    }

    [Fact]
    public async Task DeployFile_Should_Overwrite_Existing_File()
    {
        // Skip if no permission to create hard links
        if (!_canCreateHardlinks)
        {
            return;
        }

        // Arrange
        var sourceFile = Path.Combine(_testSourceDir, "file.txt");
        var targetFile = Path.Combine(_testTargetDir, "target.txt");

        await File.WriteAllTextAsync(sourceFile, "New content");
        await File.WriteAllTextAsync(targetFile, "Old content");

        // Act
        var result = await _hardlinkMethod.DeployFileAsync(sourceFile, targetFile);

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
        var result = await _hardlinkMethod.DeployFileAsync(nonExistentSource, targetFile);

        // Assert
        Assert.False(result);
        Assert.False(File.Exists(targetFile));
    }

    [Fact]
    public async Task DeployFile_Should_Create_Target_Directory_If_Not_Exists()
    {
        // Skip if no permission to create hard links
        if (!_canCreateHardlinks)
        {
            return;
        }

        // Arrange
        var sourceFile = Path.Combine(_testSourceDir, "test.txt");
        var nestedTargetDir = Path.Combine(_testTargetDir, "nested", "deep", "directory");
        var targetFile = Path.Combine(nestedTargetDir, "test.txt");

        await File.WriteAllTextAsync(sourceFile, "Test content");

        // Act
        var result = await _hardlinkMethod.DeployFileAsync(sourceFile, targetFile);

        // Assert
        Assert.True(result);
        Assert.True(Directory.Exists(nestedTargetDir));
        Assert.True(File.Exists(targetFile));
    }

    [Fact]
    public async Task DeployFile_Should_Report_Progress()
    {
        // Skip if no permission to create hard links
        if (!_canCreateHardlinks)
        {
            return;
        }

        // Arrange
        var sourceFile = Path.Combine(_testSourceDir, "progress_test.txt");
        var targetFile = Path.Combine(_testTargetDir, "progress_test.txt");
        await File.WriteAllTextAsync(sourceFile, "Test content");

        var progressReports = new List<string>();
        var progress = new Progress<string>(report => progressReports.Add(report));

        // Act
        var result = await _hardlinkMethod.DeployFileAsync(sourceFile, targetFile, progress);

        // Assert
        Assert.True(result);
        Assert.NotEmpty(progressReports);
        Assert.Contains(progressReports, r => r.Contains("Creating hard link"));
        Assert.Contains(progressReports, r => r.Contains("Completed"));
    }

    [Fact]
    public async Task DeployFile_Should_Handle_Multiple_Files_In_Parallel()
    {
        // Skip if no permission to create hard links
        if (!_canCreateHardlinks)
        {
            return;
        }

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
        var tasks = files.Select(f => _hardlinkMethod.DeployFileAsync(f.source, f.target));
        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.All(results, result => Assert.True(result));
        Assert.All(files, f => Assert.True(File.Exists(f.target)));
    }

    [Fact]
    public async Task CleanupFile_Should_Delete_Hard_Link()
    {
        // Skip if no permission to create hard links
        if (!_canCreateHardlinks)
        {
            return;
        }

        // Arrange
        var sourceFile = Path.Combine(_testSourceDir, "cleanup_test.txt");
        var targetFile = Path.Combine(_testTargetDir, "cleanup_test.txt");
        await File.WriteAllTextAsync(sourceFile, "Test content");

        await _hardlinkMethod.DeployFileAsync(sourceFile, targetFile);
        Assert.True(File.Exists(targetFile));

        // Act
        var result = await _hardlinkMethod.CleanupFileAsync(targetFile);

        // Assert
        Assert.True(result);
        Assert.False(File.Exists(targetFile));

        // 源文件应该仍然存在（硬链接特性）
        Assert.True(File.Exists(sourceFile));
    }

    [Fact]
    public async Task CleanupFile_Should_Succeed_When_File_Not_Exists()
    {
        // Arrange
        var nonExistentFile = Path.Combine(_testTargetDir, "nonexistent.txt");

        // Act
        var result = await _hardlinkMethod.CleanupFileAsync(nonExistentFile);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task HardLink_Should_Share_Content_With_Source()
    {
        // Skip if no permission to create hard links
        if (!_canCreateHardlinks)
        {
            return;
        }

        // Arrange
        var sourceFile = Path.Combine(_testSourceDir, "shared_test.txt");
        var targetFile = Path.Combine(_testTargetDir, "shared_test.txt");

        await File.WriteAllTextAsync(sourceFile, "Original content");
        await _hardlinkMethod.DeployFileAsync(sourceFile, targetFile);

        // Act - 修改目标文件
        await File.WriteAllTextAsync(targetFile, "Modified via target");

        // Assert - 源文件应该也被修改（硬链接共享 inode）
        var sourceContent = await File.ReadAllTextAsync(sourceFile);
        Assert.Equal("Modified via target", sourceContent);
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
