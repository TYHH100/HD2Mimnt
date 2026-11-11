using HD2Mimnt.Core.Darktide;

namespace HD2Mimnt.Infrastructure.Darktide;

/// <summary>
/// 压缩包与文件夹共用的模组包分类：识别“&lt;名称&gt;/&lt;名称&gt;.mod”候选目录，
/// 并按候选之外的文件判定 StandardMod / ModBundle / CustomInstallRequired。
/// 路径一律使用 '/' 分隔的相对路径。
/// </summary>
internal static class DarktidePackageClassifier
{
    private static readonly string[] ExecutableExtensions =
    {
        ".exe", ".dll", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".js", ".jse", ".wsf",
        ".scr", ".lnk", ".com", ".msi", ".sys", ".ocx", ".so", ".dylib"
    };

    public static (List<DarktideArchiveCandidate> Candidates, string? Error,
        DarktideArchivePackageKind Kind, List<string> ExtraFiles) Classify(
        IReadOnlyList<(string Path, long Length)> files)
    {
        var candidates = new List<DarktideArchiveCandidate>();
        var modFileCount = files.Count(file => file.Path.EndsWith(".mod", StringComparison.OrdinalIgnoreCase));
        foreach (var (path, _) in files.Where(file => file.Path.EndsWith(".mod", StringComparison.OrdinalIgnoreCase)))
        {
            var internalName = Path.GetFileNameWithoutExtension(path);
            if (internalName.Length == 0) continue;
            var root = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
            if (root.Length == 0 && modFileCount > 1)
                return (candidates, "根目录存在多个 .mod 文件，无法确定模组边界。",
                    DarktideArchivePackageKind.NotAMod, new List<string>());
            if (candidates.Any(candidate => string.Equals(candidate.RelativeRoot, root, StringComparison.OrdinalIgnoreCase)))
                continue;
            var underRoot = files.Where(file => IsUnderRoot(file.Path, root)).ToList();
            var hasScripts = underRoot.Any(file => file.Path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)
                                                    && IsUnderRoot(file.Path[root.Length..].TrimStart('/'), "scripts"));
            var hasInfo = underRoot.Any(file => string.Equals(
                root.Length == 0 ? file.Path : file.Path[(root.Length + 1)..], "info.json", StringComparison.OrdinalIgnoreCase));
            candidates.Add(new DarktideArchiveCandidate(root, internalName, Path.GetFileName(path), hasScripts, hasInfo,
                underRoot.Count, underRoot.Sum(file => Math.Max(0, file.Length))));
        }

        candidates = candidates.OrderBy(candidate => candidate.RelativeRoot.Count(c => c == '/'))
            .ThenBy(candidate => candidate.RelativeRoot, StringComparer.OrdinalIgnoreCase).ToList();
        if (candidates.Count == 0)
            return (candidates, "未找到“<名称>/<名称>.mod”结构；请确认这是 DMF 模组而不是加载器安装包。",
                DarktideArchivePackageKind.NotAMod, new List<string>());

        var roots = candidates.Select(candidate => candidate.RelativeRoot).ToList();
        var outside = files
            .Where(file => !roots.Any(root => IsUnderRoot(file.Path, root)))
            .Select(file => file.Path)
            .ToList();
        var risky = outside.Where(IsRiskyExtra).ToList();
        var kind = risky.Count > 0
            ? DarktideArchivePackageKind.CustomInstallRequired
            : candidates.Count > 1 ? DarktideArchivePackageKind.ModBundle : DarktideArchivePackageKind.StandardMod;
        return (candidates, null, kind, outside);
    }

    /// <summary>
    /// 候选模组目录之外的文件是否会影响安装结果：游戏根目录文件、可执行/脚本文件，
    /// 以及落在 mods 下但没有 .mod 的目录（作者安装流程会用到它们）。
    /// </summary>
    private static bool IsRiskyExtra(string path)
    {
        if (path.StartsWith("binaries/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("bundle/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("tools/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
            return true;
        return ExecutableExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsUnderRoot(string path, string root)
        => root.Length == 0 || path.Equals(root, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
}