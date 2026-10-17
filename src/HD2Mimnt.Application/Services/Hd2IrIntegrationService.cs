using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Archive;

namespace HD2Mimnt.Application.Services;

/// <summary>将后端组件模型接到现有 Mod/Profile 流程，映射只写入库内独立元数据。</summary>
internal sealed class Hd2IrIntegrationService(IArchiveExtractor extractor)
{
    internal const string Workflow = "HD2IAS";
    internal const string MetadataFile = ".hd2mimnt-hd2ir.json";
    private readonly Hd2IrDetector _detector = new();

    internal sealed record Prepared(LogicalMod LogicalMod, ModManifest Manifest, string Root);

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

        var logical = new LogicalMod { Name = StripArchiveSuffix(sourceName) };
        var manifest = new ModManifest { Name = logical.Name, Version = "1.0.0", Author = "Unknown", ManifestVersion = ManifestVersion.V1, Options = [] };
        var preparedRoot = Path.Combine(workspace, "prepared");
        Directory.CreateDirectory(preparedRoot);
        for (var index = 0; index < candidates.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var candidate = candidates[index];
            var componentDirectory = $"components/{index}";
            var component = new LogicalModComponent
            {
                ComponentId = $"{candidate.ComponentType.ToString().ToLowerInvariant()}-{index}",
                Name = $"{candidate.ComponentType} · {StripArchiveSuffix(candidate.Name)}", Type = candidate.ComponentType
            };
            var package = new PackageSource { Name = candidate.Name, PhysicalPath = componentDirectory };
            component.SourcePackageId = package.Id;
            logical.Packages.Add(package);
            var include = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (file, target) in mappings[candidate.Id])
            {
                token.ThrowIfCancellationRequested();
                // 保留原目录，避免不同变体/同名文件在复制时相互覆盖。
                var packageRoot = packages.Single(p => p.Node == candidate.SourceNode).Root;
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
            var originalRoot = packages.Single(p => p.Node == candidate.SourceNode).Root;
            var manifestPath = Directory.EnumerateFiles(originalRoot, "manifest.json", SearchOption.AllDirectories)
                .OrderBy(path => path.Length).FirstOrDefault();
            ModManifest? original = null;
            if (manifestPath != null)
                original = await new HD2Mimnt.Core.Services.ManifestParser().ParseFromFileAsync(manifestPath, token);
            if (original != null)
            {
                if (!string.IsNullOrWhiteSpace(original.Name)) logical.Name = manifest.Name = original.Name;
                if (!string.IsNullOrWhiteSpace(original.Author)) logical.Author = manifest.Author = original.Author;
                if (!string.IsNullOrWhiteSpace(original.Version)) logical.Version = manifest.Version = original.Version;
                if (!string.IsNullOrWhiteSpace(original.Description)) logical.Description = manifest.Description = original.Description;
                if (!string.IsNullOrWhiteSpace(original.AuthorUrl)) manifest.AuthorUrl = original.AuthorUrl;
                if (!string.IsNullOrWhiteSpace(original.IconPath))
                    manifest.IconPath = await CopyAssetAsync(original.IconPath);
                logical.Tags = manifest.Tags = original.Tags;
                // 保存原始双文件，生成的组件选项仅存数据库，不改作者清单。
                await CopyAssetAsync(Path.GetFileName(manifestPath));
                if (File.Exists(Path.Combine(Path.GetDirectoryName(manifestPath)!, "hd2mimnt.json")))
                    await CopyAssetAsync("hd2mimnt.json");
            }
            if (candidate.ComponentType == ComponentType.Model && original != null && ProfileModOptionResolver.HasOptions(original))
            {
                var manifestDirectory = Path.GetDirectoryName(manifestPath)!;
                string MapInclude(string path) => componentDirectory + "/" + Path.GetRelativePath(originalRoot,
                    Path.GetFullPath(path, manifestDirectory)).Replace('\\', '/');
                if (original.ManifestVersion == ManifestVersion.V1)
                {
                    foreach (var option in original.Options!)
                    {
                        option.Include = option.Include?.Select(MapInclude).ToList()
                            ?? (option.SubOptions is { Count: > 0 } ? null : include.ToList());
                        if (!string.IsNullOrWhiteSpace(option.Image)) option.Image = await CopyAssetAsync(option.Image);
                        if (option.SubOptions != null)
                            foreach (var sub in option.SubOptions)
                            {
                                sub.Include = sub.Include?.Select(MapInclude).ToList() ?? include.ToList();
                                if (!string.IsNullOrWhiteSpace(sub.Image)) sub.Image = await CopyAssetAsync(sub.Image);
                            }
                        manifest.Options.Add(option);
                    }
                }
                else
                    manifest.Options.Add(new ModOption
                    {
                        Name = component.Name, Include = [], SubOptions = original.LegacyOptions!.Select(name =>
                            new ModSubOption { Name = name, Include = [MapInclude(name)] }).ToList()
                    });
            }
            else manifest.Options.Add(new ModOption { Name = component.Name, Include = include.Order(StringComparer.Ordinal).ToList(), DefaultEnabled = true });

            async Task<string?> CopyAssetAsync(string asset)
            {
                var manifestRoot = Path.GetDirectoryName(manifestPath)!;
                var source = ResolveWithin(originalRoot, Path.GetRelativePath(originalRoot, Path.GetFullPath(asset, manifestRoot)));
                if (!File.Exists(source)) return null;
                var relative = componentDirectory + "/" + Path.GetRelativePath(originalRoot, source).Replace('\\', '/');
                var destination = ResolveWithin(preparedRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = File.OpenRead(source);
                await using var output = File.Create(destination);
                await input.CopyToAsync(output, token);
                return relative;
            }
        }
        await File.WriteAllTextAsync(Path.Combine(preparedRoot, MetadataFile), JsonSerializer.Serialize(logical), token);
        return new(logical, manifest, preparedRoot);

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

    private static string StripArchiveSuffix(string name)
    {
        foreach (var suffix in new[] { ".hd2irpack.zip", ".tar.gz", ".zip", ".7z", ".rar", ".tar", ".gz" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return name[..^suffix.Length];
        return name;
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
            if (!file.TargetPath.StartsWith("bin/HD2IndependentRig/", StringComparison.OrdinalIgnoreCase)
                || !selected.Contains(Path.GetDirectoryName(file.SourcePath)!.Replace('\\', '/'))) continue;
            var source = ResolveWithin(mod.Path, file.SourcePath);
            if (!File.Exists(source)) throw new FileNotFoundException("HD2IAS 组件文件缺失，请重新导入。", source);
            // 目标必须仍符合导入时的显式映射。
            var target = file.TargetPath.Replace('\\', '/');
            ResolveWithin(Path.Combine(mod.Path, "bin", "HD2IndependentRig"), target["bin/HD2IndependentRig/".Length..]);
            files.Add(new(source, target, true));
        }
        return files;
    }

    private static string ResolveWithin(string root, string relative)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("HD2IAS 文件映射超出目标目录。");
        return fullPath;
    }
}
