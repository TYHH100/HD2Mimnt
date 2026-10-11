using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Mod 管理服务
/// 协调 Mod 的导入、删除、搜索等操作
/// </summary>
public class ModService
{
    private readonly IModRepository _modRepository;
    private readonly ModImportService _importService;
    private readonly ThumbnailService _thumbnailService;
    private readonly ILogger<ModService>? _logger;
    private readonly DeploymentService? _deploymentService;
    private readonly string _storageDirectory;
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
            return hash;
        }
        finally { _hashGate.Release(); }
    }

    public ModService(
        IModRepository modRepository,
        string storageDirectory,
        ILogger<ModService>? logger = null,
        DeploymentService? deploymentService = null)
    {
        _modRepository = modRepository;
        _storageDirectory = storageDirectory;
        _logger = logger;
        _deploymentService = deploymentService;

        // 初始化子服务
        var thumbnailsPath = Path.Combine(storageDirectory, "thumbnails");
        _importService = new ModImportService(_modRepository, storageDirectory, null);
        _thumbnailService = new ThumbnailService(thumbnailsPath, null);

    // 确保存储目录存在
    Directory.CreateDirectory(_storageDirectory);
    }

    /// <summary>模组库发生增删后触发，供 UI 层联动刷新各页面。</summary>
    public event EventHandler? ModsChanged;

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
            mod.PlatformLink = link;
            mod.PlatformId = link.PlatformId;
            mod.PlatformModId = link.ModId;
            mod.PlatformVersion = link.LatestVersion;
            mod.NexusModId = link.PlatformId == "nexusmods" ? int.Parse(link.ModId) : null;
            mod.LastUpdateCheck = null;
            mod.ModifiedAt = DateTime.UtcNow;
            await _modRepository.UpdateAsync(mod, cancellationToken);
            return mod;
        }
        finally { _mutationGate.Release(); }
    }

    /// <summary>新内容在独立目录准备；数据库提交成功后才删除旧内容，保留原 ID 和配置关联。</summary>
    public async Task<ModReplacementResult> ReplaceFromArchiveAsync(Guid id, string archivePath,
        ModPlatformLink expectedLink, PlatformModFile file, CancellationToken cancellationToken = default,
        IProgress<ModImportProgress>? progress = null, string? installedWebsiteFileId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file.Version);
        var prepared = await _importService.PrepareModAsync(archivePath, cancellationToken, progress);
        var committed = false;
        string? newThumbnail = null;
        try
        {
            if (!Directory.EnumerateFiles(prepared.Path, "*", SearchOption.AllDirectories).Any(path =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return PatchDeploymentPlanner.TryParseTarget(path, out _, out _, out var suffix) && suffix.Length == 0;
                }))
                throw new InvalidDataException("下载包中没有可用的游戏补丁，已保留原模组。");

            newThumbnail = await _thumbnailService.GenerateThumbnailAsync(prepared.Id, prepared.Path, cancellationToken);
            await _mutationGate.WaitAsync(cancellationToken);
            try
            {
                var original = await _modRepository.GetByIdAsync(id, cancellationToken)
                    ?? throw new KeyNotFoundException("模组已删除，无法更新。");
                if (original.PlatformId != expectedLink.PlatformId || original.PlatformModId != expectedLink.ModId)
                    throw new InvalidOperationException("模组链接已变更，请重新检查更新。");

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
                original.Version = file.Version;
                original.Manifest = prepared.Manifest;
                original.Size = prepared.Size;
                original.Hash = prepared.Hash;
                original.Category = prepared.Category;
                original.ResourceTypeIds = prepared.ResourceTypeIds;
                original.ContainsScripts = prepared.ContainsScripts;
                original.BslRole = bslRole;
                original.BslDetectionVersion = HD2Mimnt.Infrastructure.Analysis.BslModDetector.DetectionVersion;
                original.ThumbnailPath = newThumbnail ?? oldThumbnail;
                original.ModifiedAt = DateTime.UtcNow;
                original.Source = ModSource.NexusMods;
                original.PlatformLink = (original.PlatformLink ?? expectedLink) with
                {
                    InstalledFileVersionId = installedWebsiteFileId == null ? file.Id : null,
                    InstalledWebsiteFileId = installedWebsiteFileId
                };
                original.Status = ModStatus.Ready;
                progress?.Report(new(99, "保存更新", original.Name, true));
                // 提交阶段不接受半途取消；此处失败时旧目录仍然完整。
                cancellationToken.ThrowIfCancellationRequested();
                await _modRepository.UpdateAsync(original, CancellationToken.None);
                committed = true;

                string? cleanupWarning = null;
                try
                {
                    if (IsManagedModDirectory(oldPath) && Directory.Exists(oldPath)) Directory.Delete(oldPath, true);
                    if (newThumbnail != null && oldThumbnail != newThumbnail)
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

    private bool IsManagedModDirectory(string path)
        => Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_storageDirectory))
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public async Task<Mod> RecordUpdateCheckAsync(Guid id, ModPlatformLink expectedLink, string? latestVersion,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var mod = await _modRepository.GetByIdAsync(id, cancellationToken)
                ?? throw new KeyNotFoundException("模组已删除。");
            if (mod.PlatformId != expectedLink.PlatformId || mod.PlatformModId != expectedLink.ModId)
                throw new InvalidOperationException("模组链接已变更，请重新检查更新。");
            mod.LastUpdateCheck = DateTime.UtcNow;
            mod.PlatformVersion = latestVersion;
            mod.PlatformLink = (mod.PlatformLink ?? expectedLink) with { LatestVersion = latestVersion, RetrievedAt = mod.LastUpdateCheck };
            await _modRepository.UpdateAsync(mod, cancellationToken);
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
        if (mod.ResourceTypeIds == null)
        {
            await _importService.PopulateResourceTypesAsync(mod, cancellationToken);
            if (mod.ResourceTypeIds != null) await _modRepository.UpdateAsync(mod, cancellationToken);
        }

        if (mod.Manifest != null || string.IsNullOrEmpty(mod.Path) || !Directory.Exists(mod.Path))
        {
            return;
        }

        try
        {
            // 清单缺失时同样应用“子目录推断为互斥选项”的旧版语义，保证历史模组也能用选项
            var manifest = await _importService.ReadManifestFromDirectoryAsync(mod.Path, cancellationToken)
                ?? ModImportService.InferManifestFromDirectory(mod.Path, mod.Name);
            if (manifest == null)
            {
                return;
            }

            mod.Manifest = manifest;
            await _modRepository.UpdateAsync(mod, cancellationToken);
            _logger?.LogInformation("已回填历史遗漏的 manifest: {ModName}", mod.Name);
        }
        catch (Exception ex)
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

        // 从数据库删除
        await _modRepository.DeleteAsync(id, cancellationToken);

        _logger?.LogInformation("Mod 已删除: {ModId}", id);
        RaiseModsChanged();
        return true;
    }

    #endregion

    #region Import Operations

    /// <summary>
    /// 从文件夹或 ZIP 导入 Mod
    /// </summary>
    public async Task<Guid> ImportModAsync(string sourcePath, CancellationToken cancellationToken = default,
        IProgress<ModImportProgress>? progress = null)
    {
        var modId = await _importService.ImportModAsync(sourcePath, cancellationToken, progress);

        // 已落库即视为导入成功；缩略图失败或此时取消不能把成功项统计为失败。
        progress?.Report(new(95, "生成缩略图", "准备模组预览", true));
        try
        {
            var mod = await _modRepository.GetByIdAsync(modId, CancellationToken.None);
            if (mod != null)
            {
                var thumbnailPath = await _thumbnailService.GenerateThumbnailAsync(modId, mod.Path, cancellationToken);
                if (thumbnailPath != null)
                {
                    mod.ThumbnailPath = thumbnailPath;
                    await _modRepository.UpdateAsync(mod, CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { _logger?.LogWarning(ex, "模组已导入，但缩略图生成失败: {ModId}", modId); }

        progress?.Report(new(100, "添加完成", Path.GetFileName(sourcePath)));
        RaiseModsChanged();
        return modId;
    }

    /// <summary>
    /// 批量导入
    /// </summary>
    public async Task<IEnumerable<Guid>> ImportModsAsync(IEnumerable<string> sourcePaths, CancellationToken cancellationToken = default)
    {
        var importedIds = await _importService.ImportModsAsync(sourcePaths, cancellationToken);
        if (importedIds.Any())
        {
            RaiseModsChanged();
        }

        return importedIds;
    }

    #endregion

    #region Search and Filter

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

        var thumbnailPath = await _thumbnailService.GenerateThumbnailAsync(modId, mod.Path, cancellationToken);
        if (thumbnailPath != null)
        {
            mod.ThumbnailPath = thumbnailPath;
            await _modRepository.UpdateAsync(mod, cancellationToken);
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
        }

        return thumbnailPath;
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
