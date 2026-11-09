using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Archive;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 将后端组件模型接到现有 Mod/Profile 流程，映射只写入库内独立元数据。
/// 最外层名称固定为导入的压缩包/文件夹名；组件分组与生成选项只保存到管理器自有 JSON，
/// 作者 manifest.json 保持原字节，不被改写。
/// </summary>
internal sealed class Hd2IrIntegrationService(IArchiveExtractor extractor)
{
    internal const string Workflow = "HD2IAS";
    internal const string MetadataFile = ".hd2mimnt-hd2ir.json";

    /// <summary>管理器生成的组件分组与选项树；标准 manifest.json 不写入该文件内容。</summary>
    internal const string OptionsFile = ".hd2mimnt-options.json";

    private static readonly JsonSerializerOptions OptionsJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Hd2IrDetector _detector = new();

    internal sealed record Prepared(LogicalMod LogicalMod, ModManifest Manifest, string Root);

    /// <summary>自有选项元数据：一级组件分组 + 扁平选项（含 GroupId 与图片路径）。</summary>
    internal sealed class OptionsDocument
    {
        public int Schema { get; set; } = 1;
        public string? IconPath { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Author { get; set; }
        public string? Version { get; set; }
        public List<ModOptionGroup>? OptionGroups { get; set; }
        public List<ModOption>? Options { get; set; }
    }

    internal async Task<Prepared?> PrepareAsync(string root, string sourceName, string workspace,
        Func<Hd2IrImportPreview, CancellationToken, Task>? confirm, CancellationToken token)
    {
        var packages = new List<(string Root, string Name, ArchiveNode Node, List<string> Files)>();
        var container = new ArchiveNode { Name = sourceName };
        await InspectAsync(root, sourceName, container, 0);
        var hd2ir = false;
        foreach (var package in packages)
        {
            token.ThrowIfCancellationRequested();
            var paths = package.Files.Select(file => Path.GetRelativePath(package.Root, file)).Append(package.Name);
            if ((await _detector.DetectFromPathsAsync(paths, token)).IsHd2Ir) hd2ir = true;
        }
        if (!hd2ir) return null;

        // 更新器的 RuntimePackage/app 不是角色包，不能把运行时升级当作普通模组部署。
        if (packages.Any(package => package.Files.Any(file => Path.GetFileName(file).Equals("update_notes.json", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("检测到 HD2IR/HD2IAS 更新包，请使用其更新流程；此处只导入角色组件和配套模组。");

        var candidates = new List<ComponentCandidate>();
        var mappings = new Dictionary<Guid, List<(string File, string Target)>>();
        foreach (var package in packages)
        {
            var native = new List<(string, string)>();
            var patches = new List<(string, string)>();
            foreach (var file in package.Files)
            {
                token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(package.Root, file).Replace('\\', '/');
                if (PatchDeploymentPlanner.TryParseTarget(file, out _, out _, out _))
                    patches.Add((file, "data/" + Path.GetFileName(file)));
                else if (GetNativeTarget(relative) is { } target)
                    native.Add((file, target));
            }
            AddCandidate(native, ComponentType.HD2IAS, package.Name, package.Node);
            AddCandidate(patches, ComponentType.Model, package.Name, package.Node);
        }
        if (candidates.Count == 0)
            throw new InvalidDataException("已识别 HD2IAS，但没有可映射的组件文件。请检查 bin/HD2IndependentRig 目录或配套补丁。");

        var merge = await new ModMerger().MergeAsync(candidates, token);
        if (merge.Decision == MergeDecision.ManualConfirmation)
        {
            if (confirm == null) throw new InvalidOperationException("此 HD2IAS 组件组合需要在导入浮窗中确认后添加。");
            await confirm(new(sourceName, candidates.Select(c => $"{c.ComponentType} · {c.Name}").ToArray(), merge.Reason), token);
            token.ThrowIfCancellationRequested();
        }

        // 最外层名称就是导入的压缩包名或文件夹名；内层清单名称只用于组件标题。
        var outerName = StripArchiveSuffix(sourceName);
        var logical = new LogicalMod { Name = outerName };
        var manifest = new ModManifest
        {
            Name = outerName, Version = "1.0.0", Author = "Unknown",
            ManifestVersion = ManifestVersion.V1, Options = [], OptionGroups = []
        };
        var preparedRoot = Path.Combine(workspace, "prepared");
        Directory.CreateDirectory(preparedRoot);

        // 外层包（最浅清单或根级预览图）决定作者信息与外层封面，内层组件不会顶替。
        var primary = packages
            .Select(package => (Package: package, ManifestPath: FindManifestPath(package.Root, 3)))
            .FirstOrDefault(item => item.ManifestPath != null);
        if (primary.ManifestPath != null)
        {
            var primaryManifest = await new HD2Mimnt.Core.Services.ManifestParser().ParseFromFileAsync(primary.ManifestPath, token);
            if (primaryManifest != null)
            {
                if (!string.IsNullOrWhiteSpace(primaryManifest.Author)) manifest.Author = primaryManifest.Author;
                if (!string.IsNullOrWhiteSpace(primaryManifest.Version)) manifest.Version = primaryManifest.Version;
                if (!string.IsNullOrWhiteSpace(primaryManifest.Description)) manifest.Description = primaryManifest.Description;
                if (!string.IsNullOrWhiteSpace(primaryManifest.AuthorUrl)) manifest.AuthorUrl = primaryManifest.AuthorUrl;
                if (primaryManifest.Tags is { Count: > 0 }) manifest.Tags = primaryManifest.Tags;
            }
        }
        manifest.IconPath = await CopyOuterPreviewAsync();
        logical.Author = manifest.Author;
        logical.Version = manifest.Version;
        logical.Description = manifest.Description;
        logical.Tags = manifest.Tags;

        for (var index = 0; index < candidates.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var candidate = candidates[index];
            var packageRoot = packages.Single(p => p.Node == candidate.SourceNode).Root;
            var componentDirectory = $"components/{index}";
            var originalPath = FindManifestPath(packageRoot, 3);
            ModManifest? original = null;
            if (originalPath != null)
                original = await new HD2Mimnt.Core.Services.ManifestParser().ParseFromFileAsync(originalPath, token);
            var innerName = !string.IsNullOrWhiteSpace(original?.Name) ? original!.Name : StripArchiveSuffix(candidate.Name);
            var componentName = $"{candidate.ComponentType} · {StripArchiveSuffix(innerName)}";
            var component = new LogicalModComponent
            {
                ComponentId = $"{candidate.ComponentType.ToString().ToLowerInvariant()}-{index}",
                Name = componentName, Type = candidate.ComponentType
            };
            var package = new PackageSource { Name = candidate.Name, PhysicalPath = componentDirectory };
            component.SourcePackageId = package.Id;
            logical.Packages.Add(package);
            var include = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (file, target) in mappings[candidate.Id])
            {
                token.ThrowIfCancellationRequested();
                // 保留原目录，避免不同变体/同名文件在复制时相互覆盖。
                var relative = componentDirectory + "/" + Path.GetRelativePath(packageRoot, file).Replace('\\', '/');
                var destination = Path.Combine(preparedRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using (var input = File.OpenRead(file))
                await using (var output = File.Create(destination))
                    await input.CopyToAsync(output, token);
                var size = new FileInfo(destination).Length;
                component.Size += size;
                component.DeployableFiles.Add(new DeployableFile { SourcePath = relative, TargetPath = target, Size = size, PackageId = package.Id });
                include.Add(Path.GetDirectoryName(relative)!.Replace('\\', '/'));
            }
            logical.Components.Add(component);
            logical.TotalSize += component.Size;

            var group = new ModOptionGroup
            {
                Id = $"component-{index}",
                Name = componentName,
                Description = original?.Description ?? string.Empty
            };
            if (original != null && originalPath != null)
            {
                var manifestDirectory = Path.GetDirectoryName(originalPath)!;
                if (!string.IsNullOrWhiteSpace(original.IconPath))
                    group.Image = await CopyAssetAsync(componentDirectory, packageRoot, manifestDirectory, original.IconPath, token);
                // 保存原始双文件，生成的组件选项只进自有元数据，不改作者清单。
                await CopyAssetAsync(componentDirectory, packageRoot, manifestDirectory, Path.GetFileName(originalPath), token);
                if (File.Exists(Path.Combine(manifestDirectory, "hd2mimnt.json")))
                    await CopyAssetAsync(componentDirectory, packageRoot, manifestDirectory, "hd2mimnt.json", token);
            }
            if (candidate.ComponentType == ComponentType.Model && original != null && originalPath != null
                && ProfileModOptionResolver.HasOptions(original))
            {
                var manifestDirectory = Path.GetDirectoryName(originalPath)!;
                string MapInclude(string path) => componentDirectory + "/" + Path.GetRelativePath(packageRoot,
                    Path.GetFullPath(path, manifestDirectory)).Replace('\\', '/');
                if (original.ManifestVersion == ManifestVersion.V1)
                {
                    foreach (var option in original.Options!)
                    {
                        option.Include = option.Include?.Select(MapInclude).ToList()
                            ?? (option.SubOptions is { Count: > 0 } ? null : include.ToList());
                        if (!string.IsNullOrWhiteSpace(option.Image))
                            option.Image = await CopyAssetAsync(componentDirectory, packageRoot, manifestDirectory, option.Image, token);
                        if (option.SubOptions != null)
                            foreach (var sub in option.SubOptions)
                            {
                                sub.Include = sub.Include?.Select(MapInclude).ToList() ?? include.ToList();
                                if (!string.IsNullOrWhiteSpace(sub.Image))
                                    sub.Image = await CopyAssetAsync(componentDirectory, packageRoot, manifestDirectory, sub.Image, token);
                            }
                        option.GroupId = group.Id;
                        manifest.Options!.Add(option);
                    }
                }
                else
                    manifest.Options!.Add(new ModOption
                    {
                        Name = component.Name, Include = [], GroupId = group.Id,
                        SubOptions = original.LegacyOptions!.Select(name =>
                            new ModSubOption { Name = name, Include = [MapInclude(name)] }).ToList()
                    });
            }
            else manifest.Options!.Add(new ModOption
            {
                Name = component.Name, Include = include.Order(StringComparer.Ordinal).ToList(),
                DefaultEnabled = true, GroupId = group.Id, Image = group.Image
            });
            manifest.OptionGroups!.Add(group);
        }
        await File.WriteAllTextAsync(Path.Combine(preparedRoot, MetadataFile), JsonSerializer.Serialize(logical), token);
        await SaveOptionsAsync(preparedRoot, manifest, token);
        return new(logical, manifest, preparedRoot);

        /// <summary>外层封面：按包层级从外到内，先取该包清单图片，再取该包根级预览图；内层组件不得顶替。</summary>
        async Task<string?> CopyOuterPreviewAsync()
        {
            foreach (var package in packages)
            {
                token.ThrowIfCancellationRequested();
                var manifestPath = FindManifestPath(package.Root, 2);
                if (manifestPath != null)
                {
                    var preview = await new HD2Mimnt.Core.Services.ManifestParser().ParseFromFileAsync(manifestPath, token);
                    if (preview != null && !string.IsNullOrWhiteSpace(preview.IconPath))
                    {
                        var relative = await CopyPreviewAssetAsync(package.Root,
                            Path.GetDirectoryName(manifestPath)!, preview.IconPath, token);
                        if (relative != null) return relative;
                    }
                }
                var image = Directory.EnumerateFiles(package.Root, "*", SearchOption.TopDirectoryOnly)
                    .Where(file => new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" }
                        .Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    .OrderBy(file =>
                    {
                        var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                        return name is "preview" or "cover" or "thumbnail" or "icon" ? 0 : 1;
                    })
                    .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (image != null)
                    return await CopyPreviewAssetAsync(package.Root, package.Root,
                        Path.GetRelativePath(package.Root, image), token);
            }
            return null;
        }

        async Task<string?> CopyPreviewAssetAsync(string packageRoot, string assetRoot, string asset, CancellationToken ct)
        {
            var source = ResolveWithin(packageRoot, Path.GetRelativePath(packageRoot, Path.GetFullPath(asset, assetRoot)));
            if (!File.Exists(source)) return null;
            var relative = "preview/" + Path.GetFileName(source);
            var destination = ResolveWithin(preparedRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = File.OpenRead(source);
            await using var output = File.Create(destination);
            await input.CopyToAsync(output, ct);
            return relative;
        }

        async Task<string?> CopyAssetAsync(string componentDirectory, string packageRoot, string assetRoot, string asset, CancellationToken ct)
        {
            var source = ResolveWithin(packageRoot, Path.GetRelativePath(packageRoot, Path.GetFullPath(asset, assetRoot)));
            if (!File.Exists(source)) return null;
            var relative = componentDirectory + "/" + Path.GetRelativePath(packageRoot, source).Replace('\\', '/');
            var destination = ResolveWithin(preparedRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = File.OpenRead(source);
            await using var output = File.Create(destination);
            await input.CopyToAsync(output, ct);
            return relative;
        }

        void AddCandidate(List<(string File, string Target)> files, ComponentType type, string name, ArchiveNode node)
        {
            if (files.Count == 0) return;
            var candidate = new ComponentCandidate
            {
                Name = name, ComponentType = type, SourceNode = node,
                DetectionResult = new() { ComponentType = type, Confidence = DetectionConfidence.High },
                RelatedFiles = files.Select(item => item.File).ToList(), Size = files.Sum(item => new FileInfo(item.File).Length)
            };
            candidates.Add(candidate);
            mappings.Add(candidate.Id, files);
        }

        async Task InspectAsync(string directory, string name, ArchiveNode parent, int depth)
        {
            token.ThrowIfCancellationRequested();
            var node = new ArchiveNode { Name = name, Parent = parent };
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
            packages.Add((directory, name, node, files.Where(file => !extractor.IsArchiveFile(file)).ToList()));
            foreach (var archive in files.Where(extractor.IsArchiveFile))
            {
                token.ThrowIfCancellationRequested();
                if (depth >= 5) throw new InvalidDataException("嵌套压缩包超过 5 层，请解包后重新添加。");
                var nestedRoot = Path.Combine(workspace, "archives", Guid.NewGuid().ToString("N"));
                await extractor.ExtractToDirectoryAsync(archive, nestedRoot, token);
                await InspectAsync(nestedRoot, Path.GetFileName(archive), node, depth + 1);
            }
        }
    }

    /// <summary>读取管理器生成的组件分组/选项树（自有 JSON，非作者清单）。</summary>
    internal static async Task<OptionsDocument?> ReadOptionsAsync(Mod mod, CancellationToken token)
    {
        var path = Path.Combine(mod.Path, OptionsFile);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<OptionsDocument>(await File.ReadAllTextAsync(path, token), OptionsJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 把生成的组件分组/选项树写入管理器自有 JSON（HD2IAS 组件包与散装文件夹合集共用）。
    /// 作者 manifest.json 保持原字节，不写入该结构。
    /// </summary>
    internal static Task SaveOptionsAsync(string root, ModManifest manifest, CancellationToken token)
        => File.WriteAllTextAsync(Path.Combine(root, OptionsFile),
            JsonSerializer.Serialize(new OptionsDocument
            {
                IconPath = manifest.IconPath, Name = manifest.Name, Description = manifest.Description,
                Author = manifest.Author, Version = manifest.Version,
                OptionGroups = manifest.OptionGroups, Options = manifest.Options
            }, OptionsJson), token);

    private static string StripArchiveSuffix(string name)
    {
        foreach (var suffix in new[] { ".hd2irpack.zip", ".tar.gz", ".zip", ".7z", ".rar", ".tar", ".gz" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return name[..^suffix.Length];
        return name;
    }

    /// <summary>在限定深度内查找清单：深层的内层组件清单不能冒充外层清单。</summary>
    private static string? FindManifestPath(string root, int maxDepth)
    {
        if (!Directory.Exists(root)) return null;
        try
        {
            return Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories)
                .Select(path => (Path: path, Depth: Path.GetRelativePath(root, path)
                    .Count(c => c is '\\' or '/')))
                .Where(item => item.Depth <= maxDepth)
                .OrderBy(item => item.Depth)
                .ThenBy(item => item.Path.Length)
                .Select(item => item.Path)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? GetNativeTarget(string relative)
    {
        var parts = relative.Split('/');
        var rig = Array.FindIndex(parts, part => part.Equals("HD2IndependentRig", StringComparison.OrdinalIgnoreCase));
        if (rig >= 0 && rig < parts.Length - 1) return "bin/" + string.Join('/', parts.Skip(rig));
        if (parts.Length == 1 && (new[] { ".hd2preview", ".hd2phys", ".hd2pose", ".rigbin", ".hd2shared" }
                .Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase)
            || relative.Equals("difference.ini", StringComparison.OrdinalIgnoreCase) || relative.Equals("isolation.ini", StringComparison.OrdinalIgnoreCase)))
            return "bin/HD2IndependentRig/" + relative;
        return null;
    }

    internal static async Task<LogicalMod> ReadAsync(Mod mod, CancellationToken token)
        => JsonSerializer.Deserialize<LogicalMod>(await File.ReadAllTextAsync(Path.Combine(mod.Path, MetadataFile), token))
            ?? throw new InvalidDataException("HD2IAS 组件元数据无效，请重新导入模组。");

    internal static async Task<List<PatchDeploymentPlanner.FileEntry>> GetNativeFilesAsync(Mod mod, string? optionsJson, CancellationToken token)
    {
        var logical = await ReadAsync(mod, token);
        var plan = ProfileModOptionResolver.ResolveDeployPlan(mod.Manifest, ProfileModOptionResolver.ResolveEffective(mod.Manifest, optionsJson));
        var selected = plan.Directories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new List<PatchDeploymentPlanner.FileEntry>();
        foreach (var file in logical.Components.SelectMany(c => c.DeployableFiles))
        {
            token.ThrowIfCancellationRequested();
            // 作者元数据只保留在模组库，不作为运行时文件部署到游戏。
            if (IsAuthorMetadata(file.SourcePath) || IsAuthorMetadata(file.TargetPath)) continue;
            if (!file.TargetPath.StartsWith("bin/HD2IndependentRig/", StringComparison.OrdinalIgnoreCase)
                || !selected.Contains(Path.GetDirectoryName(file.SourcePath)!.Replace('\\', '/'))) continue;
            var source = ResolveWithin(mod.Path, file.SourcePath);
            if (!File.Exists(source)) throw new FileNotFoundException("HD2IAS 组件文件缺失，请重新导入。", source);
            // 目标必须仍符合导入时的显式映射。
            var target = file.TargetPath.Replace('\\', '/');
            ResolveWithin(Path.Combine(mod.Path, "bin", "HD2IndependentRig"), target["bin/HD2IndependentRig/".Length..]);
            files.Add(new(source, target, true, SourceRelativePath: target,
                SourceKey: PatchDeploymentPlanner.MakeNativeSourceKey(target)));
        }
        return files;
    }

    /// <summary>`.hd2author` 是作者元数据，保留在模组库，但不需要写入游戏目录。</summary>
    private static bool IsAuthorMetadata(string path) =>
        Path.GetExtension(path).Equals(".hd2author", StringComparison.OrdinalIgnoreCase);

    private static string ResolveWithin(string root, string relative)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("HD2IAS 文件映射超出目标目录。");
        return fullPath;
    }
}
