using HD2Mimnt.Core.IO;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Worker 经管道回传的临时文件路径的最后防线：链路本身（CurrentUserOnly 管道 +
/// Worker 只写 %TEMP%\HD2Mimnt.Preview）已可信，此校验防“收到路径即 File 操作”
/// 的模式在会话被劫持时被引导到任意路径读/删文件。
/// </summary>
internal static class PreviewPayloadGuard
{
    private static readonly string ExpectedRoot = Path.Combine(Path.GetTempPath(), "HD2Mimnt.Preview");

    /// <summary>校验路径位于预览临时目录内并返回规范化绝对路径。</summary>
    public static string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException("预览 Worker 未返回临时文件路径。");
        }

        return PathGuard.ResolveInsideRoot(ExpectedRoot, path,
            "预览临时文件路径越出约定目录，已拒绝读取。");
    }
}
