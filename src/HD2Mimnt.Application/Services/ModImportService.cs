using HD2Mimnt.Core.IO;
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
    private readonly SettingsService? _settingsService;
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private readonly Dictionary<(string Path, string Hash), string> _duplicateFingerprints = new();

    public ModImportService(
        IModRepository modRepository,
        string modsStoragePath,
        ILogger<ModImportService>? logger = null,
        IArchiveExtractor? archiveExtractor = null,
        string? tempDirectory = null, SettingsService? settingsService = null)
    {
        _modRepository = modRepository;
        _modsStoragePath = modsStoragePath;
        _logger = logger;
        _archiveExtractor = archiveExtractor ?? (settingsService == null ? new SharpCompressArchiveExtractor()
            : new PasswordAwareArchiveExtractor(new ArchivePasswordService(settingsService)));
        _tempDirectory = tempDirectory ?? Path.GetTempPath();
        _settingsService = settingsService;

        Directory.CreateDirectory(_modsStoragePath);
    }

    /// <summary>
    /// 从文件夹或 ZIP 文件导入 Mod
    /// </summary>
    public async Task<Guid> ImportModAsync(string sourcePath, CancellationToken cancellationToken = default,
        IProgress<ModImportProgress>? progress = null, ModPlatformLink? sourceLink = null,
        Func<Hd2IrImportPreview, CancellationToken, Task>? confirmHd2Ir = null)
    {
        var mod = await PrepareModAsync(sourcePath, cancellationToken, progress, confirmHd2Ir: confirmHd2Ir);
        var saved = false;
        var entered = false;
        try
        {
            await _importGate.WaitAsync(cancellationToken);
            entered = true;
            if (_settingsService?.GetSetting(SettingKeys.AllowDuplicateImports, false) != true)
                await RejectDuplicateAsync(mod, cancellationToken, progress);
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
            if (entered) { _duplicateFingerprints.Clear(); _importGate.Release(); }
            if (!saved && sourceLink != null)
                await new ModMetadataStore(_modsStoragePath, _logger).WriteAsync(mod.Id, null, CancellationToken.None);
            if (!saved && Directory.Exists(mod.Path)) Directory.Delete(mod.Path, true);
        }
    }

    private async Task RejectDuplicateAsync(Mod prepared, CancellationToken token,
        IProgress<ModImportProgress>? progress = null)
    {
        foreach (var existing in await _modRepository.GetAllAsync(token))
        {
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(existing.Path) && await IsSameContentAsync(prepared, existing, token, progress))
                throw new InvalidOperationException($"模组库中已存在相同内容：{existing.Name}。如需独立副本，请在设置中开启“允许重复导入”。");
            foreach (var version in await new ModVersionStore(_modsStoragePath).ReadAsync(existing.Id, token))
            {
                if (Directory.Exists(version.Content.Path) && await IsSameContentAsync(prepared, version.Content, token, progress))
                    throw new InvalidOperationException($"此内容已保存在 {existing.Name} 的本地版本中，请在模组详情中切换版本。");
            }
        }
    }

    private async Task<bool> IsSameContentAsync(Mod prepared, Mod existing, CancellationToken token,
        IProgress<ModImportProgress>? progress = null)
    {
        if (!string.IsNullOrWhiteSpace(existing.Hash) && existing.Hash == prepared.Hash) return true;
        // 旧包规范化会生成清单 GUID；HD2IR 映射还包含随机 ID/时间。
        // 比较作者元数据和完整文件指纹，排除这些运行时身份，普通 Hash 保持原有语义。
        static async Task<string> MetadataAsync(string root, CancellationToken ct)
        {
            var path = Path.Combine(root, "manifest.json");
            if (!File.Exists(path)) return "";
            // 导入时自动生成的档案属于生成身份（名称来自来源），不参与作者元数据比较。
            if (ManifestPackage.IsGeneratedManifest(root)) return "";
            var manifest = await ManifestPackage.ReadAsync(path, ct);
            foreach (var key in manifest.Select(p => p.Key).Where(k => k.Equals("Guid", StringComparison.OrdinalIgnoreCase)).ToArray()) manifest.Remove(key);
            static System.Text.Json.Nodes.JsonNode? Sort(System.Text.Json.Nodes.JsonNode? node) => node switch
            {
                System.Text.Json.Nodes.JsonObject obj => new System.Text.Json.Nodes.JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => KeyValuePair.Create(p.Key, Sort(p.Value)))),
                System.Text.Json.Nodes.JsonArray array => new System.Text.Json.Nodes.JsonArray(array.Select(Sort).ToArray()),
                _ => node?.DeepClone()
            };
            return Sort(manifest)!.ToJsonString();
        }
        var metadata = await MetadataAsync(prepared.Path, token);
        if (metadata != await MetadataAsync(existing.Path, token)) return false;
        if (metadata.Length == 0 && prepared.WorkflowType != Hd2IrIntegrationService.Workflow && !string.IsNullOrWhiteSpace(existing.Hash)) return false;
        async Task<string> Fingerprint(Mod mod)
        {
            if (_duplicateFingerprints.TryGetValue((mod.Path, mod.Hash), out var cached)) return cached;
            var hash = await ModDirectoryHash.CalculateAsync(mod.Path, token, progress: progress, includeFile: path =>
            {
                var relative = Path.GetRelativePath(mod.Path, path);
                // 生成身份（组件映射、组件选项树、导入自动生成的档案）不参与重复判定；
                // 作者清单的差异由上方元数据比较处理。
                return !relative.Equals(Hd2IrIntegrationService.MetadataFile, StringComparison.OrdinalIgnoreCase)
                    && !relative.Equals(Hd2IrIntegrationService.OptionsFile, StringComparison.OrdinalIgnoreCase)
                    && !relative.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
                    && !relative.Equals(ManifestPackage.MetadataFileName, StringComparison.OrdinalIgnoreCase);
            }, stepName: "判重比对", startPercentage: 89, spanPercentage: 0);
            _duplicateFingerprints[(mod.Path, mod.Hash)] = hash;
            return hash;
        }
        return await Fingerprint(prepared) == await Fingerprint(existing);
    }

    /// <summary>准备独立副本，不落库；调用方取得成功结果后负责保留或清理。</summary>
    internal async Task<Mod> PrepareModAsync(string sourcePath, CancellationToken cancellationToken = default,
        IProgress<ModImportProgress>? progress = null, ExternalMigrationMod? migration = null,
        Func<Hd2IrImportPreview, CancellationToken, Task>? confirmHd2Ir = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(0, "检查源文件", Path.GetFileName(sourcePath)));
        _logger?.LogInformation("开始导入 Mod: {SourcePath}", sourcePath);

        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Mod 路径不存在: {sourcePath}");
        }

        string tempExtractPath = Path.Combine(_tempDirectory, $"hd2mimnt_import_{Guid.NewGuid()}");
        string hd2IrWorkspace = Path.Combine(_tempDirectory, $"hd2mimnt_hd2ir_{Guid.NewGuid():N}");
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
                FlattenSingleRootFolder(tempExtractPath, cancellationToken);
                modRootPath = tempExtractPath;
                // manifest 缺失时的回退名：压缩包文件名，而不是临时解压目录的 GUID
                fallbackName = Path.GetFileNameWithoutExtension(sourcePath);
            }
            else
            {
                modRootPath = sourcePath;
                fallbackName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            Hd2IrIntegrationService.Prepared? hd2Ir = null;
            if (migration == null)
            {
                progress?.Report(new(15, "识别 HD2IAS 组件", "检查嵌套压缩包和部署映射", true));
                hd2Ir = await new Hd2IrIntegrationService(_archiveExtractor).PrepareAsync(modRootPath,
                    Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    hd2IrWorkspace, confirmHd2Ir, cancellationToken);
                if (hd2Ir != null) modRootPath = hd2Ir.Root;
            }

            // 整棵解压树都没有清单时，外层不是模组：展开其中的嵌套压缩包，内层包才是真正的模组或补丁包。
            // HD2IR 识别优先（它自己按包分组嵌套压缩包）；已有的嵌套清单查找和合集识别在展开后照常生效。
            HashSet<string>? nestedPackages = null;
            if (migration == null && hd2Ir == null && isArchiveFile && !ContainsManifest(modRootPath))
            {
                nestedPackages = await ExpandNestedArchivesAsync(modRootPath, progress, cancellationToken);
                if (nestedPackages.Count > 0) FlattenSingleRootFolder(modRootPath, cancellationToken);
            }

            // 散装文件夹合集：外层没有清单、每个子目录各自是模组时，按一个模组导入并生成两级组件选项。
            // HD2IR/HD2IAS 已识别的内容优先，合集只在普通模组路径生效；识别只枚举目录，不单独占用进度步骤。
            ModCollectionImporter.Collection? collection = null;
            if (migration == null && hd2Ir == null)
            {
                collection = await ModCollectionImporter.TryBuildAsync(modRootPath, fallbackName, cancellationToken,
                    nestedPackages);
                if (collection != null) modRootPath = collection.Root;
            }

            // 读取 manifest：真实模组压缩包常带一层同名子文件夹，根目录没有时向下查找
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(20, "读取模组清单", "识别模组信息和选项", true));
            var manifest = hd2Ir?.Manifest ?? collection?.Manifest
                ?? (migration == null ? await ReadManifestAsync(modRootPath, cancellationToken)
                    : System.Text.Json.JsonSerializer.Deserialize<ModManifest>(System.Text.Json.JsonSerializer.Serialize(migration.Manifest)));
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

            // 合集/HD2IAS 的清单由管理器生成，没有作者根清单需要规范化。
            var hasParsedManifest = manifest != null && collection == null;
            // 无 manifest 时按设置推断：若启用 ForceManifestV1，生成 V1 清单；否则按旧版语义生成 Legacy 清单
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
            if (hd2Ir != null)
            {
                mod.WorkflowType = Hd2IrIntegrationService.Workflow;
                mod.LogicalMod = hd2Ir.LogicalMod;
                hd2Ir.LogicalMod.Id = mod.Id;
                await File.WriteAllTextAsync(Path.Combine(modRootPath, Hd2IrIntegrationService.MetadataFile),
                    System.Text.Json.JsonSerializer.Serialize(hd2Ir.LogicalMod), cancellationToken);
            }

            // 复制 Mod 到存储目录
            targetPath = Path.Combine(_modsStoragePath, mod.Id.ToString());
            Directory.CreateDirectory(targetPath);
            if (migration == null)
            {
                await CopyDirectoryAsync(modRootPath, targetPath, modInfo.TotalSize, progress, cancellationToken);
                // 合集：生成的组件分组/选项树只进管理器自有 JSON，作者子清单保持原字节。
                if (collection != null) await Hd2IrIntegrationService.SaveOptionsAsync(targetPath, manifest!, cancellationToken);
                else if (hasParsedManifest && hd2Ir == null) await NormalizeStoredManifestAsync(targetPath, manifest!, cancellationToken);
                // 无作者清单的普通模组：把推断档案写进库内副本，便于查看和复用；
                // 文件带生成标记，不参与内容哈希与判重元数据。
                else if (hd2Ir == null && manifest != null)
                    _manifestParser.SaveGeneratedToFile(manifest, Path.Combine(targetPath, "manifest.json"));
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
                mod.ThumbnailPath = ResolveCardPreviewPath(targetPath, manifest?.IconPath) ?? string.Empty;
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
            if (Directory.Exists(hd2IrWorkspace))
            {
                try { Directory.Delete(hd2IrWorkspace, true); }
                catch (Exception ex) { _logger?.LogWarning(ex, "清理 HD2IAS 临时目录失败: {TempPath}", hd2IrWorkspace); }
            }
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

    /// <summary>
    /// 与旧管理器一致，压缩包解压出“唯一根文件夹且根上无文件”时拍平一层目录。
    /// 仅处理内部完全没有 manifest.json 的包：简装补丁包的补丁在同名子文件夹里，
    /// 不拍平会落入“推断出无 Include 的 V1 选项 → 部署回退全量目录只扫根直属文件”
    /// 而收集不到任何补丁；带清单的包保持原结构，交给嵌套清单查找和 HD2IR/合集识别。
    /// </summary>
    private static void FlattenSingleRootFolder(string root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directories = Directory.GetDirectories(root);
        if (directories.Length != 1 || Directory.GetFiles(root).Length != 0) return;
        var inner = directories[0];
        if (Directory.EnumerateFiles(inner, "manifest.json", SearchOption.AllDirectories).Any()) return;

        foreach (var directory in Directory.EnumerateDirectories(inner))
            Directory.Move(directory, Path.Combine(root, Path.GetFileName(directory)));
        foreach (var file in Directory.EnumerateFiles(inner))
            File.Move(file, Path.Combine(root, Path.GetFileName(file)));
        Directory.Delete(inner, true);
    }

    /// <summary>
    /// 展开解压树根部的嵌套压缩包：每个包解到同名的同级目录，成功后删除原压缩包。
    /// 只在整棵树没有任何清单时调用——外层本身不是模组，内层包才是发布单位（常见于“一个角色多套补丁”打包）。
    /// 只展开一轮：内层包再套压缩包请先解包，避免把已展开模组自带的素材压缩包也解掉。
    /// 返回展开出的目录（绝对路径），供合集识别只把这些目录当作无清单组件。
    /// </summary>
    private async Task<HashSet<string>> ExpandNestedArchivesAsync(string root, IProgress<ModImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in EnumerateNestedArchives(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(12, "展开嵌套压缩包", Path.GetFileName(archive), true));
            var destination = CreatePackageDirectory(Path.GetDirectoryName(archive)!, Path.GetFileName(archive));
            try
            {
                await _archiveExtractor.ExtractToDirectoryAsync(archive, destination, cancellationToken);
                FlattenPackageWrapper(destination, cancellationToken);
            }
            catch
            {
                if (Directory.Exists(destination)) Directory.Delete(destination, true);
                throw;
            }

            File.Delete(archive);
            expanded.Add(Path.TrimEndingDirectorySeparator(destination));
            _logger?.LogInformation("已展开嵌套压缩包: {ArchivePath} -> {Destination}", archive, destination);
        }
        return expanded;
    }

    /// <summary>只展开根目录和一层子目录里的压缩包；更深层的多是模组自带的素材包，保持原样。</summary>
    private List<string> EnumerateNestedArchives(string root)
    {
        var archives = new List<string>();
        void Walk(string directory, int depth)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                // .hd2irpack.zip 由 Hd2IR 识别按文件名判断，展开成目录会丢失扩展名证据。
                if (_archiveExtractor.IsArchiveFile(file)
                    && !file.EndsWith(".hd2irpack.zip", StringComparison.OrdinalIgnoreCase))
                    archives.Add(file);
            }
            if (depth >= 1) return;
            foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                Walk(child, depth + 1);
        }
        Walk(root, 0);
        return archives.Order(StringComparer.Ordinal).ToList();
    }

    private static string CreatePackageDirectory(string parent, string archiveName)
    {
        var stem = StripArchiveExtension(archiveName);
        if (stem.Length == 0) stem = "package";
        var destination = Path.Combine(parent, stem);
        for (var index = 2; Directory.Exists(destination) || File.Exists(destination); index++)
            destination = Path.Combine(parent, $"{stem} ({index})");
        return destination;
    }

    private static string StripArchiveExtension(string name)
    {
        foreach (var suffix in new[] { ".tar.gz", ".tar.bz2", ".tar.xz", ".tgz", ".tbz2",
                     ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length)
                return name[..^suffix.Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>
    /// 内层包解出的唯一包裹目录拍平。这里不看清单：包裹层只是打包习惯，
    /// 清单随内容一起上移，Include 仍相对清单所在目录，语义不变。
    /// </summary>
    private static void FlattenPackageWrapper(string root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directories = Directory.GetDirectories(root);
        if (directories.Length != 1 || Directory.GetFiles(root).Length != 0) return;
        var inner = directories[0];
        foreach (var directory in Directory.EnumerateDirectories(inner))
            Directory.Move(directory, Path.Combine(root, Path.GetFileName(directory)));
        foreach (var file in Directory.EnumerateFiles(inner))
            File.Move(file, Path.Combine(root, Path.GetFileName(file)));
        Directory.Delete(inner, true);
    }

    private static bool ContainsManifest(string root)
    {
        if (!Directory.Exists(root)) return false;
        return File.Exists(Path.Combine(root, "manifest.json"))
            || Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories).Any();
    }

    /// <summary>清单图片以模组目录为基准，导入副本和历史回填使用同一规则。</summary>
    internal static string? ResolveManifestIconPath(string modPath, string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(modPath) || string.IsNullOrWhiteSpace(iconPath)) return null;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modPath));
            var image = Path.GetFullPath(iconPath.Replace('/', Path.DirectorySeparatorChar), root);
            return PathGuard.IsInsideRoot(root, image) && File.Exists(image) ? image : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        { return null; }
    }

    /// <summary>卡片优先使用作者指定图片，否则只查找模组根目录；不借用子目录的选项图片。</summary>
    internal static string? ResolveCardPreviewPath(string modPath, string? iconPath)
    {
        if (ResolveManifestIconPath(modPath, iconPath) is { } declared) return declared;
        if (!Directory.Exists(modPath)) return null;
        return Directory.EnumerateFiles(modPath, "*", SearchOption.TopDirectoryOnly)
            .Where(file => Path.GetExtension(file).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")
            .OrderBy(file => Path.GetFileNameWithoutExtension(file).ToLowerInvariant() is "thumbnail" or "preview" or "cover" or "icon" ? 0 : 1)
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
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
    /// 有子目录时把子目录名作为互斥的选项；没有子目录时只生成基础档案（模组按整体部署）。
    /// 根据 ForceManifestV1 设置决定使用 V1 还是 Legacy 格式。导入、档案生成与历史数据回填共用。
    /// </summary>
    public ModManifest? InferManifestFromDirectory(string modRootPath, string fallbackName)
    {
        if (!Directory.Exists(modRootPath))
        {
            return null;
        }

        var subDirectories = Directory.GetDirectories(modRootPath);
        var forceV1 = _settingsService?.GetSetting(SettingKeys.ForceManifestV1, true) ?? true;
        var manifest = new ModManifest
        {
            Guid = Guid.NewGuid(),
            Name = fallbackName,
            Description = "A locally imported mod.",
            Version = "1.0.0",
            Author = "Unknown",
            ManifestVersion = forceV1 ? ManifestVersion.V1 : ManifestVersion.Legacy
        };
        if (subDirectories.Length == 0)
        {
            return manifest;
        }

        if (forceV1)
        {
            // V1 格式：每个子目录作为一个互斥的子选项
            manifest.Options = subDirectories.Select(dir =>
            {
                var dirName = Path.GetFileName(dir) ?? Path.GetDirectoryName(dir) ?? "Unknown";
                return new ModOption
                {
                    Name = dirName,
                    Description = $"Option for {dirName}",
                    DefaultEnabled = true,
                    SubOptions = new List<ModSubOption>()
                };
            }).ToList();
        }
        else
        {
            // Legacy 格式：子目录作为互斥下拉选项
            manifest.LegacyOptions = subDirectories.Select(d => Path.GetFileName(d) ?? "Unknown").ToList();
        }

        return manifest;
    }

    /// <summary>清单是否仍是“子目录推断”的原始结构（选项默认关闭、无 Include、描述由推断生成）。</summary>
    internal static bool IsUntouchedInferredOptions(ModManifest manifest)
        => manifest.ManifestVersion == ManifestVersion.V1
            && manifest.Options is { Count: > 0 } options
            && options.All(option => option.DefaultEnabled == false
                && option.Include == null
                && option.SubOptions is not { Count: > 0 }
                && option.Description == $"Option for {option.Name}");

    /// <summary>在上面的结构判断之外，再要求选项与当前子目录一一对应，避免覆盖用户编辑过的清单。</summary>
    internal static bool IsUntouchedInferredOptions(ModManifest manifest, string modRootPath)
        => IsUntouchedInferredOptions(manifest)
            && HasSameDirectories(manifest.Options!.Select(option => option.Name), modRootPath);

    /// <summary>数据库清单是否由目录推断生成：旧版推断不写 Guid，新版带固定描述且选项与子目录对应。</summary>
    internal static bool IsManagerInferredManifest(ModManifest manifest, string modRootPath)
    {
        if (manifest.Guid == Guid.Empty) return true;
        if (manifest.Description != "A locally imported mod.") return false;
        if (manifest.LegacyOptions is { Count: > 0 } legacyOptions)
            return HasSameDirectories(legacyOptions, modRootPath);
        if (manifest.ManifestVersion == ManifestVersion.Legacy || manifest.Options is null or { Count: 0 }) return true;
        return manifest.Options.All(option => option.Include == null
                && option.SubOptions is not { Count: > 0 }
                && option.Description == $"Option for {option.Name}")
            && HasSameDirectories(manifest.Options.Select(option => option.Name), modRootPath);
    }

    private static bool HasSameDirectories(IEnumerable<string> names, string modRootPath)
    {
        try
        {
            var directories = Directory.GetDirectories(modRootPath)
                .Select(dir => Path.GetFileName(dir))
                .OrderBy(name => name, StringComparer.Ordinal);
            return names.OrderBy(name => name, StringComparer.Ordinal)
                .SequenceEqual(directories, StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>把推断档案写入库内模组目录，与数据库清单保持一致（生成档案不参与内容哈希）。</summary>
    internal void WriteGeneratedManifest(Mod mod)
        => _manifestParser.SaveGeneratedToFile(mod.Manifest!, Path.Combine(mod.Path, "manifest.json"));

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
        return PathGuard.ResolveInsideRoot(normalized,
            Path.Combine(normalized, relative.Replace('/', Path.DirectorySeparatorChar)),
            $"迁移文件路径越出模组目录：{relative}");
    }

    /// <summary>
    /// 将无清单或 Legacy 清单的模组升级为 V1 清单格式
    /// 仅在 ForceManifestV1 设置启用时执行
    /// </summary>
    public async Task<bool> UpgradeModManifestToV1Async(Mod mod, CancellationToken cancellationToken = default)
    {
        var forceV1 = _settingsService?.GetSetting(SettingKeys.ForceManifestV1, true) ?? true;
        if (!forceV1)
        {
            _logger?.LogInformation("ForceManifestV1 未启用，跳过清单升级: {ModId}", mod.Id);
            return false;
        }

        // 如果已是 V1 格式，无需升级
        if (mod.Manifest?.ManifestVersion == ManifestVersion.V1)
        {
            _logger?.LogInformation("模组已是 V1 格式，无需升级: {ModId}", mod.Id);
            return false;
        }

        try
        {
            if (!Directory.Exists(mod.Path))
            {
                _logger?.LogWarning("模组路径不存在，无法升级清单: {ModPath}", mod.Path);
                return false;
            }

            var manifestPath = Path.Combine(mod.Path, "manifest.json");
            var hadManifest = File.Exists(manifestPath);
            ModManifest? newManifest = null;

            if (hadManifest)
            {
                // 已有清单文件，转换为 V1
                var currentManifest = await _manifestParser.ParseFromFileAsync(manifestPath, cancellationToken);
                if (currentManifest == null)
                {
                    _logger?.LogWarning("无法解析现有清单: {ManifestPath}", manifestPath);
                    return false;
                }

                // 复制基本信息
                newManifest = new ModManifest
                {
                    Guid = currentManifest.Guid,
                    Name = currentManifest.Name,
                    Description = currentManifest.Description,
                    Version = currentManifest.Version,
                    Author = currentManifest.Author,
                    AuthorUrl = currentManifest.AuthorUrl,
                    IconPath = currentManifest.IconPath,
                    Category = currentManifest.Category,
                    Tags = currentManifest.Tags,
                    ManifestVersion = ManifestVersion.V1,
                    NexusData = currentManifest.NexusData,
                    OptionGroups = currentManifest.OptionGroups
                };

                // 如果是 Legacy 格式，转换 Legacy 选项为 V1 选项
                if (currentManifest.ManifestVersion == ManifestVersion.Legacy && currentManifest.LegacyOptions?.Count > 0)
                {
                    newManifest.Options = currentManifest.LegacyOptions
                        .Select(option => new ModOption
                        {
                            Name = option,
                            Description = $"Option for {option}",
                            DefaultEnabled = true,
                            SubOptions = new List<ModSubOption>()
                        })
                        .ToList();
                }
            }
            else
            {
                // 无清单，根据目录推断
                newManifest = InferManifestFromDirectory(mod.Path, mod.Name);
            }

            if (newManifest == null)
            {
                _logger?.LogInformation("模组无子目录结构，无法生成清单: {ModId}", mod.Id);
                return false;
            }

            // 保存新清单：原本没有档案的属生成档案，标记后不参与内容哈希。
            if (hadManifest) _manifestParser.SaveToFile(newManifest, manifestPath);
            else _manifestParser.SaveGeneratedToFile(newManifest, manifestPath);
            mod.Manifest = newManifest;
            await _modRepository.UpdateAsync(mod, cancellationToken);

            _logger?.LogInformation("模组清单已升级为 V1 格式: {ModId}", mod.Id);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "升级模组清单失败: {ModId}", mod.Id);
            return false;
        }
    }

    /// <summary>
    /// 批量升级无清单或 Legacy 清单的模组为 V1 格式
    /// </summary>
    public async Task<(int upgraded, int skipped, int failed)> UpgradeAllModsManifestToV1Async(
        CancellationToken cancellationToken = default,
        IProgress<(int current, int total)>? progress = null)
    {
        var forceV1 = _settingsService?.GetSetting(SettingKeys.ForceManifestV1, true) ?? true;
        if (!forceV1)
        {
            _logger?.LogInformation("ForceManifestV1 未启用，跳过批量升级");
            return (0, 0, 0);
        }

        int upgraded = 0, skipped = 0, failed = 0;

        try
        {
            var allMods = await _modRepository.GetAllAsync(cancellationToken);
            var total = allMods.Count();
            int current = 0;

            foreach (var mod in allMods)
            {
                cancellationToken.ThrowIfCancellationRequested();
                current++;
                progress?.Report((current, total));

                // 已是 V1 或有 manifest 且不是 Legacy 的，跳过
                if (mod.Manifest?.ManifestVersion == ManifestVersion.V1)
                {
                    skipped++;
                    continue;
                }

                if (await UpgradeModManifestToV1Async(mod, cancellationToken))
                {
                    upgraded++;
                }
                else
                {
                    if (mod.Manifest == null)
                        skipped++; // 无子目录，无法升级
                    else
                        failed++;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "批量升级模组清单失败");
        }

        _logger?.LogInformation("模组清单批量升级完成: 升级 {Upgraded} 个，跳过 {Skipped} 个，失败 {Failed} 个", upgraded, skipped, failed);
        return (upgraded, skipped, failed);
    }
}
