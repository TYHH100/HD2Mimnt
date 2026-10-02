using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Deployment;
using System.Runtime.InteropServices;

namespace HD2Mimnt.Tests.Deployment;

public class SymbolicLinkDeploymentMethodTests : IDisposable
{
    private readonly string _testSourceDir;
    private readonly string _testTargetDir;
    private readonly SymbolicLinkDeploymentMethod _symlinkMethod;
    private readonly bool _canCreateSymlinks;

    public SymbolicLinkDeploymentMethodTests()
    {
        _testSourceDir = Path.Combine(Path.GetTempPath(), $"hd2_test_symlink_source_{Guid.NewGuid()}");
        _testTargetDir = Path.Combine(Path.GetTempPath(), $"hd2_test_symlink_target_{Guid.NewGuid()}");

        Directory.CreateDirectory(_testSourceDir);
        Directory.CreateDirectory(_testTargetDir);

        _symlinkMethod = new SymbolicLinkDeploymentMethod();
        _canCreateSymlinks = CheckCanCreateSymlinks();
    }

    private bool CheckCanCreateSymlinks()
    {
        // 在非 Windows 平台上，总是可以创建符号链接
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return true;
        }

        // 在 Windows 上，尝试创建一个测试符号链接
        try
        {
            var testSource = Path.Combine(_testSourceDir, "test_permission.txt");
            var testTarget = Path.Combine(_testTargetDir, "test_permission_link.txt");

            File.WriteAllText(testSource, "test");
            File.CreateSymbolicLink(testTarget, testSource);
            File.Delete(testTarget);
            File.Delete(testSource);

            return true;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task Name_Should_Return_SymbolicLink()
    {
        // Assert
        Assert.Equal("SymbolicLink", _symlinkMethod.Name);
    }

    [Fact]
    public async Task CheckCompatibility_Should_Check_Source_Directory()
    {
        // Arrange
        var nonExistentDir = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid()}");

        // Act
        var (canDeploy, errorMessage) = await _symlinkMethod.CheckCompatibilityAsync(
            nonExistentDir,
            _testTargetDir);

        // Assert
        Assert.False(canDeploy);
        Assert.NotNull(errorMessage);
        Assert.Contains("does not exist", errorMessage);
    }

    [Fact]
    public async Task DeployFile_Should_Create_Symbolic_Link()
    {
        // Skip if no permission to create symlinks
        if (!_canCreateSymlinks)
        {
            return;
        }

        // Arrange
        var sourceFile = Path.Combine(_testSourceDir, "test.txt");
        var targetFile = Path.Combine(_testTargetDir, "test.txt");
        var testContent = "This is a test file for symbolic link.";
        await File.WriteAllTextAsync(sourceFile, testContent);

        // Act
        var result = await _symlinkMethod.DeployFileAsync(sourceFile, targetFile);

        // Assert
        Assert.True(result);
        Assert.True(File.Exists(targetFile));

        // 验证是符号链接
        var attrs = File.GetAttributes(targetFile);
        Assert.True((attrs & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint);

        // 验证内容可以通过符号链接访问
        var linkedContent = await File.ReadAllTextAsync(targetFile);
        Assert.Equal(testContent, linkedContent);
    }

    [Fact]
    public async Task DeployFile_Should_Overwrite_Existing_Symlink()
    {
        // Skip if no permission to create symlinks
        if (!_canCreateSymlinks)
        {
            return;
        }

        // Arrange
        var sourceFile1 = Path.Combine(_testSourceDir, "file1.txt");
        var sourceFile2 = Path.Combine(_testSourceDir, "file2.txt");
        var targetFile = Path.Combine(_testTargetDir, "target.txt");

        await File.WriteAllTextAsync(sourceFile1, "Content 1");
        await File.WriteAllTextAsync(sourceFile2, "Content 2");

        // 创建第一个符号链接
        await _symlinkMethod.DeployFileAsync(sourceFile1, targetFile);
        Assert.Equal("Content 1", await File.ReadAllTextAsync(targetFile));

        // Act - 用第二个源文件覆盖
        var result = await _symlinkMethod.DeployFileAsync(sourceFile2, targetFile);

        // Assert
        Assert.True(result);
        var content = await File.ReadAllTextAsync(targetFile);
        Assert.Equal("Content 2", content);
    }

    [Fact]
    public async Task DeployFile_Should_Fail_When_Source_Not_Exists()
    {
        // Arrange
        var nonExistentSource = Path.Combine(_testSourceDir, "nonexistent.txt");
        var targetFile = Path.Combine(_testTargetDir, "target.txt");

        // Act
        var result = await _symlinkMethod.DeployFileAsync(nonExistentSource, targetFile);

        // Assert
        Assert.False(result);
        Assert.False(File.Exists(targetFile));
    }

    [Fact]
    public async Task DeployFile_Should_Create_Target_Directory_If_Not_Exists()
    {
        // Skip if no permission to create symlinks
        if (!_canCreateSymlinks)
        {
            return;
        }

        // Arrange
        var sourceFile = Path.Combine(_testSourceDir, "test.txt");
        var nestedTargetDir = Path.Combine(_testTargetDir, "nested", "deep", "directory");
        var targetFile = Path.Combine(nestedTargetDir, "test.txt");

        await File.WriteAllTextAsync(sourceFile, "Test content");

        // Act
        var result = await _symlinkMethod.DeployFileAsync(sourceFile, targetFile);

        // Assert
        Assert.True(result);
        Assert.True(Directory.Exists(nestedTargetDir));
        Assert.True(File.Exists(targetFile));
    }

    [Fact]
    public async Task DeployFile_Should_Report_Progress()
    {
        // Skip if no permission to create symlinks
        if (!_canCreateSymlinks)
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
        var result = await _symlinkMethod.DeployFileAsync(sourceFile, targetFile, progress);

        // Assert
        Assert.True(result);
        Assert.NotEmpty(progressReports);
        Assert.Contains(progressReports, r => r.Contains("Creating symlink"));
        Assert.Contains(progressReports, r => r.Contains("Completed"));
    }

    [Fact]
    public async Task DeployFile_Should_Handle_Multiple_Files_In_Parallel()
    {
        // Skip if no permission to create symlinks
        if (!_canCreateSymlinks)
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
        var tasks = files.Select(f => _symlinkMethod.DeployFileAsync(f.source, f.target));
        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.All(results, result => Assert.True(result));
        Assert.All(files, f => Assert.True(File.Exists(f.target)));

        // 验证所有都是符号链接
        foreach (var (_, target) in files)
        {
            var attrs = File.GetAttributes(target);
            Assert.True((attrs & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint);
        }
    }

    [Fact]
    public async Task CleanupFile_Should_Delete_Symbolic_Link()
    {
        // Skip if no permission to create symlinks
        if (!_canCreateSymlinks)
        {
            return;
        }

        // Arrange
        var sourceFile = Path.Combine(_testSourceDir, "cleanup_test.txt");
        var targetFile = Path.Combine(_testTargetDir, "cleanup_test.txt");
        await File.WriteAllTextAsync(sourceFile, "Test content");

        await _symlinkMethod.DeployFileAsync(sourceFile, targetFile);
        Assert.True(File.Exists(targetFile));

        // Act
        var result = await _symlinkMethod.CleanupFileAsync(targetFile);

        // Assert
        Assert.True(result);
        Assert.False(File.Exists(targetFile));

        // 源文件应该仍然存在
        Assert.True(File.Exists(sourceFile));
    }

    [Fact]
    public async Task CleanupFile_Should_Succeed_When_File_Not_Exists()
    {
        // Arrange
        var nonExistentFile = Path.Combine(_testTargetDir, "nonexistent.txt");

        // Act
        var result = await _symlinkMethod.CleanupFileAsync(nonExistentFile);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task CleanupFile_Should_Not_Delete_Regular_File()
    {
        // Arrange
        var regularFile = Path.Combine(_testTargetDir, "regular_file.txt");
        await File.WriteAllTextAsync(regularFile, "Regular file content");

        // Act
        var result = await _symlinkMethod.CleanupFileAsync(regularFile);

        // Assert
        Assert.False(result); // 应该返回 false，因为不是符号链接
        Assert.True(File.Exists(regularFile)); // 文件应该仍然存在
    }

    [Fact]
    public async Task SymbolicLink_Should_Reflect_Source_Changes()
    {
        // Skip if no permission to create symlinks
        if (!_canCreateSymlinks)
        {
            return;
        }

        // Arrange
        var sourceFile = Path.Combine(_testSourceDir, "dynamic_test.txt");
        var targetFile = Path.Combine(_testTargetDir, "dynamic_test.txt");

        await File.WriteAllTextAsync(sourceFile, "Original content");
        await _symlinkMethod.DeployFileAsync(sourceFile, targetFile);

        // Act - 修改源文件
        await File.WriteAllTextAsync(sourceFile, "Modified content");

        // Assert - 通过符号链接读取应该得到修改后的内容
        var content = await File.ReadAllTextAsync(targetFile);
        Assert.Equal("Modified content", content);
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
