using System.Text.Json;
using System.Text.RegularExpressions;

namespace HD2Mimnt.Infrastructure.ModelConversion;

/// <summary>合集模组中的一个可转换版本：显示名与参与扫描的组件目录（相对模组根）。</summary>
public sealed record Hd2ClVariantPlan(string Id, string Name, IReadOnlyList<string> Folders);

/// <summary>
/// 按组件文件名里的 16 位资源编号把合集模组拆成多个版本：模型组件按补丁编号分组，
/// 配套组件按编号归入对应版本，纯材质组件随每个版本携带。
/// </summary>
public static class Hd2ClVariantPlanner
{
    private const string MaterialArchive = "9ba626afa44a3aa3";
    private static readonly Regex Token = new("[0-9a-fA-F]{16}", RegexOptions.Compiled);

    public static IReadOnlyList<Hd2ClVariantPlan> Plan(string modPath)
    {
        var folders = Hd2ClArtifactBuilder.ComponentFolders(modPath)
            .Select(folder => Inspect(modPath, folder))
            .ToList();
        if (folders.Count <= 1) return [Single(folders)];

        var patchGroups = folders
            .Where(folder => folder.PatchTokens.Any(token => token != MaterialArchive))
            .ToList();
        if (patchGroups.Count == 0) return [Single(folders)];

        var groups = Group(patchGroups);
        if (groups.Count <= 1) return [Single(folders)];

        var shared = folders
            .Where(folder => folder is { PatchTokens.Count: > 0, CompanionCount: 0 } && folder.PatchTokens.All(token => token == MaterialArchive))
            .ToList();
        var companions = folders
            .Where(folder => !patchGroups.Contains(folder) && !shared.Contains(folder) && folder.CompanionCount > 0)
            .ToList();
        var assignments = companions.Select(folder =>
        {
            var best = groups.Max(group => Overlap(folder, group));
            var hits = best == 0
                ? new List<List<FolderInfo>>()
                : groups.Where(group => Overlap(folder, group) == best).ToList();
            return (Folder: folder, Groups: hits);
        }).ToList();
        // 同一份配套同时对应多个补丁组（头/躯干拆开导出）时，它们属于同一个模型，不能当成不同版本。
        if (assignments.Any(item => item.Groups.Count > 1))
            groups = MergeGroups(groups, assignments);
        var orphanCompanions = assignments.Where(item => item.Groups.Count == 0).Select(item => item.Folder).ToList();
        var extra = groups.ToDictionary(group => group, _ => new List<FolderInfo>());
        foreach (var folder in companions)
        {
            var best = groups.Max(group => Overlap(folder, group));
            if (best == 0)
            {
                if (!orphanCompanions.Contains(folder)) orphanCompanions.Add(folder);
                continue;
            }
            foreach (var group in groups.Where(group => Overlap(folder, group) == best))
                extra[group].Add(folder);
        }
        if (groups.Count <= 1) return [Single(folders)];

        return groups.Select(group =>
        {
            var anchor = group.OrderBy(folder => folder.Relative, StringComparer.OrdinalIgnoreCase).First();
            var members = extra[group].Concat(orphanCompanions).Concat(shared).Concat(group)
                .Select(folder => folder.Relative)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new Hd2ClVariantPlan(anchor.Relative, ReadName(anchor), members);
        }).ToList();
    }

    private static int Overlap(FolderInfo folder, List<FolderInfo> group)
        => folder.Tokens.Intersect(group.SelectMany(item => item.Tokens), StringComparer.OrdinalIgnoreCase).Count();

    private static List<List<FolderInfo>> MergeGroups(List<List<FolderInfo>> groups,
        List<(FolderInfo Folder, List<List<FolderInfo>> Groups)> assignments)
    {
        var parent = Enumerable.Range(0, groups.Count).ToArray();
        int Find(int value)
        {
            while (parent[value] != value) value = parent[value] = parent[parent[value]];
            return value;
        }
        void Union(int left, int right)
        {
            left = Find(left);
            right = Find(right);
            if (left != right) parent[right] = left;
        }
        foreach (var (_, hits) in assignments)
        {
            var positions = hits.Select(group => groups.IndexOf(group)).Where(position => position >= 0).ToList();
            for (var position = 1; position < positions.Count; position++) Union(positions[0], positions[position]);
        }
        return groups
            .Select((group, position) => (group, root: Find(position)))
            .GroupBy(pair => pair.root)
            .OrderBy(group => group.Min(pair => pair.group.Min(item => item.Relative)), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.SelectMany(pair => pair.group).ToList())
            .ToList();
    }

    private static List<List<FolderInfo>> Group(List<FolderInfo> patchGroups)
    {
        var parent = Enumerable.Range(0, patchGroups.Count).ToArray();
        int Find(int index)
        {
            while (parent[index] != index) index = parent[index] = parent[parent[index]];
            return index;
        }
        void Union(int left, int right)
        {
            left = Find(left);
            right = Find(right);
            if (left != right) parent[right] = left;
        }
        var byToken = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < patchGroups.Count; index++)
        {
            foreach (var token in patchGroups[index].PatchTokens)
            {
                if (byToken.TryGetValue(token, out var first)) Union(first, index);
                else byToken[token] = index;
            }
        }
        return patchGroups
            .Select((folder, index) => (folder, root: Find(index)))
            .GroupBy(pair => pair.root)
            .OrderBy(group => group.Min(pair => pair.folder.Relative), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Select(pair => pair.folder).ToList())
            .ToList();
    }

    private static Hd2ClVariantPlan Single(List<FolderInfo> folders)
    {
        var first = folders.FirstOrDefault();
        return new Hd2ClVariantPlan("all", first is null ? "模型" : ReadName(first),
            folders.Select(folder => folder.Relative).ToList());
    }

    private static FolderInfo Inspect(string root, string folder)
    {
        var relative = Path.GetRelativePath(root, folder).Replace('\\', '/');
        var patchTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var companions = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (Hd2ClArtifactBuilder.IsPatchFile(name))
            {
                var token = name[..16].ToLowerInvariant();
                patchTokens.Add(token);
                tokens.Add(token);
                continue;
            }
            if (Hd2ClArtifactBuilder.IsCompanionFile(name)) companions++;
            // 头/躯干拆开导出时，某个部件的编号可能只出现在 hd2author/hd2preview 里，
            // 因此非补丁文件都要参与编号提取，不能只认 rigbin/hd2phys。
            foreach (Match match in Token.Matches(name)) tokens.Add(match.Value.ToLowerInvariant());
        }
        return new FolderInfo(relative, folder, patchTokens, tokens, companions);
    }

    /// <summary>组件名优先取组件清单，缺失时退回目录名；合集模组的一级选项名由调用方覆盖。</summary>
    private static string ReadName(FolderInfo folder)
    {
        var manifest = Path.Combine(folder.Full, "manifest.json");
        if (File.Exists(manifest))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("Name", out var name)
                    && name.GetString() is { Length: > 0 } value)
                    return value;
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
            }
        }
        return Path.GetFileName(folder.Relative);
    }

    private sealed record FolderInfo(
        string Relative,
        string Full,
        HashSet<string> PatchTokens,
        HashSet<string> Tokens,
        int CompanionCount);
}
