using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Services;

/// <summary>模组目录指纹：对有序相对路径及每个文件的完整 SHA-256 摘要再次计算 SHA-256。
/// 导入时自动生成的档案不参与，保证同一内容从不同来源导入得到相同指纹。</summary>
public static class ModDirectoryHash
{
    private static ReadOnlySpan<byte> FormatTag => "HD2Mimnt.Directory.SHA256.v1\0"u8;

    /// <param name="stepName">进度阶段名；判重等二次指纹计算用独立阶段名避免误报“计算哈希”。</param>
    /// <param name="startPercentage">进度区间起点；spanPercentage 为 0 时保持起点不动，仅刷新文件明细。</param>
    /// <param name="spanPercentage">进度区间跨度；调用方负责分配不回退的区间。</param>
    public static async Task<string> CalculateAsync(string directory, CancellationToken cancellationToken = default,
        IProgress<ModImportProgress>? progress = null, Func<string, bool>? includeFile = null,
        string stepName = "计算哈希", double startPercentage = 84, double spanPercentage = 5)
        => (await CalculateCoreAsync(directory, includeFile, null, cancellationToken, progress,
            stepName, startPercentage, spanPercentage)).Hash;

    /// <summary>一次读取同时得到完整目录哈希与判重指纹：两者共享同一文件枚举与排序，指纹只覆盖 <paramref name="fingerprintFilter"/> 放行的文件。
    /// 结果与分别调用两次 <see cref="CalculateAsync"/> 完全一致。</summary>
    public static Task<(string Hash, string Fingerprint)> CalculateWithFingerprintAsync(string directory,
        CancellationToken cancellationToken = default, IProgress<ModImportProgress>? progress = null,
        Func<string, bool>? fingerprintFilter = null, string stepName = "计算哈希",
        double startPercentage = 84, double spanPercentage = 5)
        => CalculateCoreAsync(directory, null, fingerprintFilter, cancellationToken, progress,
            stepName, startPercentage, spanPercentage);

    private static async Task<(string Hash, string Fingerprint)> CalculateCoreAsync(string directory,
        Func<string, bool>? includeFile, Func<string, bool>? fingerprintFilter,
        CancellationToken cancellationToken, IProgress<ModImportProgress>? progress,
        string stepName, double startPercentage, double spanPercentage)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"模组目录不存在: {directory}");
        progress?.Report(new(startPercentage, stepName, "准备计算模组内容 SHA-256", true));
        var generatedManifest = ManifestPackage.IsGeneratedManifest(directory);
        var files = new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(file => includeFile?.Invoke(file.FullName) != false)
            .Where(file => !generatedManifest || !IsGeneratedManifestFile(directory, file.FullName))
            .Select(file => (File: file, Relative: Path.GetRelativePath(directory, file.FullName).Replace('\\', '/')))
            .OrderBy(file => file.Relative, StringComparer.Ordinal).ToArray();
        long totalBytes = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalBytes += file.File.Length;
        }

        using var directoryHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var fingerprintHash = fingerprintFilter == null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // 固定格式标识及长度前缀避免路径/内容拼接歧义；根目录和压缩包元数据不参与。
        directoryHash.AppendData(FormatTag);
        fingerprintHash?.AppendData(FormatTag);
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
            await using var stream = new FileStream(file.File.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
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
            var digest = fileHash.GetHashAndReset();
            directoryHash.AppendData(digest);
            if (fingerprintHash != null && fingerprintFilter!(file.File.FullName))
            {
                fingerprintHash.AppendData(lengthBuffer);
                fingerprintHash.AppendData(pathBytes);
                fingerprintHash.AppendData(digest);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(startPercentage + spanPercentage, stepName, "SHA-256 计算完成"));
        return (Convert.ToHexStringLower(directoryHash.GetHashAndReset()),
            fingerprintHash == null ? "" : Convert.ToHexStringLower(fingerprintHash.GetHashAndReset()));
    }

    private static bool IsGeneratedManifestFile(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
            || relative.Equals(ManifestPackage.MetadataFileName, StringComparison.OrdinalIgnoreCase);
    }
}
