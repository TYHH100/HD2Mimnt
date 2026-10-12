using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Services;

namespace HD2Mimnt.Application.Services;

/// <summary>导入与迁移共用有序相对路径及文件完整内容的 SHA-256 指纹。</summary>
public static class ModHashService
{
    public static Task<string> CalculateAsync(string directory, CancellationToken cancellationToken = default,
        IProgress<ModImportProgress>? progress = null)
        => ModDirectoryHash.CalculateAsync(directory, cancellationToken, progress);
}
