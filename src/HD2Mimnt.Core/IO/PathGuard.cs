namespace HD2Mimnt.Core.IO;

/// <summary>
/// 共享的“路径必须位于根目录内”校验，统一 GetFullPath 规范化与 Windows 大小写不敏感比较。
/// </summary>
public static class PathGuard
{
    /// <summary>
    /// 判断 <paramref name="path"/> 规范化后是否严格位于 <paramref name="root"/> 之内；
    /// 恰好等于根目录返回 false。两侧输入都会先做 GetFullPath 规范化。
    /// </summary>
    public static bool IsInsideRoot(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(path);
        return full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>校验路径在根内并返回其绝对路径形式；越界抛 <see cref="InvalidDataException"/>。</summary>
    public static string ResolveInsideRoot(string root, string path, string? errorMessage = null)
    {
        if (!IsInsideRoot(root, path))
        {
            throw new InvalidDataException(errorMessage ?? "路径越界。");
        }
        return Path.GetFullPath(path);
    }
}
