using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Services;

namespace HD2Mimnt.Application.Services;

/// <summary>导入与迁移共用有序相对路径及文件完整内容的 SHA-256 指纹。</summary>
public static class ModHashService
{
    public static Task<string> CalculateAsync(string directory, CancellationToken cancellationToken = default,
        IProgress<ModImportProgress>? progress = null)
        => ModDirectoryHash.CalculateAsync(directory, cancellationToken, progress);

    /// <summary>一次读取同时得到目录哈希与判重指纹；导入/回填哈希时都用它，避免同一内容读两遍。</summary>
    public static Task<(string Hash, string Fingerprint)> CalculateWithFingerprintAsync(string directory,
        CancellationToken cancellationToken = default, IProgress<ModImportProgress>? progress = null,
        string stepName = "计算哈希", double startPercentage = 84, double spanPercentage = 5)
        => ModDirectoryHash.CalculateWithFingerprintAsync(directory, cancellationToken, progress,
            AuthorContentFilter(directory), stepName, startPercentage, spanPercentage);

    /// <summary>判重只比较作者内容：生成身份（组件映射、组件选项树、导入自动生成的档案）不计入。</summary>
    public static Func<string, bool> AuthorContentFilter(string root) => path =>
    {
        // 只有根目录下的这几种文件属于生成身份；其余文件直接放行，避免为每个文件计算相对路径。
        var name = Path.GetFileName(path.AsSpan());
        if (!IsGeneratedContentName(name)) return true;
        var relative = Path.GetRelativePath(root, path);
        return !relative.AsSpan().Equals(name, StringComparison.OrdinalIgnoreCase);
    };

    private static bool IsGeneratedContentName(ReadOnlySpan<char> name)
        => name.Equals(Hd2IrIntegrationService.MetadataFile, StringComparison.OrdinalIgnoreCase)
            || name.Equals(Hd2IrIntegrationService.OptionsFile, StringComparison.OrdinalIgnoreCase)
            || name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals(ManifestPackage.MetadataFileName, StringComparison.OrdinalIgnoreCase);
}
