using HD2Mimnt.Core.Interfaces;
using System.IO;
using System.Runtime.InteropServices;

namespace HD2Mimnt.Infrastructure.Deployment;

/// <summary>
/// 符号链接部署方法
/// 通过创建符号链接将 Mod 文件部署到目标目录
/// Windows 需要管理员权限，Linux/Mac 不需要
/// </summary>
public class SymbolicLinkDeploymentMethod : IDeploymentMethod
{
    /// <summary>
    /// 部署方法名称
    /// </summary>
    public string Name => "SymbolicLink";

    /// <summary>
    /// 检查符号链接部署的兼容性
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

        // Windows 平台需要检查管理员权限
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!IsAdministrator())
            {
                return (false, "Administrator privileges are required to create symbolic links on Windows. Please run the application as administrator.");
            }
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

        // 尝试创建一个测试符号链接来验证权限
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
                // 尝试创建符号链接
                File.CreateSymbolicLink(testTarget, testSource);

                // 验证符号链接是否有效
                if (!File.Exists(testTarget))
                {
                    return (false, "Symbolic link was created but is not accessible.");
                }

                // 清理测试文件
                File.Delete(testTarget);
            }
            finally
            {
                File.Delete(testSource);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "Access denied. Administrator privileges may be required.");
        }
        catch (Exception ex)
        {
            return (false, $"Cannot create symbolic links: {ex.Message}");
        }

        return (true, null);
    }

    /// <summary>
    /// 异步部署单个文件（创建符号链接）
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

            // 如果目标已存在，先删除
            if (File.Exists(targetPath))
            {
                // 检查是否是符号链接
                var attrs = File.GetAttributes(targetPath);
                if ((attrs & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                {
                    // 是符号链接，直接删除
                    File.Delete(targetPath);
                }
                else
                {
                    // 不是符号链接，可能是实际文件，报告警告
                    progress?.Report($"Warning: Target exists and is not a symbolic link: {targetPath}");
                    File.Delete(targetPath);
                }
            }

            progress?.Report($"Creating symlink: {Path.GetFileName(targetPath)}");

            // 创建符号链接
            File.CreateSymbolicLink(targetPath, sourcePath);

            // 验证符号链接是否有效
            if (!File.Exists(targetPath))
            {
                progress?.Report($"Failed: Symbolic link created but not accessible: {targetPath}");
                return false;
            }

            progress?.Report($"Completed: {Path.GetFileName(targetPath)}");
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            progress?.Report($"Failed: Access denied - Administrator privileges may be required");
            return false;
        }
        catch (Exception ex)
        {
            progress?.Report($"Failed: {Path.GetFileName(sourcePath)} - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 清理已部署的符号链接
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
                // 检查是否是符号链接
                var attrs = File.GetAttributes(targetPath);
                if ((attrs & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                {
                    // 是符号链接，安全删除
                    File.Delete(targetPath);
                    return true;
                }
                else
                {
                    // 不是符号链接，不应该由我们删除
                    // 但为了兼容性，我们仍然返回 false 让调用者知道
                    return false;
                }
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
    /// 检查当前进程是否以管理员身份运行（仅 Windows）
    /// </summary>
    private static bool IsAdministrator()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // 非 Windows 平台，符号链接不需要管理员权限
            return true;
        }

        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // 无法检查权限，假设没有权限
            return false;
        }
    }
}
