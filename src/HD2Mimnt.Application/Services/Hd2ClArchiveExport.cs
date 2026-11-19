using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Archive;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 把 HD2CL 转换产物（文件夹）打成可独立分发的 7z，写到用户选择的目录。
/// 只导出压缩包，不导入模组库、不改配置；先写临时包再改名，失败或取消不会留下半成品。
/// </summary>
internal static class Hd2ClArchiveExport
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static async Task<string> WriteAsync(string outputDirectory, string productName,
        string artifactDirectory, IProgress<string>? progress, CancellationToken token)
    {
        if (!Directory.Exists(artifactDirectory))
            throw new InvalidOperationException("没有生成可导出的成品目录。");
        Directory.CreateDirectory(outputDirectory);
        var destination = ReservePath(outputDirectory, productName);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        progress?.Report("正在打包压缩包…");
        try
        {
            await SevenZipArchiveWriter.CreateFromDirectoryAsync(temporary, artifactDirectory,
                ModArchiveCompression.Normal, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
            return destination;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var replaced = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        var trimmed = replaced.Trim().TrimEnd('.', ' ');
        if (trimmed.Length == 0) return "HD2CL";
        return ReservedNames.Contains(trimmed) ? "_" + trimmed : trimmed;
    }

    /// <summary>同名压缩包已存在时追加序号，避免静默覆盖用户已导出的文件。</summary>
    internal static string ReservePath(string directory, string productName)
    {
        var name = SanitizeFileName(productName);
        var path = Path.Combine(directory, name + ".7z");
        for (var index = 2; File.Exists(path); index++)
            path = Path.Combine(directory, $"{name} ({index}).7z");
        return path;
    }
}
