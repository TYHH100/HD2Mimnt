using System.Text.RegularExpressions;
using HD2Mimnt.Core.IO;

namespace HD2Mimnt.Infrastructure.ModelConversion;

/// <summary>成品中的一个可选配色/部件分支；SourceDirectory 是源模组内的相对目录。</summary>
public sealed record Hd2ClOptionChoice(string Name, string? Image, string SourceDirectory);

/// <summary>成品的一个互斥选项组，对应源模组的一个 V1 选项；分支目录里只允许材质与贴图。</summary>
public sealed record Hd2ClOptionGroup(string Name, string? Description, string? Image, bool Enabled,
    int DefaultIndex, IReadOnlyList<Hd2ClOptionChoice> Choices);

/// <summary>一次 HD2CL 产物构建所需的全部输入；路径均由主程序解析后传入。</summary>
public sealed record Hd2ClArtifactRequest(
    string Seed,
    string Name,
    string Author,
    string Description,
    string ModPath,
    string GameDataRoot,
    string OutputDirectory,
    string? ThumbnailPath = null,
    string? ManifestIconPath = null,
    IReadOnlyList<string>? ComponentSelection = null,
    IReadOnlyList<Hd2ClOptionGroup>? OptionGroups = null);

/// <summary>产物身份：成品显示名与写出的成品目录（直接交给管理器导入）。</summary>
public sealed record Hd2ClArtifactResult(string Name, string OutputDirectory);

public readonly record struct Hd2ClBuildProgress(double Percent, string Stage, string? Message = null);

/// <summary>
/// HD2CL 转换的重活：扫描模组、编译私有模型、打包并压缩归档。
/// 只依赖文件与游戏数据，不读写管理器数据库，因此可在 Worker 独立进程执行。
/// </summary>
public static class Hd2ClArtifactBuilder
{
    private static readonly Regex PatchName = new("[0-9a-fA-F]{16}\\.patch_\\d+\\z", RegexOptions.Compiled);

    public static async Task<Hd2ClArtifactResult> BuildAsync(Hd2ClArtifactRequest request,
        IProgress<Hd2ClBuildProgress>? progress, CancellationToken token)
    {
        progress?.Report(new(5, "扫描模组组件", request.Name));
        var scan = Scan(request.ModPath, request.ComponentSelection, token);
        IasBinary.Require(scan.Companions.Count > 0,
            "该模组没有找到 IAS 配套（rigbin/hd2phys/hd2shared），无法生成角色库模型。");
        IasBinary.Require(scan.Resources.Keys.Any(key => key.Type == IasResourceTypes.Unit),
            "该模组没有找到模型 Unit，无法生成角色库模型。");
        token.ThrowIfCancellationRequested();

        progress?.Report(new(15, "合并配套数据", request.Name));
        var set = IasCompanionSets.Read(scan.Companions, request.Seed);
        var icon = FindIcon(request.ThumbnailPath, request.ManifestIconPath, out var iconPath, out var iconMime);
        token.ThrowIfCancellationRequested();

        using var game = new IasGameData(request.GameDataRoot);
        progress?.Report(new(25, "编译私有模型、目录与物理数据", request.Name));
        var modelId = "ias_" + set.Data.Metadata.Id;
        var compiled = IasModelCompiler.Compile(new IasCompileRequest(
            modelId, request.Name, scan.Resources, set.Members, set.Data, set.Routing, game));
        token.ThrowIfCancellationRequested();

        progress?.Report(new(70, "生成安装包", request.Name));
        // 选项预览图要先进成品，清单才能写出成品内相对路径；缺失图片保持 null，不阻塞转换。
        var optionGroups = request.OptionGroups is { Count: > 0 }
            ? CopyOptionImages(request.ModPath, request.OutputDirectory, request.OptionGroups, token)
            : null;
        var packaged = IasModelPackager.Build(new IasPackageRequest(modelId,
            request.Name, request.Author, request.Description,
            iconPath, icon, iconMime, compiled, scan.Resources, game,
            scan.ArchiveIds.Concat(set.Routing.BodyOwners.Select(owner => owner.Archive))
                .Concat(set.Data.Metadata.Members.Select(member => member.Archive))
                .Select(archive => archive.ToLowerInvariant().StartsWith("0x", StringComparison.Ordinal)
                    ? archive[2..] : archive.ToLowerInvariant()).Distinct().ToList(),
            optionGroups));
        token.ThrowIfCancellationRequested();

        progress?.Report(new(85, "导出成品文件", packaged.Name));
        await IasModelPackager.WriteMembersAsync(request.OutputDirectory, packaged.Members, token);
        if (request.OptionGroups is { Count: > 0 })
            CopyOptionPayloads(request.ModPath, request.OutputDirectory, request.OptionGroups, token);
        progress?.Report(new(100, "转换完成", packaged.Name));
        return new Hd2ClArtifactResult(packaged.Name, request.OutputDirectory);
    }

    /// <summary>
    /// 把选项预览图复制进成品（游戏内选项面板按成品内相对路径读取），并把清单里的图片路径改写成成品路径。
    /// 清单没声明图片时退回分支目录里的 icon.*；都没有则保持 null。
    /// </summary>
    internal static IReadOnlyList<Hd2ClOptionGroup> CopyOptionImages(string modPath, string outputDirectory,
        IReadOnlyList<Hd2ClOptionGroup> groups, CancellationToken token)
    {
        var result = new List<Hd2ClOptionGroup>(groups.Count);
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            token.ThrowIfCancellationRequested();
            var group = groups[groupIndex];
            var groupFolder = (groupIndex + 1).ToString();
            // 单分支组在清单里是普通开关，不存在“选中子选项”这一步，缩略图优先从唯一分支目录取。
            var groupFallback = group.Choices.Count == 1 ? group.Choices[0].SourceDirectory : null;
            var groupImage = CopyOptionImage(modPath, outputDirectory, group.Image, groupFallback,
                "Material/" + groupFolder + "/icon", token);
            var choices = new List<Hd2ClOptionChoice>(group.Choices.Count);
            for (var choiceIndex = 0; choiceIndex < group.Choices.Count; choiceIndex++)
            {
                var choice = group.Choices[choiceIndex];
                choices.Add(choice with
                {
                    Image = CopyOptionImage(modPath, outputDirectory, choice.Image, choice.SourceDirectory,
                        $"Material/{groupFolder}/{choiceIndex + 1}/icon", token)
                });
            }
            result.Add(group with { Image = groupImage, Choices = choices });
        }
        return result;
    }

    private static string? CopyOptionImage(string modPath, string outputDirectory, string? declared,
        string? fallbackDirectory, string targetWithoutExtension, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(declared))
            candidates.Add(Path.Combine(modPath, declared.Replace('/', Path.DirectorySeparatorChar)));
        if (!string.IsNullOrWhiteSpace(fallbackDirectory))
        {
            var branch = Path.Combine(modPath, fallbackDirectory.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(branch))
                candidates.AddRange(ImageFiles(branch).OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        }
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate) || !PathGuard.IsInsideRoot(modPath, candidate)) continue;
            var extension = Path.GetExtension(candidate).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp")) continue;
            var relative = targetWithoutExtension + extension;
            var target = Path.Combine(outputDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(candidate, target, overwrite: true);
            return relative;
        }
        return null;
    }

    private static IEnumerable<string> ImageFiles(string directory)
        => new[] { "icon.png", "icon.jpg", "icon.jpeg", "icon.webp" }
            .Select(name => Path.Combine(directory, name));

    /// <summary>把源模组的配色/部件分支原样复制进成品，按选项启用后在部署时只带选中的那份。</summary>
    private static void CopyOptionPayloads(string modPath, string outputDirectory,
        IReadOnlyList<Hd2ClOptionGroup> groups, CancellationToken token)
    {
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            for (var choiceIndex = 0; choiceIndex < group.Choices.Count; choiceIndex++)
            {
                token.ThrowIfCancellationRequested();
                var source = Path.Combine(modPath,
                    group.Choices[choiceIndex].SourceDirectory.Replace('/', Path.DirectorySeparatorChar));
                IasBinary.Require(Directory.Exists(source), "配色选项目录不存在：" + group.Choices[choiceIndex].SourceDirectory);
                var target = Path.Combine(outputDirectory, "Material", (groupIndex + 1).ToString(), (choiceIndex + 1).ToString());
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(file);
                    if (!IsPatchFile(name)
                        && !name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase)
                        && !name.EndsWith(".stream", StringComparison.OrdinalIgnoreCase)) continue;
                    var destination = Path.Combine(target, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination, overwrite: true);
                }
            }
        }
    }

    public static IEnumerable<string> ComponentFolders(string root)
    {
        var components = Path.Combine(root, "components");
        if (Directory.Exists(components))
        {
            return Directory.EnumerateDirectories(components, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }
        var folders = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
            .Where(folder => !Path.GetFileName(folder).Equals("components", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly).Any()) folders.Add(root);
        return folders;
    }

    public static bool IsCompanionFile(string name)
        => name.EndsWith(".rigbin", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".hd2phys", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".hd2shared", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".hd2pose", StringComparison.OrdinalIgnoreCase)
           || Path.GetFileName(name).Equals("mod_group.json", StringComparison.OrdinalIgnoreCase);

    public static bool IsPatchFile(string name) => PatchName.IsMatch(Path.GetFileName(name));

    /// <summary>补丁本体与其 GPU/Stream 边车另有资源解析路径，不进入配套成员表。</summary>
    private static bool IsPatchMember(string name)
        => name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".stream", StringComparison.OrdinalIgnoreCase)
           || IsPatchFile(name);

    /// <summary>合集模组只扫描用户选中的组件；相对路径来自版本规划，必须落在模组目录内。</summary>
    private static IReadOnlyList<string> ResolveFolders(string root, IReadOnlyList<string>? selection)
    {
        if (selection is not { Count: > 0 }) return ComponentFolders(root).ToList();
        var folders = selection
            .Select(relative => Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();
        foreach (var folder in folders)
            IasBinary.Require(Directory.Exists(folder), "选择的组件目录不存在：" + folder);
        return folders;
    }

    private sealed record ScanResult(
        Dictionary<(ulong Type, ulong Id), IasResource> Resources,
        List<IReadOnlyDictionary<string, byte[]>> Companions,
        HashSet<string> ArchiveIds);

    /// <summary>
    /// 只把配套文件保留在内存；补丁三元组逐个读取、解析后立即释放原始字节，
    /// 避免整份模组（含上百 MB 的 gpu_resources）在扫描期间出现两份副本。
    /// </summary>
    private static ScanResult Scan(string root, IReadOnlyList<string>? selection, CancellationToken token)
    {
        var resources = new Dictionary<(ulong Type, ulong Id), IasResource>();
        var companions = new List<IReadOnlyDictionary<string, byte[]>>();
        var archives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in ResolveFolders(root, selection))
        {
            token.ThrowIfCancellationRequested();
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(file => (Full: file, Relative: Path.GetRelativePath(folder, file).Replace('\\', '/')))
                .ToList();
            if (files.Count == 0) continue;

            if (files.Any(file => IsCompanionFile(file.Relative)))
            {
                // 配套校验会按 mod_group 清单核对任意成员（含图片/作者信息）的摘要，
                // 因此除补丁三元组外都保留；三元组另有资源解析路径，不能在这里重复持有。
                // 选项可以只选中 HD2IndependentRig 子目录；配套成员键必须保留该目录前缀，
                // 因为 .hd2shared 里的引用名就是相对 HD2IndependentRig 的。
                var memberRoot = Path.GetFileName(folder).Equals("HD2IndependentRig", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetDirectoryName(folder)!
                    : folder;
                var members = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var (full, relative) in files.Where(file => !IsPatchMember(file.Relative)))
                    members[memberRoot == folder ? relative : Path.GetRelativePath(memberRoot, full).Replace('\\', '/')]
                        = File.ReadAllBytes(full);
                companions.Add(members);
            }

            foreach (var (full, _) in files
                         .Where(file => IsPatchFile(file.Relative))
                         .OrderBy(file => file.Relative, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(full);
                var toc = File.ReadAllBytes(full);
                var gpuPath = full + ".gpu_resources";
                var streamPath = full + ".stream";
                var gpu = File.Exists(gpuPath) ? File.ReadAllBytes(gpuPath) : [];
                var stream = File.Exists(streamPath) ? File.ReadAllBytes(streamPath) : [];
                archives.Add(fileName[..16].ToLowerInvariant());
                foreach (var (key, value) in IasArchive.ReadEntries(toc, gpu, stream))
                    // 同一模组内的补丁按加载顺序覆盖同名资源，游戏侧同样是后者生效；
                    // 合集模组的材质差分包会以相同编号重复出现，不能按冲突拒绝。
                    resources[key] = value;
            }
        }
        return new ScanResult(resources, companions, archives);
    }

    private static byte[]? FindIcon(string? thumbnailPath, string? manifestIconPath,
        out string? iconPath, out string? mime)
    {
        iconPath = null;
        mime = null;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(thumbnailPath) && File.Exists(thumbnailPath)) candidates.Add(thumbnailPath);
        if (!string.IsNullOrWhiteSpace(manifestIconPath) && File.Exists(manifestIconPath)) candidates.Add(manifestIconPath);
        var file = candidates.FirstOrDefault(path => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp");
        if (file is null) return null;
        var extension = Path.GetExtension(file).ToLowerInvariant();
        iconPath = "icon" + extension;
        mime = extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "image/webp"
        };
        return File.ReadAllBytes(file);
    }
}
