using HD2Mimnt.Core.IO;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Migration;
using HD2Mimnt.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Mod 管理服务
/// 协调 Mod 的导入、删除、搜索等操作
/// </summary>
public partial class ModService
{
    private readonly IModRepository _modRepository;
    private readonly ModImportService _importService;
    private readonly ThumbnailService _thumbnailService;
    private readonly ILogger<ModService>? _logger;
    private readonly DeploymentService? _deploymentService;
    private readonly string _storageDirectory;
    private readonly ModMetadataStore _metadata;
    private readonly SettingsService? _settingsService;
    private readonly ProfileService? _profileService;
    private readonly SemaphoreSlim _hashGate = new(1, 1);
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    /// <summary>仅在查看缺失哈希的模组时补算，读取列表不扫描全部文件。</summary>
    public async Task<string> EnsureModHashAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _hashGate.WaitAsync(cancellationToken);
        try
        {
            var mod = await _modRepository.GetByIdAsync(id, cancellationToken)
                ?? throw new KeyNotFoundException("模组已不存在");
            if (!string.IsNullOrWhiteSpace(mod.Hash)) return mod.Hash;
            var hash = await Task.Run(() => ModHashService.CalculateAsync(mod.Path, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // 计算期间可能更新了其它模组信息，重新读取避免旧快照覆盖变更。
            mod = await _modRepository.GetByIdAsync(id, cancellationToken)
                ?? throw new KeyNotFoundException("模组已不存在");
            if (!string.IsNullOrWhiteSpace(mod.Hash)) return mod.Hash;
            mod.Hash = hash;
            await _modRepository.UpdateAsync(mod, cancellationToken);
            // 仅回填派生缓存，未改变模组内容；调用方直接更新详情，不能触发全库重载。
            return hash;
        }
        finally { _hashGate.Release(); }
    }

    public ModService(
        IModRepository modRepository,
        string storageDirectory,
        ILogger<ModService>? logger = null,
        DeploymentService? deploymentService = null,
        string? tempDirectory = null,
        SettingsService? settingsService = null,
        ProfileService? profileService = null)
    {
        _modRepository = modRepository;
        _settingsService = settingsService;
        _profileService = profileService;
        _storageDirectory = storageDirectory;
        _metadata = new ModMetadataStore(storageDirectory, logger);
        _logger = logger;
        _deploymentService = deploymentService;

        // 初始化子服务
        var thumbnailsPath = Path.Combine(storageDirectory, "thumbnails");
        _importService = new ModImportService(_modRepository, storageDirectory, tempDirectory: tempDirectory, settingsService: settingsService);
        _thumbnailService = new ThumbnailService(thumbnailsPath, null);

    // 确保存储目录存在
    Directory.CreateDirectory(_storageDirectory);
    }

    /// <summary>模组库发生增删后触发，供 UI 层联动刷新各页面。</summary>
    public event EventHandler? ModsChanged;
    public event EventHandler? MetadataChanged;

    private void RaiseModsChanged() => ModsChanged?.Invoke(this, EventArgs.Empty);

    #region Mod CRUD Operations

    public async Task<Mod> SetPlatformLinkAsync(Guid id, ModPlatformLink link,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var mod = await _modRepository.GetByIdAsync(id, cancellationToken)
                ?? throw new KeyNotFoundException("模组已不存在，无法保存链接。");
            await LoadSourceAsync(mod, cancellationToken);
            var previous = mod.PlatformLink;
            if (previous?.PlatformId == link.PlatformId && previous.ModId == link.ModId && link.SupportsUpdates)
                link = link with { InstalledVersion = link.InstalledVersion ?? previous.InstalledVersion,
                    InstalledFileVersionId = previous.InstalledFileVersionId, InstalledWebsiteFileId = previous.InstalledWebsiteFileId,
                    AuthorUrl = link.AuthorUrl ?? previous.AuthorUrl };
            ModMetadataStore.Apply(mod, link);
            mod.LastUpdateCheck = null;
            mod.ModifiedAt = DateTime.UtcNow;
            await SaveLinkedModAsync(mod, cancellationToken);
            MetadataChanged?.Invoke(this, EventArgs.Empty);
            return mod;
        }
        finally { _mutationGate.Release(); }
    }

    /// <summary>新内容在独立目录准备；数据库提交成功后才删除旧内容，保留原 ID 和配置关联。</summary>
    public Task<ModReplacementResult> ReplaceFromArchiveAsync(Guid id, string archivePath,
        ModPlatformLink expectedLink, PlatformModFile file, CancellationToken cancellationToken = default,
        IProgress<ModImportProgress>? progress = null, string? installedWebsiteFileId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file.Version);
        return ReplaceArchiveCoreAsync(id, archivePath, expectedLink, file, cancellationToken, progress, installedWebsiteFileId);
    }

    public Task<ModReplacementResult> UpdateFromLocalArchiveAsync(Guid id, string archivePath,
        CancellationToken cancellationToken = default, IProgress<ModImportProgress>? progress = null)
        => ReplaceArchiveCoreAsync(id, archivePath, null, null, cancellationToken, progress, null);

    private async Task<ModReplacementResult> ReplaceArchiveCoreAsync(Guid id, string archivePath,
        ModPlatformLink? expectedLink, PlatformModFile? file, CancellationToken cancellationToken,
        IProgress<ModImportProgress>? progress, string? installedWebsiteFileId, bool forceReplacement = false)
    {
        var prepared = await _importService.PrepareModAsync(archivePath, cancellationToken, progress);
        var committed = false;
        string? newThumbnail = null;
        try
        {
            if ((expectedLink != null || prepared.WorkflowType != Hd2IrIntegrationService.Workflow)
                && !Directory.EnumerateFiles(prepared.Path, "*", SearchOption.AllDirectories).Any(path =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return PatchDeploymentPlanner.TryParseTarget(path, out _, out _, out var suffix) && suffix.Length == 0;
                }))
                throw new InvalidDataException("下载包中没有可用的游戏补丁，已保留原模组。");

            newThumbnail = await _thumbnailService.GenerateThumbnailAsync(prepared.Id, prepared.Path, cancellationToken, prepared.Manifest?.IconPath);
            await _mutationGate.WaitAsync(cancellationToken);
            try
            {
                var original = await _modRepository.GetByIdAsync(id, cancellationToken)
                    ?? throw new KeyNotFoundException("模组已删除，无法更新。");
                await LoadSourceAsync(original, cancellationToken);
                if (expectedLink != null && (original.PlatformId != expectedLink.PlatformId || original.PlatformModId != expectedLink.ModId))
                    throw new InvalidOperationException("模组链接已变更，请重新检查更新。");
                // 修复缺失的 HD2IAS data 时，数据库中的旧哈希不能证明库文件仍然存在。
                if (!forceReplacement && prepared.Hash == original.Hash && prepared.Version == original.Version && file == null)
                    return new(original, false, null);

                var history = new ModVersionStore(_storageDirectory);
                var previousVersions = await history.ReadAsync(id, cancellationToken);
                var keepPrevious = _settingsService?.GetSetting(SettingKeys.KeepModVersions, false) == true;
                var oldSnapshot = ModVersionStore.Clone(original);
                var oldPath = original.Path;
                var oldThumbnail = original.ThumbnailPath;
                var wasDeployed = _deploymentService != null &&
                    (await _deploymentService.GetModDeploymentHistoryAsync(id, cancellationToken)).Any();
                prepared.Id = id;
                var bslRole = await HD2Mimnt.Infrastructure.Analysis.BslModDetector.DetectAsync(prepared, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new(97, "清理旧部署", original.Name, true));
                if (_deploymentService != null)
                    await _deploymentService.DeleteModDeploymentRecordsAsync(id, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                original.Path = prepared.Path;
                original.Version = file?.Version ?? prepared.Version;
                original.Manifest = prepared.Manifest;
                original.WorkflowType = prepared.WorkflowType;
                original.LogicalMod = prepared.LogicalMod;
                original.Size = prepared.Size;
                original.Hash = prepared.Hash;
                original.Category = prepared.Category;
                original.ResourceTypeIds = prepared.ResourceTypeIds;
                original.ContainsScripts = prepared.ContainsScripts;
                original.BslRole = bslRole;
                original.BslDetectionVersion = HD2Mimnt.Infrastructure.Analysis.BslModDetector.DetectionVersion;
                original.ThumbnailPath = newThumbnail ?? prepared.ThumbnailPath;
                original.ModifiedAt = DateTime.UtcNow;
                original.Source = expectedLink?.PlatformId switch
                { "nexusmods" => ModSource.NexusMods, "ayakamods" => ModSource.AyakaMods, _ => original.Source };
                if ((original.PlatformLink ?? expectedLink) is { } sourceLink)
                    original.PlatformLink = sourceLink with
                {
                    InstalledFileVersionId = installedWebsiteFileId == null ? file?.Id : null,
                    InstalledWebsiteFileId = installedWebsiteFileId ?? file?.WebsiteFileId,
                    InstalledVersion = original.Version
                };
                original.Status = ModStatus.Ready;
                progress?.Report(new(99, "保存更新", original.Name, true));
                // 提交阶段不接受半途取消；此处失败时旧目录仍然完整。
                cancellationToken.ThrowIfCancellationRequested();
                if (keepPrevious)
                    await history.WriteAsync(id, previousVersions.Append(new LocalModVersion(Guid.NewGuid(), DateTime.UtcNow, oldSnapshot)).ToList(), CancellationToken.None);
                try { await SaveLinkedModAsync(original, CancellationToken.None); }
                catch
                {
                    if (keepPrevious) await history.WriteAsync(id, previousVersions, CancellationToken.None);
                    throw;
                }
                committed = true;

                string? cleanupWarning = null;
                try
                {
                    if (!keepPrevious && IsManagedModDirectory(oldPath) && Directory.Exists(oldPath)) Directory.Delete(oldPath, true);
                    if (!keepPrevious && newThumbnail != null && oldThumbnail != newThumbnail
                        && !previousVersions.Any(v => v.Content.ThumbnailPath == oldThumbnail))
                        await _thumbnailService.DeleteThumbnailFileAsync(oldThumbnail, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    cleanupWarning = "旧文件清理失败，请查看日志。";
                    _logger?.LogWarning(ex, "模组更新已提交，但旧副本未完全清理: {ModId}", id);
                }
                RaiseModsChanged();
                return new(original, wasDeployed, cleanupWarning);
            }
            finally { _mutationGate.Release(); }
        }
        finally
        {
            if (!committed)
            {
                if (Directory.Exists(prepared.Path)) Directory.Delete(prepared.Path, true);
                if (newThumbnail != null) await _thumbnailService.DeleteThumbnailFileAsync(newThumbnail, CancellationToken.None);
            }
        }
    }

    private bool IsManagedModDirectory(string path) => PathGuard.IsInsideRoot(_storageDirectory, path);

    public async Task<Mod> RecordUpdateCheckAsync(Guid id, ModPlatformLink expectedLink, string? latestVersion,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var mod = await _modRepository.GetByIdAsync(id, cancellationToken)
                ?? throw new KeyNotFoundException("模组已删除。");
            await LoadSourceAsync(mod, cancellationToken);
            if (mod.PlatformId != expectedLink.PlatformId || mod.PlatformModId != expectedLink.ModId)
                throw new InvalidOperationException("模组链接已变更，请重新检查更新。");
            mod.LastUpdateCheck = DateTime.UtcNow;
            mod.PlatformVersion = latestVersion;
            mod.PlatformLink = (mod.PlatformLink ?? expectedLink) with { LatestVersion = latestVersion, RetrievedAt = mod.LastUpdateCheck };
            await SaveLinkedModAsync(mod, cancellationToken);
            MetadataChanged?.Invoke(this, EventArgs.Empty);
            return mod;
        }
        finally { _mutationGate.Release(); }
    }

    public async Task<Mod?> GetModByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var mod = await _modRepository.GetByIdAsync(id, cancellationToken);
        if (mod != null)
        {
            await BackfillMissingManifestAsync(mod, cancellationToken);
        }

        return mod;
    }

    public async Task<IEnumerable<Mod>> GetAllModsAsync(CancellationToken cancellationToken = default)
    {
        var mods = (await _modRepository.GetAllAsync(cancellationToken)).ToList();
        await BackfillMissingManifestsAsync(mods, cancellationToken);
        return mods;
    }

    public async Task<IEnumerable<Mod>> GetModsByIdsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
    {
        var mods = (await _modRepository.GetByIdsAsync(ids, cancellationToken)).ToList();
        await BackfillMissingManifestsAsync(mods, cancellationToken);
        return mods;
    }

    /// <summary>
    /// 回填历史导入遗漏的 manifest：旧版本导入时没有把 manifest 写进库（Mod.Manifest 为 null），
    /// 但模组目录里的 manifest.json 还在。按导入相同的解析规则读取并回填一次，此后不再触发。
    /// 没有它，配置文件页的模组选项功能对这些模组不可见。
    /// </summary>
    private async Task BackfillMissingManifestsAsync(IReadOnlyList<Mod> mods, CancellationToken cancellationToken)
    {
        foreach (var mod in mods)
        {
            await BackfillMissingManifestAsync(mod, cancellationToken);
        }
    }

    private async Task BackfillMissingManifestAsync(Mod mod, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await LoadSourceAsync(mod, cancellationToken);
        if (mod.ResourceTypeIds == null)
        {
            await _importService.PopulateResourceTypesAsync(mod, cancellationToken);
            if (mod.ResourceTypeIds != null) await _modRepository.UpdateAsync(mod, cancellationToken);
        }

        if (string.IsNullOrEmpty(mod.Path) || !Directory.Exists(mod.Path))
        {
            return;
        }

        try
        {
            var changed = false;
            // 生成的分组/选项树只来自管理器自有 JSON；作者 manifest.json 保持原样。
            // HD2IAS 组件包与散装文件夹合集都靠该文件恢复生成选项，不能只按 WorkflowType 判断。
            var options = await Hd2IrIntegrationService.ReadOptionsAsync(mod, cancellationToken);
            if (mod.WorkflowType == Hd2IrIntegrationService.Workflow)
                mod.LogicalMod = await Hd2IrIntegrationService.ReadAsync(mod, cancellationToken);
            if (mod.Manifest == null)
            {
                if (options != null)
                {
                    mod.Manifest = new ModManifest
                    {
                        Name = mod.Name, Version = options.Version ?? mod.Version,
                        Description = options.Description ?? mod.Description, Author = options.Author ?? mod.Author,
                        ManifestVersion = ManifestVersion.V1, IconPath = options.IconPath,
                        Options = options.Options, OptionGroups = options.OptionGroups
                    };
                    changed = true;
                }
                else
                {
                    // 清单缺失时同样应用”子目录推断为互斥选项”的旧版语义。
                    mod.Manifest = await _importService.ReadManifestFromDirectoryAsync(mod.Path, cancellationToken)
                        ?? _importService.InferManifestFromDirectory(mod.Path, mod.Name);
                    changed = mod.Manifest != null;
                }
            }
            else if (options?.OptionGroups is { Count: > 0 } groups && mod.Manifest.OptionGroups is not { Count: > 0 })
            {
                // 旧版本导入的 HD2IAS 条目补上组件分组，使两级选项页可用；选项映射沿用生成结果。
                mod.Manifest.OptionGroups = groups;
                if (options.Options is { Count: > 0 }) mod.Manifest.Options = options.Options;
                changed = true;
            }

            if (!File.Exists(mod.ThumbnailPath)
                && ModImportService.ResolveManifestIconPath(mod.Path, mod.Manifest?.IconPath) is { } image)
            {
                mod.ThumbnailPath = image;
                changed = true;
            }
            if (changed)
            {
                await _modRepository.UpdateAsync(mod, cancellationToken);
                _logger?.LogInformation("已回填历史遗漏的清单或预览图: {ModName}", mod.Name);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "回填 manifest 失败: {ModPath}", mod.Path);
        }
    }

    public async Task<bool> DeleteModAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try { return await DeleteModCoreAsync(id, cancellationToken); }
        finally { _mutationGate.Release(); }
    }

    private async Task<bool> DeleteModCoreAsync(Guid id, CancellationToken cancellationToken)
    {
        var mod = await _modRepository.GetByIdAsync(id, cancellationToken);
        if (mod == null)
        {
            return false;
        }

        // 先解除游戏中的部署，再删除库内源文件；失败时保留模组供用户重试。
        if (_deploymentService != null)
            await _deploymentService.DeleteModDeploymentRecordsAsync(id, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        await DeleteSavedVersionsCoreAsync(id, cancellationToken);

        // 删除物理文件
        if (Directory.Exists(mod.Path))
        {
            try
            {
                Directory.Delete(mod.Path, true);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "删除 Mod 文件失败: {Path}", mod.Path);
            }
        }

        // 删除缩略图
        await _thumbnailService.DeleteThumbnailAsync(id, cancellationToken);
        await _thumbnailService.DeleteThumbnailFileAsync(mod.ThumbnailPath, cancellationToken);

        // 先通过配置服务移除引用并调整分组边界；数据库级联删除不会更新分隔符位置和成员数。
        if (_profileService != null)
        {
            var profiles = await _profileService.GetAllProfilesAsync(cancellationToken);
            foreach (var profile in profiles.Where(profile => profile.Mods.Any(item => item.ModId == id)))
                await _profileService.RemoveModFromProfileAsync(profile.Id, id, cancellationToken);
        }

        // 从数据库删除
        await _modRepository.DeleteAsync(id, cancellationToken);
        await _metadata.WriteAsync(id, null, CancellationToken.None);

        _logger?.LogInformation("Mod 已删除: {ModId}", id);
        RaiseModsChanged();
        return true;
    }

    #endregion

    #region Import Operations

    internal async Task<(Mod Mod, bool Reused)> ImportMigratedModAsync(ExternalMigrationMod external, string sourceIdentity,
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        Mod? prepared = null;
        var saved = false;
        try
        {
            prepared = await _importService.PrepareModAsync(external.SourcePath, cancellationToken, migration: external);
            var id = MigrationIdentity.Create(sourceIdentity, "mod", external.SourceId.ToUpperInvariant(), prepared.Hash);
            var existing = await _modRepository.GetByIdAsync(id, cancellationToken);
            if (existing != null)
            {
                if (!Directory.Exists(existing.Path) || existing.Hash != prepared.Hash)
                    throw new InvalidDataException("已有迁移副本不完整，请先修复或删除该副本后重试。");
                return (existing, true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(_storageDirectory, id.ToString());
            if (!string.IsNullOrWhiteSpace(prepared.ThumbnailPath))
                prepared.ThumbnailPath = Path.Combine(path, Path.GetRelativePath(prepared.Path, prepared.ThumbnailPath));
            Directory.Move(prepared.Path, path);
            prepared.Path = path;
            prepared.Id = id;
            await _modRepository.AddAsync(prepared, cancellationToken);
            saved = true;
            RaiseModsChanged();
            return (prepared, false);
        }
        finally
        {
            try { if (!saved && prepared != null && Directory.Exists(prepared.Path)) Directory.Delete(prepared.Path, true); }
            finally { _mutationGate.Release(); }
        }
    }

    /// <summary>
    /// 从文件夹或 ZIP 导入 Mod
    /// </summary>
    public async Task<Guid> ImportModAsync(string sourcePath, CancellationToken cancellationToken = default,
        IProgress<ModImportProgress>? progress = null, ModPlatformLink? sourceLink = null,
        Func<Hd2IrImportPreview, CancellationToken, Task>? confirmHd2Ir = null)
    {
        var modId = await _importService.ImportModAsync(sourcePath, cancellationToken, progress, sourceLink, confirmHd2Ir);

        // 已落库即视为导入成功；缩略图失败或此时取消不能把成功项统计为失败。
        progress?.Report(new(95, "生成缩略图", "准备模组预览", true));
        try
        {
            var mod = await _modRepository.GetByIdAsync(modId, CancellationToken.None);
            if (mod != null)
            {
                var thumbnailPath = await _thumbnailService.GenerateThumbnailAsync(modId, mod.Path, cancellationToken, mod.Manifest?.IconPath);
                if (thumbnailPath != null)
                {
                    mod.ThumbnailPath = thumbnailPath;
                    await _modRepository.UpdateAsync(mod, CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { _logger?.LogWarning(ex, "模组已导入，但缩略图生成失败: {ModId}", modId); }

        await AddImportedModsToActiveProfileAsync(new[] { modId });
        progress?.Report(new(100, "添加完成", Path.GetFileName(sourcePath)));
        RaiseModsChanged();
        return modId;
    }

    /// <summary>
    /// 批量导入
    /// </summary>
    public async Task<IEnumerable<Guid>> ImportModsAsync(IEnumerable<string> sourcePaths, CancellationToken cancellationToken = default)
    {
        var importedIds = (await _importService.ImportModsAsync(sourcePaths, cancellationToken)).ToArray();
        await AddImportedModsToActiveProfileAsync(importedIds);
        if (importedIds.Any())
        {
            RaiseModsChanged();
        }

        return importedIds;
    }

    private async Task AddImportedModsToActiveProfileAsync(IReadOnlyCollection<Guid> modIds)
    {
        if (modIds.Count == 0 || _profileService == null
            || _settingsService?.GetSetting(SettingKeys.ProfilesAutoAddImportedMods, false) != true) return;
        try
        {
            var profile = await _profileService.GetActiveProfileAsync();
            if (profile != null)
                await _profileService.SetModsEnabledAsync(profile.Id, modIds, true);
        }
        catch (Exception ex)
        {
            RaiseModsChanged();
            throw new InvalidOperationException("模组已保留在模组库，但自动加入当前配置文件失败：" + ex.Message, ex);
        }
    }

    #endregion

    #region Search and Filter

    private async Task LoadSourceAsync(Mod mod, CancellationToken token)
    {
        if (await _metadata.ReadAsync(mod.Id, token) is { } source) ModMetadataStore.Apply(mod, source);
    }

    private async Task SaveLinkedModAsync(Mod mod, CancellationToken token)
    {
        var previous = await _metadata.ReadAsync(mod.Id, token);
        await _metadata.WriteAsync(mod.Id, mod.PlatformLink, token);
        try { await _modRepository.UpdateAsync(mod, token); }
        catch
        {
            await _metadata.WriteAsync(mod.Id, previous, CancellationToken.None);
            throw;
        }
    }

    public async Task<IEnumerable<Mod>> SearchModsAsync(string query, CancellationToken cancellationToken = default)
    {
        var allMods = await _modRepository.GetAllAsync(cancellationToken);
        return allMods.Where(m =>
            m.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            m.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            m.Author.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            m.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<IEnumerable<Mod>> GetModsByCategoryAsync(ModCategory category, CancellationToken cancellationToken = default)
    {
        return await _modRepository.GetByCategoryAsync(category, cancellationToken);
    }

    public async Task<IEnumerable<Mod>> GetModsByStatusAsync(ModStatus status, CancellationToken cancellationToken = default)
    {
        return await _modRepository.GetByStatusAsync(status, cancellationToken);
    }

    public async Task<IEnumerable<Mod>> GetModsByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        var allMods = await _modRepository.GetAllAsync(cancellationToken);
        return allMods.Where(m => m.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
    }

    #endregion

    #region Metadata Operations

    public async Task UpdateModMetadataAsync(
        Guid id,
        string? name = null,
        string? description = null,
        string? author = null,
        string? version = null,
        ModCategory? category = null,
        CancellationToken cancellationToken = default)
    {
        var mod = await _modRepository.GetByIdAsync(id, cancellationToken);
        if (mod == null)
        {
            throw new InvalidOperationException($"Mod not found: {id}");
        }

        if (name != null) mod.Name = name;
        if (description != null) mod.Description = description;
        if (author != null) mod.Author = author;
        if (version != null) mod.Version = version;
        if (category.HasValue) mod.Category = category.Value;

        mod.ModifiedAt = DateTime.UtcNow;
        await _modRepository.UpdateAsync(mod, cancellationToken);
        RaiseModsChanged();
    }

    #endregion

    #region Tag Operations

    public async Task AddTagAsync(Guid modId, string tag, CancellationToken cancellationToken = default)
    {
        var mod = await _modRepository.GetByIdAsync(modId, cancellationToken);
        if (mod == null)
        {
            throw new InvalidOperationException($"Mod not found: {modId}");
        }

        if (!mod.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            mod.Tags.Add(tag);
            mod.ModifiedAt = DateTime.UtcNow;
            await _modRepository.UpdateAsync(mod, cancellationToken);
            RaiseModsChanged();
        }
    }

    public async Task RemoveTagAsync(Guid modId, string tag, CancellationToken cancellationToken = default)
    {
        var mod = await _modRepository.GetByIdAsync(modId, cancellationToken);
        if (mod == null)
        {
            throw new InvalidOperationException($"Mod not found: {modId}");
        }

        var tagToRemove = mod.Tags.FirstOrDefault(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));
        if (tagToRemove != null)
        {
            mod.Tags.Remove(tagToRemove);
            mod.ModifiedAt = DateTime.UtcNow;
            await _modRepository.UpdateAsync(mod, cancellationToken);
            RaiseModsChanged();
        }
    }

    public async Task<IEnumerable<string>> GetAllTagsAsync(CancellationToken cancellationToken = default)
    {
        var allMods = await _modRepository.GetAllAsync(cancellationToken);
        return allMods.SelectMany(m => m.Tags).Distinct().OrderBy(t => t);
    }

    #endregion

    #region Thumbnail Operations

    public async Task<string?> GenerateThumbnailAsync(Guid modId, CancellationToken cancellationToken = default)
    {
        var mod = await _modRepository.GetByIdAsync(modId, cancellationToken);
        if (mod == null)
        {
            return null;
        }

        var thumbnailPath = await _thumbnailService.GenerateThumbnailAsync(modId, mod.Path, cancellationToken, mod.Manifest?.IconPath);
        if (thumbnailPath != null)
        {
            mod.ThumbnailPath = thumbnailPath;
            await _modRepository.UpdateAsync(mod, cancellationToken);
            RaiseModsChanged();
        }

        return thumbnailPath;
    }

    public async Task<string?> SetCustomThumbnailAsync(Guid modId, string imagePath, CancellationToken cancellationToken = default)
    {
        var mod = await _modRepository.GetByIdAsync(modId, cancellationToken);
        if (mod == null)
        {
            return null;
        }

        var thumbnailPath = await _thumbnailService.CreateThumbnailFromImageAsync(modId, imagePath, cancellationToken);
        if (thumbnailPath != null)
        {
            mod.ThumbnailPath = thumbnailPath;
            await _modRepository.UpdateAsync(mod, cancellationToken);
            RaiseModsChanged();
        }

        return thumbnailPath;
    }

    /// <summary>读取卡片所对应的原始图片，放大预览不读取生成的缩略图。</summary>
    public async Task<byte[]?> ReadOriginalPreviewAsync(Guid modId, CancellationToken cancellationToken = default)
    {
        var mod = await _modRepository.GetByIdAsync(modId, cancellationToken);
        if (mod == null) return null;
        return await Task.Run(async () =>
        {
            var path = ModImportService.ResolveCardPreviewPath(mod.Path, mod.Manifest?.IconPath);
            return path == null ? null : await File.ReadAllBytesAsync(path, cancellationToken);
        }, cancellationToken);
    }

    public string? GetThumbnailPath(Guid modId)
    {
        return _thumbnailService.GetThumbnailPath(modId);
    }

    #endregion

    #region Statistics

    public async Task<ModStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        var allMods = await _modRepository.GetAllAsync(cancellationToken);
        var modsList = allMods.ToList();

        return new ModStatistics
        {
            TotalCount = modsList.Count,
            ActiveCount = modsList.Count(m => m.Status == ModStatus.Installed),
            InactiveCount = modsList.Count(m => m.Status == ModStatus.Ready),
            ConflictCount = modsList.Count(m => m.Status == ModStatus.Conflict),
            TotalSize = modsList.Sum(m => m.Size),
            ScriptModCount = modsList.Count(m => m.ContainsScripts),
            TagCount = modsList.SelectMany(m => m.Tags).Distinct().Count(),
            CategoryCounts = modsList.GroupBy(m => m.Category)
                .ToDictionary(g => g.Key, g => g.Count())
        };
    }

    #endregion
}

public class ModStatistics
{
    public int TotalCount { get; set; }
    public int ActiveCount { get; set; }
    public int InactiveCount { get; set; }
    public int ConflictCount { get; set; }
    public long TotalSize { get; set; }
    public int ScriptModCount { get; set; }
    public int TagCount { get; set; }
    public Dictionary<ModCategory, int> CategoryCounts { get; set; } = new();
}
