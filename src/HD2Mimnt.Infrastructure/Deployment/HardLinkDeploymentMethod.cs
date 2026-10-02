using HD2Mimnt.Core.Interfaces;
using System.IO;
using System.Runtime.InteropServices;

namespace HD2Mimnt.Infrastructure.Deployment;

/// <summary>
/// 硬链接部署方法
/// 通过创建硬链接将 Mod 文件部署到目标目录
/// 要求源和目标在同一卷（分区）上
/// </summary>
public class HardLinkDeploymentMethod : IDeploymentMethod
{
    /// <summary>
    /// 部署方法名称
    /// </summary>
    public string Name => "HardLink";

    /// <summary>
    /// 检查硬链接部署的兼容性
    /// </summary>
    /// <param name="sourceDirectory">源目录</param>
    /// <param name="targetDirectory">目标目录</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>兼容性检查结果</returns>
    public async Task<(bool CanDeploy, string? ErrorMessage)> CheckCompatibilityAsync(
        string sourceDirectory,
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        await Task.Yield(); // 确保异步执行

        // 检查源目录是否存在
        if (!Directory.Exists(sourceDirectory))
        {
            return (false, $"Source directory does not exist: {sourceDirectory}");
        }

        // 检查目标目录的父目录是否存在
        var targetParent = Path.GetDirectoryName(targetDirectory);
        if (!string.IsNullOrEmpty(targetParent) && !Directory.Exists(targetParent))
        {
            try
            {
                Directory.CreateDirectory(targetParent);
            }
            catch (Exception ex)
            {
                return (false, $"Cannot create target directory: {ex.Message}");
            }
        }

        // 检查源和目标是否在同一卷上
        try
        {
            var sourceRoot = Path.GetPathRoot(Path.GetFullPath(sourceDirectory));
            var targetRoot = Path.GetPathRoot(Path.GetFullPath(targetDirectory));

            if (!string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"Hard links require source and target to be on the same volume. Source: {sourceRoot}, Target: {targetRoot}");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Cannot determine volume information: {ex.Message}");
        }

        // 尝试创建一个测试硬链接来验证权限和功能
        try
        {
            if (!Directory.Exists(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            var testSource = Path.Combine(sourceDirectory, $".hd2mimnt_test_source_{Guid.NewGuid()}");
            var testTarget = Path.Combine(targetDirectory, $".hd2mimnt_test_target_{Guid.NewGuid()}");

            // 创建测试源文件
            await File.WriteAllTextAsync(testSource, "test", cancellationToken);

            try
            {
                // 尝试创建硬链接
                CreateHardLink(testTarget, testSource);

                // 验证硬链接是否有效
                if (!File.Exists(testTarget))
                {
                    return (false, "Hard link was created but is not accessible.");
                }

                // 清理测试文件（硬链接删除不影响源文件）
                File.Delete(testTarget);
            }
            finally
            {
                File.Delete(testSource);
            }
        }
        catch (PlatformNotSupportedException)
        {
            return (false, "Hard links are not supported on this platform.");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "Access denied. Cannot create hard links.");
        }
        catch (Exception ex)
        {
            return (false, $"Cannot create hard links: {ex.Message}");
        }

        return (true, null);
    }

    /// <summary>
    /// 异步部署单个文件（创建硬链接）
    /// </summary>
    /// <param name="sourcePath">源文件路径</param>
    /// <param name="targetPath">目标文件路径</param>
    /// <param name="progress">进度报告</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>是否成功</returns>
    public async Task<bool> DeployFileAsync(
        string sourcePath,
        string targetPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await Task.Yield(); // 确保异步执行

        try
        {
            // 检查源文件是否存在
            if (!File.Exists(sourcePath))
            {
                progress?.Report($"Source file not found: {sourcePath}");
                return false;
            }

            // 确保目标目录存在
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // 检查源和目标是否在同一卷上
            var sourceRoot = Path.GetPathRoot(Path.GetFullPath(sourcePath));
            var targetRoot = Path.GetPathRoot(Path.GetFullPath(targetPath));

            if (!string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report($"Failed: Source and target must be on the same volume");
                return false;
            }

            // 如果目标已存在，先删除
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
                progress?.Report($"Deleted existing file: {Path.GetFileName(targetPath)}");
            }

            progress?.Report($"Creating hard link: {Path.GetFileName(targetPath)}");

            // 创建硬链接
            CreateHardLink(targetPath, sourcePath);

            // 验证硬链接是否有效
            if (!File.Exists(targetPath))
            {
                progress?.Report($"Failed: Hard link created but not accessible: {targetPath}");
                return false;
            }

            progress?.Report($"Completed: {Path.GetFileName(targetPath)}");
            return true;
        }
        catch (PlatformNotSupportedException)
        {
            progress?.Report($"Failed: Hard links not supported on this platform");
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            progress?.Report($"Failed: Access denied");
            return false;
        }
        catch (Exception ex)
        {
            progress?.Report($"Failed: {Path.GetFileName(sourcePath)} - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 清理已部署的硬链接
    /// </summary>
    /// <param name="targetPath">目标文件路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>是否成功</returns>
    public async Task<bool> CleanupFileAsync(
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        await Task.Yield(); // 确保异步执行

        try
        {
            if (File.Exists(targetPath))
            {
                // 硬链接删除只是减少引用计数
                // 不会影响其他硬链接或原始文件
                File.Delete(targetPath);
                return true;
            }

            // 文件不存在也算清理成功
            return true;
        }
        catch (Exception)
        {
            // 清理失败（例如文件被锁定）
            return false;
        }
    }

    /// <summary>
    /// 创建硬链接的平台特定实现
    /// </summary>
    private static void CreateHardLink(string linkPath, string targetPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows 平台使用 P/Invoke
            if (!CreateHardLinkWindows(linkPath, targetPath, IntPtr.Zero))
            {
                throw new IOException($"Failed to create hard link. Error code: {Marshal.GetLastWin32Error()}");
            }
        }
        else
        {
            // Unix/Linux/Mac 平台使用系统调用
            if (link(targetPath, linkPath) != 0)
            {
                throw new IOException($"Failed to create hard link. Error code: {Marshal.GetLastWin32Error()}");
            }
        }
    }

    // Windows P/Invoke
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    private static extern bool CreateHardLinkWindows(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    // Unix/Linux/Mac P/Invoke
    [DllImport("libc", SetLastError = true)]
    private static extern int link(string oldpath, string newpath);
}
