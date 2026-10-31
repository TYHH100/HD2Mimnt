using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Services;

/// <summary>模组目录指纹：对有序相对路径及每个文件的完整 SHA-256 摘要再次计算 SHA-256。
/// 导入时自动生成的档案不参与，保证同一内容从不同来源导入得到相同指纹。</summary>
public static class ModDirectoryHash
{
    /// <param name="stepName">进度阶段名；判重等二次指纹计算用独立阶段名避免误报“计算哈希”。</param>
    /// <param name="startPercentage">进度区间起点；spanPercentage 为 0 时保持起点不动，仅刷新文件明细。</param>
    /// <param name="spanPercentage">进度区间跨度；调用方负责分配不回退的区间。</param>
    public static async Task<string> CalculateAsync(string directory, CancellationToken cancellationToken = default,
        IProgress<ModImportProgress>? progress = null, Func<string, bool>? includeFile = null,
        string stepName = "计算哈希", double startPercentage = 84, double spanPercentage = 5)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"模组目录不存在: {directory}");
        progress?.Report(new(startPercentage, stepName, "准备计算模组内容 SHA-256", true));
        var generatedManifest = ManifestPackage.IsGeneratedManifest(directory);
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => includeFile?.Invoke(path) != false)
            .Where(path => !generatedManifest || !IsGeneratedManifestFile(directory, path))
            .Select(path => (Path: path, Relative: Path.GetRelativePath(directory, path).Replace('\\', '/')))
            .OrderBy(file => file.Relative, StringComparer.Ordinal).ToArray();
        long totalBytes = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalBytes += new FileInfo(file.Path).Length;
        }

        using var directoryHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // 固定格式标识及长度前缀避免路径/内容拼接歧义；根目录和压缩包元数据不参与。
        directoryHash.AppendData("HD2Mimnt.Directory.SHA256.v1\0"u8);
        var lengthBuffer = new byte[4];
        var buffer = new byte[81920];
        long processedBytes = 0;
        var lastReport = Environment.TickCount64;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pathBytes = Encoding.UTF8.GetBytes(file.Relative);
            BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, pathBytes.Length);
            directoryHash.AppendData(lengthBuffer);
            directoryHash.AppendData(pathBytes);
            using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                fileHash.AppendData(buffer, 0, read);
                processedBytes += read;
                if (Environment.TickCount64 - lastReport >= 100)
                {
                    var ratio = totalBytes == 0 ? 1 : Math.Clamp((double)processedBytes / totalBytes, 0, 1);
                    progress?.Report(new(startPercentage + spanPercentage * ratio, stepName, file.Relative));
                    lastReport = Environment.TickCount64;
                }
            }
            directoryHash.AppendData(fileHash.GetHashAndReset());
        }
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(startPercentage + spanPercentage, stepName, "SHA-256 计算完成"));
        return Convert.ToHexStringLower(directoryHash.GetHashAndReset());
    }

    private static bool IsGeneratedManifestFile(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
            || relative.Equals(ManifestPackage.MetadataFileName, StringComparison.OrdinalIgnoreCase);
    }
}
