using System.Threading;
using System.Threading.Tasks;

namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// 压缩包提取器接口：统一 mod 导入时的压缩包解压能力，
/// 由 Infrastructure 提供具体实现（SharpCompress，支持 zip/7z/rar 等）。
/// </summary>
public interface IArchiveExtractor
{
    /// <summary>
    /// 判断给定文件路径是否为受支持的压缩包（按扩展名判断）。
    /// </summary>
    bool IsArchiveFile(string path);

    /// <summary>
    /// 将压缩包完整解压到目标目录（目录不存在时创建）。
    /// 支持的格式由实现决定：zip、7z、rar、tar、gz、bz2、xz 等。
    /// </summary>
    Task ExtractToDirectoryAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken = default);
}
