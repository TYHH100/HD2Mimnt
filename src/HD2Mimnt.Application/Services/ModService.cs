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
    private readonly string _storageDirectory;

    public ModService(
        IModRepository modRepository,
        string storageDirectory,
        ILogger<ModService>? logger = null)
    {
        _modRepository = modRepository;
        _storageDirectory = storageDirectory;
        _logger = logger;

        // 初始化子服务
        var thumbnailsPath = Path.Combine(storageDirectory, "thumbnails");
        _importService = new ModImportService(_modRepository, storageDirectory, null);
        _thumbnailService = new ThumbnailService(thumbnailsPath, null);

        // 确保存储目录存在
        Directory.CreateDirectory(_storageDirectory);
    }

    #region Mod CRUD Operations

    public async Task<Mod?> GetModByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _modRepository.GetByIdAsync(id, cancellationToken);
    }

    public async Task<IEnumerable<Mod>> GetAllModsAsync(CancellationToken cancellationToken = default)
    {
        return await _modRepository.GetAllAsync(cancellationToken);
    }

    public async Task<bool> DeleteModAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var mod = await _modRepository.GetByIdAsync(id, cancellationToken);
        if (mod == null)
        {
            return false;
        }

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

        // 从数据库删除
        await _modRepository.DeleteAsync(id, cancellationToken);

        _logger?.LogInformation("Mod 已删除: {ModId}", id);
        return true;
    }

    #endregion

    #region Import Operations

    /// <summary>
    /// 从文件夹或 ZIP 导入 Mod
    /// </summary>
    public async Task<Guid> ImportModAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var modId = await _importService.ImportModAsync(sourcePath, cancellationToken);

        // 生成缩略图
        var mod = await _modRepository.GetByIdAsync(modId, cancellationToken);
        if (mod != null)
        {
            var thumbnailPath = await _thumbnailService.GenerateThumbnailAsync(modId, mod.Path, cancellationToken);
            if (thumbnailPath != null)
            {
                mod.ThumbnailPath = thumbnailPath;
                await _modRepository.UpdateAsync(mod, cancellationToken);
            }
        }

        return modId;
    }

    /// <summary>
    /// 批量导入
    /// </summary>
    public async Task<IEnumerable<Guid>> ImportModsAsync(IEnumerable<string> sourcePaths, CancellationToken cancellationToken = default)
    {
        return await _importService.ImportModsAsync(sourcePaths, cancellationToken);
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
