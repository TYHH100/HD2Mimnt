namespace HD2Mimnt.Core.Services;

/// <summary>
/// 模组目录轻量内容戳：递归文件数、总字节与最大 UTC 修改时间合成。
/// 不读文件内容，用于廉价检测模组目录是否变化；目录不存在时返回空串。
/// </summary>
public static class ModContentStamp
{
    public static string Calculate(string directory)
    {
        if (!Directory.Exists(directory)) return string.Empty;
        var count = 0;
        long totalBytes = 0;
        long maxTicks = 0;
        var luaRoot = Path.Combine(directory, ModDerivedContent.LuaFolderName);
        foreach (var path in Directory.EnumerateFiles(directory, "*", new EnumerationOptions
                 { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            // 拆包输出是派生审查产物，不改变模组内容身份。
            if (path.StartsWith(luaRoot, StringComparison.OrdinalIgnoreCase)
                && (path.Length == luaRoot.Length || path[luaRoot.Length] is '\\' or '/')) continue;
            var info = new FileInfo(path);
            if (!info.Exists) continue;
            count++;
            totalBytes += info.Length;
            var ticks = info.LastWriteTimeUtc.Ticks;
            if (ticks > maxTicks) maxTicks = ticks;
        }
        return FormattableString.Invariant($"v1:{count}:{totalBytes}:{maxTicks}");
    }
}
