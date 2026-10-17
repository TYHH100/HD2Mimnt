using System.Globalization;
using System.Text.RegularExpressions;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Application.Services;

/// <summary>与旧管理器一致，只展开选中目录直属的补丁，按资源前缀累计部署编号。</summary>
internal static partial class PatchDeploymentPlanner
{
    [GeneratedRegex(@"^([a-z0-9]{16})\.patch_([0-9]+)(\.(?:stream|gpu_resources))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PatchNameRegex();

    internal sealed record FileEntry(string? SourcePath, string FileName, bool RelativeToGameRoot = false);

    internal static bool TryParseTarget(string path, out string prefix, out int index, out string suffix)
    {
        var match = PatchNameRegex().Match(Path.GetFileName(path));
        prefix = match.Groups[1].Value;
        suffix = match.Groups[3].Value;
        index = 0;
        return match.Success && int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    internal static List<string> CollectSelectedFiles(Mod mod, string? optionsJson, CancellationToken cancellationToken)
    {
        var plan = ProfileModOptionResolver.ResolveDeployPlan(mod.Manifest,
            ProfileModOptionResolver.ResolveEffective(mod.Manifest, optionsJson));
        if (plan.Scope == OptionDeployScope.InvalidSelection)
            return new();

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mod.Path));
        var directories = plan.Scope == OptionDeployScope.DirectoryList ? plan.Directories : new[] { "." };
        var files = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetFullPath(Path.Combine(root, relative));
            if ((!directory.Equals(root, StringComparison.OrdinalIgnoreCase)
                && !directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                || !visited.Add(directory) || !Directory.Exists(directory))
                continue;

            // 递归会把未选中的嵌套变体一并部署。
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var match = PatchNameRegex().Match(Path.GetFileName(file));
                if (match.Success && int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    files.Add(file);
            }
        }
        return files;
    }

    internal static List<FileEntry> BuildFiles(IReadOnlyList<string> selectedFiles, Dictionary<string, int> nextIndexes,
        CancellationToken cancellationToken = default)
    {
        var result = new List<FileEntry>();
        foreach (var directory in selectedFiles.GroupBy(Path.GetDirectoryName, StringComparer.OrdinalIgnoreCase))
        {
            var matches = directory.Select(file => (File: file, Match: PatchNameRegex().Match(Path.GetFileName(file)))).ToList();
            foreach (var resource in matches.GroupBy(item => item.Match.Groups[1].Value.ToLowerInvariant()))
            {
                // 原编号只属于当前资源前缀，不能为其它 bundle 的编号创建空主文件。
                var indexes = resource.Select(item => int.Parse(item.Match.Groups[2].Value, CultureInfo.InvariantCulture))
                    .Distinct().Order();
                var next = nextIndexes.GetValueOrDefault(resource.Key);
                foreach (var originalIndex in indexes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var triplet = resource.Where(item => int.Parse(item.Match.Groups[2].Value, CultureInfo.InvariantCulture) == originalIndex)
                        .ToDictionary(item => item.Match.Groups[3].Value, item => item.File, StringComparer.OrdinalIgnoreCase);
                    if (!triplet.ContainsKey(""))
                        throw new InvalidDataException($"补丁缺少主文件：{Path.Combine(directory.Key!, $"{resource.Key}.patch_{originalIndex}")}");
                    foreach (var suffix in new[] { "", ".gpu_resources", ".stream" })
                        result.Add(new FileEntry(triplet.GetValueOrDefault(suffix), $"{resource.Key}.patch_{next}{suffix}"));
                    next++;
                }
                nextIndexes[resource.Key] = next;
            }
        }
        return result;
    }
}
