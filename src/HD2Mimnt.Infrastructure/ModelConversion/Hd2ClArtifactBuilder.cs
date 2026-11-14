using System.Text.RegularExpressions;

namespace HD2Mimnt.Infrastructure.ModelConversion;

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
    IReadOnlyList<string>? ComponentSelection = null);

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
        var packaged = IasModelPackager.Build(new IasPackageRequest(modelId,
            request.Name, request.Author, request.Description,
            iconPath, icon, iconMime, compiled, scan.Resources, game,
            scan.ArchiveIds.Concat(set.Routing.BodyOwners.Select(owner => owner.Archive))
                .Concat(set.Data.Metadata.Members.Select(member => member.Archive))
                .Select(archive => archive.ToLowerInvariant().StartsWith("0x", StringComparison.Ordinal)
                    ? archive[2..] : archive.ToLowerInvariant()).Distinct().ToList()));
        token.ThrowIfCancellationRequested();

        progress?.Report(new(85, "导出成品文件", packaged.Name));
        await IasModelPackager.WriteMembersAsync(request.OutputDirectory, packaged.Members, token);
        progress?.Report(new(100, "转换完成", packaged.Name));
        return new Hd2ClArtifactResult(packaged.Name, request.OutputDirectory);
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
                var members = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var (full, relative) in files.Where(file => !IsPatchMember(file.Relative)))
                    members[relative] = File.ReadAllBytes(full);
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
