using HD2Mimnt.Core.Interfaces;
using System.IO;

namespace HD2Mimnt.Infrastructure.Deployment;

/// <summary>
/// 文件复制部署方法
/// 通过异步文件复制将 Mod 文件部署到目标目录
/// </summary>
public class CopyDeploymentMethod : IDeploymentMethod
{
    private const int DefaultBufferSize = 81920; // 80KB buffer for streaming
    private const long LargeFileThreshold = 10 * 1024 * 1024; // 10MB

    /// <summary>
    /// 部署方法名称
    /// </summary>
    public string Name => "Copy";

    /// <summary>
    /// 检查文件复制部署的兼容性
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

        // 检查目标目录是否可写
        try
        {
            if (!Directory.Exists(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            // 尝试创建一个临时文件来验证写权限
            var testFile = Path.Combine(targetDirectory, $".hd2mimnt_test_{Guid.NewGuid()}");
            await File.WriteAllTextAsync(testFile, "test", cancellationToken);
            File.Delete(testFile);
        }
        catch (Exception ex)
        {
            return (false, $"Target directory is not writable: {ex.Message}");
        }

        return (true, null);
    }

    /// <summary>
    /// 异步部署单个文件（复制）
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

            // 如果目标文件已存在，先删除
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            // 获取文件大小以决定使用哪种复制策略
            var fileInfo = new FileInfo(sourcePath);
            var fileSize = fileInfo.Length;

            progress?.Report($"Copying: {Path.GetFileName(sourcePath)} ({FormatFileSize(fileSize)})");

            // 小文件：直接使用 File.Copy（性能更好）
            // 大文件：使用流式复制（支持取消和进度报告）
            if (fileSize < LargeFileThreshold)
            {
                // 小文件：异步复制（实际上是在后台线程执行同步复制）
                await Task.Run(() => File.Copy(sourcePath, targetPath, overwrite: true), cancellationToken);
            }
            else
            {
                // 大文件：流式异步复制
                await CopyLargeFileAsync(sourcePath, targetPath, fileSize, progress, cancellationToken);
            }

            // 取消可能在最后一次写入完成后到达，完成通知前仍要尊重它。
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Completed: {Path.GetFileName(sourcePath)}");
            return true;
        }
        catch (OperationCanceledException)
        {
            progress?.Report($"Cancelled: {Path.GetFileName(sourcePath)}");

            // 清理部分复制的文件
            if (File.Exists(targetPath))
            {
                try
                {
                    File.Delete(targetPath);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            progress?.Report($"Failed: {Path.GetFileName(sourcePath)} - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 流式复制大文件，支持取消和进度报告
    /// </summary>
    private async Task CopyLargeFileAsync(
        string sourcePath,
        string targetPath,
        long fileSize,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        await using var sourceStream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            DefaultBufferSize,
            useAsync: true);

        await using var targetStream = new FileStream(
            targetPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            DefaultBufferSize,
            useAsync: true);

        var buffer = new byte[DefaultBufferSize];
        long totalBytesRead = 0;
        int bytesRead;
        int lastReportedProgress = -1;

        while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            await targetStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
            totalBytesRead += bytesRead;

            // 报告进度（每 10% 报告一次，避免过于频繁）
            var progressPercent = (int)((totalBytesRead * 100) / fileSize);
            if (progressPercent / 10 > lastReportedProgress / 10)
            {
                lastReportedProgress = progressPercent;
                progress?.Report($"Copying: {Path.GetFileName(sourcePath)} - {progressPercent}%");
            }
        }
    }

    /// <summary>
    /// 清理已部署的文件
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
    /// 格式化文件大小为可读字符串
    /// </summary>
    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }
}
