using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Migration;
using HD2Mimnt.Core.Services;
using HD2Mimnt.Infrastructure.Archive;
using HD2Mimnt.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Mod Import Service
/// 负责导入 Mod（从文件夹或压缩包文件，压缩包格式由 IArchiveExtractor 决定）
/// </summary>
public class ModImportService
{
    private readonly IModRepository _modRepository;
    private readonly string _modsStoragePath;
    private readonly ILogger<ModImportService>? _logger;
    private readonly IArchiveExtractor _archiveExtractor;
    private readonly string _tempDirectory;
    private readonly ManifestParser _manifestParser = new();

    public ModImportService(
        IModRepository modRepository,
        string modsStoragePath,
        ILogger<ModImportService>? logger = null,
        IArchiveExtractor? archiveExtractor = null,
        string? tempDirectory = null)
    {
        _modRepository = modRepository;
        _modsStoragePath = modsStoragePath;
        _logger = logger;
        _archiveExtractor = archiveExtractor ?? new SharpCompressArchiveExtractor();
        _tempDirectory = tempDirectory ?? Path.GetTempPath();

        Directory.CreateDirectory(_modsStoragePath);
    }

    /// <summary>
    /// 从文件夹或 ZIP 文件导入 Mod
    /// </summary>
    public async Task<Guid> ImportModAsync(string sourcePath, CancellationToken cancellationToken = default,
        IProgress<ModImportProgress>? progress = null, ModPlatformLink? sourceLink = null)
    {
        var mod = await PrepareModAsync(sourcePath, cancellationToken, progress);
        var saved = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceLink != null)
            {
                ModMetadataStore.Apply(mod, sourceLink);
                mod.Source = sourceLink.PlatformId switch
                {
                    "nexusmods" => ModSource.NexusMods,
                    "ayakamods" => ModSource.AyakaMods,
                    _ => mod.Source
                };
                await new ModMetadataStore(_modsStoragePath, _logger).WriteAsync(mod.Id, sourceLink, cancellationToken);
            }
            progress?.Report(new(90, "保存模组", mod.Name, true));
            await _modRepository.AddAsync(mod, cancellationToken);
            saved = true;
            progress?.Report(new(95, "模组已保存", mod.Name));
            return mod.Id;
        }
        finally
        {
            if (!saved && sourceLink != null)
                await new ModMetadataStore(_modsStoragePath, _logger).WriteAsync(mod.Id, null, CancellationToken.None);
            if (!saved && Directory.Exists(mod.Path)) Directory.Delete(mod.Path, true);
        }
    }

    /// <summary>准备独立副本，不落库；调用方取得成功结果后负责保留或清理。</summary>
    internal async Task<Mod> PrepareModAsync(string sourcePath, CancellationToken cancellationToken = default,
        IProgress<ModImportProgress>? progress = null, ExternalMigrationMod? migration = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(0, "检查源文件", Path.GetFileName(sourcePath)));
        _logger?.LogInformation("开始导入 Mod: {SourcePath}", sourcePath);

        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Mod 路径不存在: {sourcePath}");
        }

        string tempExtractPath = Path.Combine(_tempDirectory, $"hd2mimnt_import_{Guid.NewGuid()}");
        string? modRootPath = null;
        string fallbackName;
        bool isArchiveFile = false;
        string? targetPath = null;
        bool saved = false;

        try
        {
            // 如果是压缩包文件（zip/7z/rar 等），先解压到临时目录
            if (File.Exists(sourcePath) && _archiveExtractor.IsArchiveFile(sourcePath))
            {
                _logger?.LogInformation("检测到压缩包文件，开始解压: {SourcePath}", sourcePath);
                isArchiveFile = true;
                progress?.Report(new(5, "解压模组", Path.GetFileName(sourcePath), true));
                await _archiveExtractor.ExtractToDirectoryAsync(sourcePath, tempExtractPath, cancellationToken);
                modRootPath = tempExtractPath;
                // manifest 缺失时的回退名：压缩包文件名，而不是临时解压目录的 GUID
                fallbackName = Path.GetFileNameWithoutExtension(sourcePath);
            }
            else
            {
                modRootPath = sourcePath;
                fallbackName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            // 读取 manifest：真实模组压缩包常带一层同名子文件夹，根目录没有时向下查找
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(20, "读取模组清单", "识别模组信息和选项", true));
            var manifest = migration == null ? await ReadManifestAsync(modRootPath, cancellationToken)
                : System.Text.Json.JsonSerializer.Deserialize<ModManifest>(System.Text.Json.JsonSerializer.Serialize(migration.Manifest));
            if (manifest == null)
            {
                var nestedRoot = FindManifestRoot(modRootPath);
                if (nestedRoot != null)
                {
                    _logger?.LogInformation("manifest.json 位于子目录，使用其所在目录作为 Mod 根目录: {ModRoot}", nestedRoot);
                    modRootPath = nestedRoot;
                    manifest = await ReadManifestAsync(modRootPath, cancellationToken);
                }
            }

            var hasParsedManifest = manifest != null;
            // 无 manifest 时按旧版管理器语义推断：有子目录时把子目录名作为互斥选项（下拉选择）
            manifest ??= InferManifestFromDirectory(modRootPath, fallbackName);

            // 分析 Mod 内容
            progress?.Report(new(30, "分析模组文件", "统计文件大小和内容", true));
            var modInfo = AnalyzeModContent(modRootPath, cancellationToken);

            // 创建 Mod 实体
            var importedAt = DateTime.UtcNow;
            var mod = new Mod
            {
                Id = Guid.NewGuid(),
                Name = !string.IsNullOrWhiteSpace(manifest?.Name) ? manifest.Name : fallbackName,
                Version = manifest?.Version ?? "1.0.0",
                Author = manifest?.Author ?? "Unknown",
                Description = manifest?.Description ?? "",
                Category = DetermineCategory(manifest, modInfo),
                Status = ModStatus.Ready,
                Tags = manifest?.Tags ?? new List<string>(),
                Size = modInfo.TotalSize,
                Source = isArchiveFile ? ModSource.LocalZip : ModSource.LocalFolder,
                ContainsScripts = modInfo.ContainsScripts,
                ImportedAt = importedAt,
                CreatedAt = importedAt,
                ModifiedAt = importedAt,
                // manifest（含 v0/v1 选项定义）必须落库，配置文件的模组选项功能依赖它
                Manifest = manifest
            };

            // 复制 Mod 到存储目录
            targetPath = Path.Combine(_modsStoragePath, mod.Id.ToString());
            Directory.CreateDirectory(targetPath);
            if (migration == null)
            {
                await CopyDirectoryAsync(modRootPath, targetPath, modInfo.TotalSize, progress, cancellationToken);
                if (hasParsedManifest) await NormalizeStoredManifestAsync(targetPath, manifest!, cancellationToken);
                mod.Size = AnalyzeModContent(targetPath, cancellationToken).TotalSize;
            }
            else
            {
                await CopyMigratedDirectoryAsync(migration, targetPath, cancellationToken);
                var manifestPath = Path.Combine(targetPath, "manifest.json");
                System.Text.Json.Nodes.JsonObject? original = null;
                if (File.Exists(manifestPath))
                    original = await ManifestPackage.ReadAsync(manifestPath, cancellationToken);
                if (!string.IsNullOrWhiteSpace(migration.ImagePath))
                {
                    if (!File.Exists(migration.ImagePath)) throw new FileNotFoundException("源预览图不存在", migration.ImagePath);
                    var imageName = "migration-preview" + Path.GetExtension(migration.ImagePath);
                    await CopyMigrationFileAsync(migration.ImagePath, Path.Combine(targetPath, imageName), cancellationToken);
                    manifest!.IconPath = imageName;
                    mod.ThumbnailPath = Path.Combine(targetPath, imageName);
                }
                manifest!.Tags = migration.Tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                mod.Tags = manifest.Tags;
                _manifestParser.SaveToFile(manifest, manifestPath, original);
                mod.Size = AnalyzeModContent(targetPath, cancellationToken).TotalSize;
            }
            mod.Path = targetPath;
            if (string.IsNullOrWhiteSpace(mod.ThumbnailPath))
                mod.ThumbnailPath = ResolveManifestIconPath(targetPath, manifest?.IconPath) ?? string.Empty;
            progress?.Report(new(80, "识别资源类型", "扫描模组资源索引", true));
            await PopulateResourceTypesAsync(mod, cancellationToken);
            try
            {
                mod.BslRole = await HD2Mimnt.Infrastructure.Analysis.BslModDetector.DetectAsync(mod, cancellationToken);
                if (mod.BslRole != null)
                    mod.BslDetectionVersion = HD2Mimnt.Infrastructure.Analysis.BslModDetector.DetectionVersion;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { _logger?.LogWarning(ex, "BSL 依赖识别失败: {ModPath}", mod.Path); }
            mod.Hash = await ModHashService.CalculateAsync(targetPath, cancellationToken, progress);

            // 将准备好的副本交给调用方；更新不创建临时 Mod 行。
            cancellationToken.ThrowIfCancellationRequested();
            saved = true;

            _logger?.LogInformation("Mod 导入成功: {ModName} ({ModId})", mod.Name, mod.Id);
            return mod;
        }
        finally
        {
            // 取消或失败时移除尚未落库的副本，保留已成功导入的模组。
            if (!saved && targetPath != null && Directory.Exists(targetPath))
            {
                try { Directory.Delete(targetPath, true); }
                catch (Exception ex) { _logger?.LogWarning(ex, "清理未完成的导入失败: {TargetPath}", targetPath); }
            }
            // 清理临时目录
            if (isArchiveFile && Directory.Exists(tempExtractPath))
            {
                try
                {
                    Directory.Delete(tempExtractPath, true);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "清理临时目录失败: {TempPath}", tempExtractPath);
                }
            }
        }
    }

    /// <summary>清单图片以模组目录为基准，导入副本和历史回填使用同一规则。</summary>
    internal static string? ResolveManifestIconPath(string modPath, string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(modPath) || string.IsNullOrWhiteSpace(iconPath)) return null;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modPath));
            var image = Path.GetFullPath(iconPath.Replace('/', Path.DirectorySeparatorChar), root);
            return image.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && File.Exists(image) ? image : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        { return null; }
    }

    internal async Task PopulateResourceTypesAsync(Mod mod, CancellationToken cancellationToken)
    {
        if (mod.ResourceTypeIds != null) return;
        try
        {
            mod.ResourceTypeIds = await HD2Mimnt.Infrastructure.Analysis.ResourceTypeScanner.ScanAsync(mod.Path, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 单个模组索引失败不能阻断库加载/导入；保持 null，后续刷新可重试。
            _logger?.LogWarning(ex, "资源 Type ID 识别失败: {ModPath}", mod.Path);
        }
    }

    /// <summary>
    /// 批量导入 Mod
    /// </summary>
    public async Task<IEnumerable<Guid>> ImportModsAsync(IEnumerable<string> sourcePaths, CancellationToken cancellationToken = default)
    {
        var importedIds = new List<Guid>();

        foreach (var path in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var modId = await ImportModAsync(path, cancellationToken);
                importedIds.Add(modId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "导入 Mod 失败: {Path}", path);
            }
        }

        return importedIds;
    }

    /// <summary>
    /// 读取 Mod manifest 文件。
    /// 旧版格式的 manifest（v1 的 "Version" 是数字、v0 的 Options 是字符串数组）无法直接
    /// 反序列化为 ModManifest，会抛异常导致整个清单被丢弃；改用 ManifestParser 按版本语义解析。
    /// </summary>
    private async Task<ModManifest?> ReadManifestAsync(string modPath, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(modPath, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            _logger?.LogInformation("未找到 manifest.json 文件: {ManifestPath}", manifestPath);
            return null;
        }

        try
        {
            return await _manifestParser.ParseFromFileAsync(manifestPath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "读取 manifest.json 失败: {ManifestPath}", manifestPath);
            return null;
        }
    }

    private async Task NormalizeStoredManifestAsync(string directory, ModManifest manifest, CancellationToken token)
    {
        var path = Path.Combine(directory, "manifest.json");
        var original = ManifestPackage.Parse(await File.ReadAllTextAsync(path, token));
        // 已符合标准的包保持原字节及内容哈希；仅转换仍内嵌扩展的库副本。
        if (System.Text.Json.Nodes.JsonNode.DeepEquals(original, ManifestPackage.Separate(original).Manifest)) return;
        var combined = await ManifestPackage.ReadAsync(path, token,
            error => _logger?.LogWarning(error, "扩展元数据不可用，保留清单中的兼容字段: {Path}", path));
        foreach (var key in combined.Select(pair => pair.Key).Where(key => key.Equals("Guid", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Version", StringComparison.OrdinalIgnoreCase) || key.Equals("ModVersion", StringComparison.OrdinalIgnoreCase)).ToArray())
            combined.Remove(key);
        combined["Guid"] = manifest.Guid.ToString();
        if (manifest.ManifestVersion == ManifestVersion.V1) combined["Version"] = 1;
        combined["ModVersion"] = manifest.Version;
        await ManifestPackage.SaveAsync(path, combined, token);
    }

    /// <summary>
    /// 从模组目录读取 manifest（含嵌套子目录）。
    /// 与导入使用相同的解析规则，供历史数据回填复用。
    /// </summary>
    public async Task<ModManifest?> ReadManifestFromDirectoryAsync(string modPath, CancellationToken cancellationToken = default)
    {
        var manifest = await ReadManifestAsync(modPath, cancellationToken);
        if (manifest == null)
        {
            var nestedRoot = FindManifestRoot(modPath);
            if (nestedRoot != null)
            {
                manifest = await ReadManifestAsync(nestedRoot, cancellationToken);
            }
        }

        return manifest;
    }

    /// <summary>
    /// 向下查找 manifest.json 所在目录（浅层优先）：真实模组压缩包常套一层同名子文件夹，
    /// 旧管理器同样按嵌套目录解析。返回 null 表示根目录已有 manifest 或不存在。
    /// </summary>
    private static string? FindManifestRoot(string modPath)
    {
        if (File.Exists(Path.Combine(modPath, "manifest.json")))
        {
            return null;
        }

        if (!Directory.Exists(modPath))
        {
            return null;
        }

        return Directory
            .EnumerateDirectories(modPath, "*", SearchOption.AllDirectories)
            .OrderBy(dir => dir.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar))
            .FirstOrDefault(dir => File.Exists(Path.Combine(dir, "manifest.json")));
    }

    /// <summary>
    /// 无 manifest 时的推断（与旧版管理器 InferFromDirectory 语义一致）：
    /// 有子目录时把子目录名作为互斥的 Legacy 选项，部署时只使用选中的那个子目录；
    /// 没有子目录时返回 null（模组按整体部署）。导入与历史数据回填共用。
    /// </summary>
    public static ModManifest? InferManifestFromDirectory(string modRootPath, string fallbackName)
    {
        if (!Directory.Exists(modRootPath))
        {
            return null;
        }

        var subDirectories = Directory.GetDirectories(modRootPath);
        if (subDirectories.Length == 0)
        {
            return null;
        }

        return new ModManifest
        {
            Name = fallbackName,
            Description = "A locally imported mod.",
            ManifestVersion = ManifestVersion.Legacy,
            LegacyOptions = subDirectories.Select(Path.GetFileName).ToList()
        };
    }

    /// <summary>
    /// 分析 Mod 内容
    /// </summary>
    private ModContentInfo AnalyzeModContent(string modPath, CancellationToken cancellationToken)
    {
        var info = new ModContentInfo();

        if (!Directory.Exists(modPath))
        {
            return info;
        }

        var files = Directory.GetFiles(modPath, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            info.TotalSize += new FileInfo(file).Length;
        }

        // 检测是否包含脚本文件
        info.ContainsScripts = files.Any(f =>
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            return ext == ".lua" || ext == ".py" || ext == ".js";
        });

        // 检测文件类型
        info.FileTypes = files
            .Select(f => Path.GetExtension(f).ToLowerInvariant())
            .Where(ext => !string.IsNullOrEmpty(ext))
            .Distinct()
            .ToList();

        return info;
    }

    /// <summary>
    /// 根据 manifest 和内容分析确定 Mod 类别
    /// </summary>
    private ModCategory DetermineCategory(ModManifest? manifest, ModContentInfo contentInfo)
    {
        if (manifest?.Category != null)
        {
            if (Enum.TryParse<ModCategory>(manifest.Category, true, out var category))
            {
                return category;
            }
        }

        // 根据文件内容推断类别
        if (contentInfo.FileTypes.Any(ext => ext == ".lua" || ext == ".py"))
        {
            return ModCategory.Gameplay;
        }

        if (contentInfo.FileTypes.Any(ext => ext == ".png" || ext == ".jpg" || ext == ".dds"))
        {
            return ModCategory.Visual;
        }

        return ModCategory.Other;
    }

    /// <summary>
    /// 递归复制目录
    /// </summary>
    private static async Task CopyDirectoryAsync(string sourceDir, string targetDir, long totalSize,
        IProgress<ModImportProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new(35, "复制模组文件", "准备复制到模组库"));
        long copied = 0;
        var buffer = new byte[81920];
        var lastReport = Environment.TickCount64;
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceDir, file);
            var targetFile = Path.Combine(targetDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, true);
            await using var output = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, true);
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
                if (Environment.TickCount64 - lastReport >= 100)
                {
                    progress?.Report(new(35 + (totalSize > 0 ? 45d * copied / totalSize : 45), "复制模组文件", relativePath));
                    lastReport = Environment.TickCount64;
                }
            }
        }
        // 保留空目录：无 manifest 的旧格式可能将目录名作为选项。
        foreach (var directory in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(targetDir, Path.GetRelativePath(sourceDir, directory)));
        }
        progress?.Report(new(80, "复制模组文件", "文件复制完成"));
    }

    private class ModContentInfo
    {
        public long TotalSize { get; set; }
        public bool ContainsScripts { get; set; }
        public List<string> FileTypes { get; set; } = new();
    }

    private static async Task CopyMigratedDirectoryAsync(ExternalMigrationMod migration, string target, CancellationToken token)
    {
        var layout = migration.FileLayout.GroupBy(file => Inside(migration.SourcePath, file.SourceRelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(file => Inside(target, file.TargetRelativePath)).ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var file in layout.Keys)
            if (!File.Exists(file)) throw new FileNotFoundException("源资源集合文件不存在", file);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(migration.SourcePath, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var targets = layout.GetValueOrDefault(Path.GetFullPath(file)) ?? [Inside(target, Path.GetRelativePath(migration.SourcePath, file))];
            foreach (var destination in targets)
            {
                if (!destinations.Add(destination)) throw new InvalidDataException($"源资源集合目标重名：{destination}");
                await CopyMigrationFileAsync(file, destination, token);
            }
        }
        foreach (var directory in Directory.EnumerateDirectories(migration.SourcePath, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Inside(target, Path.GetRelativePath(migration.SourcePath, directory)));
        }
    }

    private static async Task CopyMigrationFileAsync(string source, string target, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, token);
    }

    private static string Inside(string root, string relative)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(normalized, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"迁移文件路径越出模组目录：{relative}");
        return full;
    }
}
