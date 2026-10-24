using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Archive import service for handling nested archives and HD2IAS content
/// </summary>
public class ArchiveImportService
{
    private readonly IArchiveInspector _archiveInspector;
    private readonly IComponentDetector _componentDetector;
    private readonly IModMerger _modMerger;
    private readonly ILogicalModRepository _logicalModRepository;
    private readonly IArchiveAnalysisCacheRepository _cacheRepository;
    private readonly ILogger<ArchiveImportService>? _logger;
    private readonly ArchiveImportConfiguration _configuration;

    public ArchiveImportService(
        IArchiveInspector archiveInspector,
        IComponentDetector componentDetector,
        IModMerger modMerger,
        ILogicalModRepository logicalModRepository,
        IArchiveAnalysisCacheRepository cacheRepository,
        ILogger<ArchiveImportService>? logger = null)
    {
        _archiveInspector = archiveInspector;
        _componentDetector = componentDetector;
        _modMerger = modMerger;
        _logicalModRepository = logicalModRepository;
        _cacheRepository = cacheRepository;
        _logger = logger;
        _configuration = new ArchiveImportConfiguration();
    }

    /// <summary>
    /// Imports an archive file and creates logical mods
    /// </summary>
    public async Task<ArchiveImportResult> ImportArchiveAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("[Import] 开始导入: {ArchivePath}", archivePath);

        var result = new ArchiveImportResult
        {
            ArchivePath = archivePath,
            StartTime = DateTime.UtcNow
        };

        try
        {
            // Check cache first
            var fileInfo = new FileInfo(archivePath);
            var cachedAnalysis = await TryGetCachedAnalysisAsync(
                archivePath,
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc,
                cancellationToken);

            ArchiveTree tree;
            if (cachedAnalysis != null)
            {
                _logger?.LogInformation("[Import] 使用缓存的分析结果");
                tree = JsonSerializer.Deserialize<ArchiveTree>(cachedAnalysis.ArchiveTreeJson)!;
                result.UsedCache = true;
            }
            else
            {
                // Analyze archive
                _logger?.LogInformation("[Analyze] 开始分析归档结构");
                tree = await _archiveInspector.InspectAsync(
                    archivePath,
                    _configuration.MaxArchiveInspectionDepth,
                    cancellationToken);

                result.ArchiveTree = tree;

                // Cache the analysis
                if (_configuration.EnableAnalysisCache)
                {
                    await CacheAnalysisAsync(archivePath, fileInfo, tree, cancellationToken);
                }
            }

            // Extract component candidates
            _logger?.LogInformation("[Detect] 检测到 {Count} 个组件候选", tree.ComponentCandidates.Count);
            result.ComponentCandidates = tree.ComponentCandidates;

            if (tree.ComponentCandidates.Count == 0)
            {
                result.Success = false;
                result.ErrorMessage = "未检测到有效的组件";
                _logger?.LogWarning("[Import] 未检测到有效的组件");
                return result;
            }

            // Merge components into logical mods
            _logger?.LogInformation("[Merge] 开始合并组件");
            var mergeResult = await _modMerger.MergeAsync(tree.ComponentCandidates, cancellationToken);
            result.MergeResult = mergeResult;

            // Check if user confirmation is needed
            if (mergeResult.RequiredConfirmation && _configuration.PromptForLowConfidenceMerge)
            {
                result.RequiresUserConfirmation = true;
                result.Success = false;
                _logger?.LogInformation("[Merge] 需要用户确认合并决策");
                return result;
            }

            // Save logical mods to repository
            _logger?.LogInformation("[Import] 保存 {Count} 个逻辑模组", mergeResult.LogicalMods.Count);
            foreach (var logicalMod in mergeResult.LogicalMods)
            {
                await _logicalModRepository.AddAsync(logicalMod, cancellationToken);
                result.ImportedLogicalMods.Add(logicalMod);
            }

            result.Success = true;
            result.EndTime = DateTime.UtcNow;

            _logger?.LogInformation("[Import] 导入完成: {Count} 个逻辑模组, 耗时 {Duration}ms",
                result.ImportedLogicalMods.Count,
                (result.EndTime - result.StartTime).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.EndTime = DateTime.UtcNow;
            _logger?.LogError(ex, "[Import] 导入失败: {ArchivePath}", archivePath);
        }

        return result;
    }

    /// <summary>
    /// Confirms and completes a pending merge that required user confirmation
    /// </summary>
    public async Task<ArchiveImportResult> ConfirmMergeAsync(
        ArchiveImportResult pendingResult,
        bool autoMerge,
        CancellationToken cancellationToken = default)
    {
        if (pendingResult.MergeResult == null)
        {
            throw new InvalidOperationException("No merge result available");
        }

        _logger?.LogInformation("[Merge] 用户确认合并: {AutoMerge}", autoMerge);

        if (!autoMerge)
        {
            // User chose separate import
            // Recreate logical mods with separate strategy
            var candidates = pendingResult.ComponentCandidates;
            var separateMods = new List<LogicalMod>();

            foreach (var candidate in candidates)
            {
                var logicalMod = new LogicalMod
                {
                    Name = candidate.Name,
                    Components = new List<LogicalModComponent>
                    {
                        new LogicalModComponent
                        {
                            ComponentId = candidate.ComponentType.ToString().ToLowerInvariant(),
                            Type = candidate.ComponentType,
                            Name = candidate.Name,
                            Size = candidate.Size
                        }
                    }
                };

                separateMods.Add(logicalMod);
            }

            pendingResult.MergeResult.LogicalMods.Clear();
            pendingResult.MergeResult.LogicalMods.AddRange(separateMods);
        }

        // Save the logical mods
        foreach (var logicalMod in pendingResult.MergeResult.LogicalMods)
        {
            await _logicalModRepository.AddAsync(logicalMod, cancellationToken);
            pendingResult.ImportedLogicalMods.Add(logicalMod);
        }

        pendingResult.Success = true;
        pendingResult.RequiresUserConfirmation = false;
        pendingResult.EndTime = DateTime.UtcNow;

        return pendingResult;
    }

    /// <summary>
    /// Clears expired cache entries
    /// </summary>
    public async Task ClearExpiredCacheAsync(CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("清理过期缓存");
        await _cacheRepository.DeleteExpiredAsync(_configuration.CacheExpirationDays, cancellationToken);
    }

    private async Task<ArchiveAnalysisCache?> TryGetCachedAnalysisAsync(
        string filePath,
        long fileSize,
        DateTime lastModified,
        CancellationToken cancellationToken)
    {
        if (!_configuration.EnableAnalysisCache)
        {
            return null;
        }

        var cache = await _cacheRepository.GetAsync(filePath, fileSize, lastModified, cancellationToken);
        if (cache != null)
        {
            await _cacheRepository.UpdateAccessAsync(cache.Id, cancellationToken);
        }

        return cache;
    }

    private async Task CacheAnalysisAsync(
        string filePath,
        FileInfo fileInfo,
        ArchiveTree tree,
        CancellationToken cancellationToken)
    {
        try
        {
            var cache = new ArchiveAnalysisCache
            {
                FilePath = filePath,
                FileSize = fileInfo.Length,
                LastModified = fileInfo.LastWriteTimeUtc,
                ContentHash = CalculateFastHash(filePath),
                ArchiveTreeJson = JsonSerializer.Serialize(tree),
                DetectionResultsJson = JsonSerializer.Serialize(tree.ComponentCandidates)
            };

            await _cacheRepository.SaveAsync(cache, cancellationToken);
            _logger?.LogDebug("已缓存分析结果: {FilePath}", filePath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "缓存分析结果失败: {FilePath}", filePath);
        }
    }

    private string CalculateFastHash(string filePath)
    {
        // Fast fingerprint based on file size and first/last bytes
        using var stream = File.OpenRead(filePath);
        var buffer = new byte[(int)Math.Min(1024, stream.Length)];
        stream.ReadExactly(buffer);

        if (stream.Length > 1024)
        {
            stream.Seek(-1024, SeekOrigin.End);
            stream.ReadExactly(buffer);
        }

        return Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(buffer));
    }
}

/// <summary>
/// Result of archive import operation
/// </summary>
public class ArchiveImportResult
{
    public bool Success { get; set; }
    public string ArchivePath { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public bool UsedCache { get; set; }
    public bool RequiresUserConfirmation { get; set; }
    public ArchiveTree? ArchiveTree { get; set; }
    public List<ComponentCandidate> ComponentCandidates { get; set; } = new();
    public MergeResult? MergeResult { get; set; }
    public List<LogicalMod> ImportedLogicalMods { get; set; } = new();

    public TimeSpan Duration => EndTime - StartTime;
}
