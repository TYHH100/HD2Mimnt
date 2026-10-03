using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.IO.Compression;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 归档文件提取服务
/// 负责从压缩包中提取文件用于部署
/// </summary>
public class ArchiveExtractionService
{
    private readonly ILogger<ArchiveExtractionService>? _logger;
    private readonly string _tempExtractionPath;

    public ArchiveExtractionService(ILogger<ArchiveExtractionService>? logger = null)
    {
        _logger = logger;
        _tempExtractionPath = Path.Combine(Path.GetTempPath(), "HD2Mimnt", "Extraction");

        // 确保临时目录存在
        if (!Directory.Exists(_tempExtractionPath))
        {
            Directory.CreateDirectory(_tempExtractionPath);
        }
    }

    /// <summary>
    /// 从归档中提取文件到临时位置
    /// </summary>
    /// <param name="archivePath">归档文件路径</param>
    /// <param name="internalPath">归档内部路径</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>提取后的临时文件路径</returns>
    public async Task<string> ExtractFileAsync(
        string archivePath,
        string internalPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException($"归档文件不存在: {archivePath}");
        }

        var extension = Path.GetExtension(archivePath).ToLowerInvariant();

        return extension switch
        {
            ".zip" => await ExtractFromZipAsync(archivePath, internalPath, cancellationToken),
            _ => throw new NotSupportedException($"不支持的归档格式: {extension}")
        };
    }

    /// <summary>
    /// 从 ZIP 归档中提取文件
    /// </summary>
    private async Task<string> ExtractFromZipAsync(
        string zipPath,
        string internalPath,
        CancellationToken cancellationToken = default)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        // 查找条目（支持不同的路径分隔符）
        var normalizedInternalPath = internalPath.Replace('\\', '/');
        var entry = archive.Entries.FirstOrDefault(e =>
            e.FullName.Replace('\\', '/').Equals(normalizedInternalPath, StringComparison.OrdinalIgnoreCase));

        if (entry == null)
        {
            throw new FileNotFoundException($"归档中未找到文件: {internalPath}");
        }

        // 生成临时文件路径
        var tempFileName = $"{Guid.NewGuid()}_{Path.GetFileName(internalPath)}";
        var tempFilePath = Path.Combine(_tempExtractionPath, tempFileName);

        _logger?.LogDebug("提取文件: {InternalPath} -> {TempPath}", internalPath, tempFilePath);

        // 提取文件
        using (var entryStream = entry.Open())
        using (var fileStream = File.Create(tempFilePath))
        {
            await entryStream.CopyToAsync(fileStream, cancellationToken);
        }

        return tempFilePath;
    }

    /// <summary>
    /// 批量提取文件
    /// </summary>
    /// <param name="archivePath">归档文件路径</param>
    /// <param name="internalPaths">归档内部路径列表</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>内部路径 -> 临时文件路径的映射</returns>
    public async Task<Dictionary<string, string>> ExtractFilesAsync(
        string archivePath,
        IEnumerable<string> internalPaths,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>();

        foreach (var internalPath in internalPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var tempPath = await ExtractFileAsync(archivePath, internalPath, cancellationToken);
                result[internalPath] = tempPath;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "提取文件失败: {InternalPath}", internalPath);
            }
        }

        return result;
    }

    /// <summary>
    /// 计算文件哈希
    /// </summary>
    public async Task<string> CalculateFileHashAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return string.Empty;
        }

        using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// 快速指纹哈希（首尾各 1KB）
    /// 用于大文件的快速比对
    /// </summary>
    public async Task<string> CalculateFastFingerprintAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return string.Empty;
        }

        var fileInfo = new FileInfo(filePath);
        using var stream = File.OpenRead(filePath);

        var buffer = new byte[Math.Min(1024, fileInfo.Length)];
        await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

        if (fileInfo.Length > 1024)
        {
            stream.Seek(-1024, SeekOrigin.End);
            var tailBuffer = new byte[1024];
            await stream.ReadAsync(tailBuffer, 0, tailBuffer.Length, cancellationToken);

            // 合并首尾数据
            var combined = new byte[buffer.Length + tailBuffer.Length];
            Buffer.BlockCopy(buffer, 0, combined, 0, buffer.Length);
            Buffer.BlockCopy(tailBuffer, 0, combined, buffer.Length, tailBuffer.Length);

            var hash = SHA256.HashData(combined);
            return Convert.ToBase64String(hash);
        }
        else
        {
            var hash = SHA256.HashData(buffer);
            return Convert.ToBase64String(hash);
        }
    }

    /// <summary>
    /// 清理临时提取的文件
    /// </summary>
    /// <param name="tempFilePath">临时文件路径</param>
    public void CleanupTempFile(string tempFilePath)
    {
        try
        {
            if (File.Exists(tempFilePath) && tempFilePath.StartsWith(_tempExtractionPath))
            {
                File.Delete(tempFilePath);
                _logger?.LogDebug("已清理临时文件: {TempPath}", tempFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "清理临时文件失败: {TempPath}", tempFilePath);
        }
    }

    /// <summary>
    /// 清理所有临时文件
    /// </summary>
    public void CleanupAllTempFiles()
    {
        try
        {
            if (Directory.Exists(_tempExtractionPath))
            {
                var files = Directory.GetFiles(_tempExtractionPath);
                foreach (var file in files)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "删除临时文件失败: {File}", file);
                    }
                }

                _logger?.LogInformation("已清理 {Count} 个临时文件", files.Length);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "清理临时目录失败: {TempPath}", _tempExtractionPath);
        }
    }
}
