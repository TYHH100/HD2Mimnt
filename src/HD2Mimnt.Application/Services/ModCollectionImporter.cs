using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Services;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 散装文件夹（或只套了一层目录的压缩包）合集识别：
/// 外层目录自己没有清单、但直接子目录各自是一个模组时，按一个模组导入，
/// 把子模组转换成“一级组件 + 二级选项”，复用 HD2IAS 的两级配置页。
/// 库内只增加一个条目，子目录的作者清单保持原字节，生成的选项树保存到管理器自有 JSON。
/// 由嵌套压缩包展开出的补丁目录没有清单，也可作为组件（只在导入方明确传入时生效）。
/// </summary>
internal static class ModCollectionImporter
{
    /// <summary>识别结果：合集实际根目录（可能是压缩包解出的外层目录）与生成的清单。</summary>
    internal sealed record Collection(string Root, ModManifest Manifest);

    private sealed record Child(string Root, string DirectoryName, ModManifest? Manifest);

    private static readonly ManifestParser Parser = new();
    private static readonly string[] PreviewNames = { "preview", "cover", "thumbnail", "icon" };

    /// <summary>
    /// 尝试把 <paramref name="root"/> 识别为模组合集。根目录或唯一包裹目录自身带清单时返回 null，
    /// 保持现有“单模组 / 嵌套一层清单”行为；至少两个带清单子目录才生成合集。
    /// <paramref name="nestedPackageRoots"/> 是导入时由嵌套压缩包展开出的目录：它们没有作者清单，
    /// 直属补丁文件即部署单位，因此允许作为无选项组件；普通散装目录不在此列，仍按清单识别。
    /// </summary>
    internal static async Task<Collection?> TryBuildAsync(string root, string outerName, CancellationToken token,
        IReadOnlySet<string>? nestedPackageRoots = null)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        if (File.Exists(Path.Combine(root, "manifest.json"))) return null;

        var collectionRoot = FindCollectionRoot(root, nestedPackageRoots);
        if (collectionRoot == null) return null;

        var children = new List<Child>();
        foreach (var directory in Directory.EnumerateDirectories(collectionRoot)
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (await InspectChildAsync(directory, nestedPackageRoots, token) is { } child) children.Add(child);
        }

        // 根目录自身带补丁时保持普通模组：合集只会展开选中的组件目录，根级补丁会丢失。
        if (children.Any(child => child.Manifest == null) && HasPatchFiles(collectionRoot)) return null;
        // 单个无清单补丁包只在展开嵌套压缩包时成立；单个带清单目录保持“向下找一层清单”。
        if (children.Count < 2 && !(children.Count == 1 && children[0].Manifest == null)) return null;

        var manifest = BuildManifest(collectionRoot, outerName, children);
        return new Collection(collectionRoot, manifest);
    }

    private static async Task<Child?> InspectChildAsync(string directory, IReadOnlySet<string>? nestedPackageRoots,
        CancellationToken token)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                var manifest = await Parser.ParseFromFileAsync(manifestPath, token);
                if (manifest != null) return new Child(directory, Path.GetFileName(directory), manifest);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // 坏掉的子清单不能拖垮整个合集；该子目录按“无清单补丁包”重新判断。
            }
        }

        if (nestedPackageRoots != null
            && nestedPackageRoots.Contains(Path.TrimEndingDirectorySeparator(directory))
            && HasPatchFiles(directory))
            return new Child(directory, Path.GetFileName(directory), null);
        return null;
    }

    /// <summary>补丁目录：直属补丁主文件，部署器只扫描选中组件目录的直属文件。</summary>
    private static bool HasPatchFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Any(file => PatchDeploymentPlanner.TryParseTarget(file, out _, out _, out var suffix)
                    && suffix.Length == 0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 合集根目录：根目录直接子目录中至少两个是组件时用根目录；
    /// 若根目录只有一个目录（压缩包常见的同名包裹层）且它不是模组，则再看它的子目录。
    /// </summary>
    private static string? FindCollectionRoot(string root, IReadOnlySet<string>? nestedPackageRoots)
    {
        if (CountComponents(root, nestedPackageRoots) >= 2) return root;
        var directories = Directory.EnumerateDirectories(root).ToArray();
        if (directories.Length != 1) return null;
        var wrapper = directories[0];
        if (File.Exists(Path.Combine(wrapper, "manifest.json"))) return null;
        return CountComponents(wrapper, nestedPackageRoots) >= 2 ? wrapper : null;
    }

    private static int CountComponents(string directory, IReadOnlySet<string>? nestedPackageRoots)
        => Directory.EnumerateDirectories(directory).Count(child =>
            File.Exists(Path.Combine(child, "manifest.json"))
            || (nestedPackageRoots != null
                && nestedPackageRoots.Contains(Path.TrimEndingDirectorySeparator(child))
                && HasPatchFiles(child)));

    private static ModManifest BuildManifest(string collectionRoot, string outerName, List<Child> children)
    {
        var manifest = new ModManifest
        {
            Guid = Guid.NewGuid(),
            Name = outerName,
            ManifestVersion = ManifestVersion.V1,
            Version = children.Select(child => child.Manifest?.Version)
                .FirstOrDefault(version => !string.IsNullOrWhiteSpace(version)) ?? "1.0.0",
            Author = children.Select(child => child.Manifest?.Author)
                .FirstOrDefault(author => !string.IsNullOrWhiteSpace(author)) ?? "Unknown",
            Description = children.Select(child => child.Manifest?.Description)
                .FirstOrDefault(description => !string.IsNullOrWhiteSpace(description)) ?? string.Empty,
            Tags = children.SelectMany(child => child.Manifest?.Tags ?? new List<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Options = new List<ModOption>(),
            OptionGroups = new List<ModOptionGroup>()
        };

        // 子清单名称可能被作者复制成同一个（实测某系列三份清单名称相同），此时用目录名区分组件。
        var declaredNames = children.Select(child => child.Manifest?.Name?.Trim()).ToList();
        var duplicatedName = declaredNames.Where(name => !string.IsNullOrEmpty(name))
            .GroupBy(name => name!, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1);

        for (var index = 0; index < children.Count; index++)
        {
            var child = children[index];
            var componentName = duplicatedName || string.IsNullOrWhiteSpace(child.Manifest?.Name)
                ? child.DirectoryName : child.Manifest!.Name.Trim();
            var groupId = $"component-{index}";
            var image = ResolveChildImage(child);
            var group = new ModOptionGroup
            {
                Id = groupId,
                Name = componentName,
                Description = child.Manifest?.Description ?? string.Empty,
                Image = image
            };
            manifest.OptionGroups!.Add(group);
            AddChildOptions(manifest.Options!, child, groupId, componentName, image);
        }

        manifest.IconPath = ResolveOuterImage(collectionRoot);
        return manifest;
    }

    /// <summary>
    /// 子模组选项转换：V1 选项保留标题、描述、默认启用和子选项，只把 Include 与图片路径映射到合集目录下；
    /// Legacy 选项转换为一个带互斥子选项的组件；没有清单或没有选项声明的组件按整个子目录部署。
    /// </summary>
    private static void AddChildOptions(List<ModOption> options, Child child, string groupId, string componentName,
        string? image)
    {
        var manifest = child.Manifest;
        if (manifest?.ManifestVersion == ManifestVersion.V1 && manifest.Options is { Count: > 0 })
        {
            foreach (var source in manifest.Options)
            {
                options.Add(new ModOption
                {
                    Name = source.Name,
                    Description = source.Description,
                    DefaultEnabled = source.DefaultEnabled,
                    Image = ResolveChildImagePath(child, source.Image),
                    Include = MapInclude(child, source.Include, hasSubOptions: source.SubOptions is { Count: > 0 }),
                    GroupId = groupId,
                    SubOptions = source.SubOptions?.Select(sub => new ModSubOption
                    {
                        Name = sub.Name,
                        Description = sub.Description,
                        Include = MapInclude(child, sub.Include, hasSubOptions: false) ?? new List<string> { child.DirectoryName },
                        Image = ResolveChildImagePath(child, sub.Image)
                    }).ToList()
                });
            }

            return;
        }

        if (manifest?.LegacyOptions is { Count: > 0 })
        {
            // Legacy 互斥下拉：一个组件卡片 + 每个子目录一个互斥子选项。
            options.Add(new ModOption
            {
                Name = componentName,
                Description = manifest.Description,
                Include = new List<string>(),
                Image = image,
                GroupId = groupId,
                SubOptions = manifest.LegacyOptions.Select(name => new ModSubOption
                {
                    Name = name,
                    Include = new List<string> { MapPath(child.DirectoryName, name) }
                }).ToList()
            });
            return;
        }

        // 没有清单或没有选项声明：整个子目录作为一个可整体启停的组件。
        options.Add(new ModOption
        {
            Name = componentName,
            Description = manifest?.Description ?? string.Empty,
            Include = new List<string> { child.DirectoryName },
            DefaultEnabled = true,
            Image = image,
            GroupId = groupId
        });
    }

    /// <summary>
    /// 选项 Include 映射：null 表示作者未声明映射，父选项有子选项时保持 null（由子选项决定目录），
    /// 否则按整个子目录部署，避免两级页里出现“回退全量部署”而把未选组件一起部署。
    /// </summary>
    private static List<string>? MapInclude(Child child, List<string>? include, bool hasSubOptions)
    {
        if (include == null) return hasSubOptions ? null : new List<string> { child.DirectoryName };
        return include.Select(path => MapPath(child.DirectoryName, path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? ResolveChildImagePath(Child child, string? relative)
    {
        var sanitized = SanitizeRelative(relative);
        if (sanitized == null) return null;
        var mapped = MapPath(child.DirectoryName, sanitized);
        return File.Exists(Path.Combine(child.Root, sanitized.Replace('/', Path.DirectorySeparatorChar)))
            ? mapped : null;
    }

    private static string? ResolveChildImage(Child child)
    {
        if (ResolveChildImagePath(child, child.Manifest?.IconPath) is { } declared) return declared;
        if (FindPreviewImage(child.Root) is not { } preview) return null;
        return MapPath(child.DirectoryName, Path.GetRelativePath(child.Root, preview).Replace('\\', '/'));
    }

    /// <summary>合集卡片只使用根目录图片，组件和选项图片保留在各自层级。</summary>
    private static string? ResolveOuterImage(string collectionRoot)
    {
        if (FindPreviewImage(collectionRoot) is { } outer)
            return Path.GetRelativePath(collectionRoot, outer).Replace('\\', '/');
        return null;
    }

    private static string? FindPreviewImage(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(file => new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" }
                    .Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                .OrderBy(file =>
                {
                    var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    var preferred = Array.IndexOf(PreviewNames, name);
                    return preferred >= 0 ? preferred : PreviewNames.Length;
                })
                .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string MapPath(string childDirectory, string? relative)
    {
        var sanitized = SanitizeRelative(relative);
        return string.IsNullOrEmpty(sanitized) ? childDirectory : $"{childDirectory}/{sanitized}";
    }

    /// <summary>规范化作者清单里的相对路径；越界或绝对路径直接丢弃，不能映射到合集目录之外。</summary>
    private static string? SanitizeRelative(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var normalized = relative.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        normalized = normalized.Trim('/');
        if (normalized.Length == 0) return null;
        if (Path.IsPathRooted(relative) || normalized == ".."
            || normalized.Contains("../", StringComparison.Ordinal)
            || normalized.Contains("/..", StringComparison.Ordinal)) return null;
        return normalized;
    }
}
