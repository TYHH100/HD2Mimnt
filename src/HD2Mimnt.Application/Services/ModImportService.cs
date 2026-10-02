using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Text.Json;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Mod Import Service
/// 负责导入 Mod（从文件夹或 ZIP 文件）
/// </summary>
public class ModImportService
{
    private readonly IModRepository _modRepository;
    private readonly string _modsStoragePath;
    private readonly ILogger<ModImportService>? _logger;

    public ModImportService(
        IModRepository modRepository,
        string modsStoragePath,
        ILogger<ModImportService>? logger = null)
    {
        _modRepository = modRepository;
        _modsStoragePath = modsStoragePath;
        _logger = logger;

        Directory.CreateDirectory(_modsStoragePath);
    }

    /// <summary>
    /// 从文件夹或 ZIP 文件导入 Mod
    /// </summary>
    public async Task<Guid> ImportModAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("开始导入 Mod: {SourcePath}", sourcePath);

        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Mod 路径不存在: {sourcePath}");
        }

        string tempExtractPath = Path.Combine(Path.GetTempPath(), $"hd2mimnt_import_{Guid.NewGuid()}");
        string? modRootPath = null;
        bool isZipFile = false;

        try
        {
            // 如果是 ZIP 文件，先解压到临时目录
            if (File.Exists(sourcePath) && Path.GetExtension(sourcePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogInformation("检测到 ZIP 文件，开始解压...");
                isZipFile = true;
                Directory.CreateDirectory(tempExtractPath);
                ZipFile.ExtractToDirectory(sourcePath, tempExtractPath);
                modRootPath = tempExtractPath;
            }
            else
            {
                modRootPath = sourcePath;
            }

            // 读取 manifest
            var manifest = await ReadManifestAsync(modRootPath, cancellationToken);

            // 分析 Mod 内容
            var modInfo = AnalyzeModContent(modRootPath);

            // 创建 Mod 实体
            var mod = new Mod
            {
                Id = Guid.NewGuid(),
                Name = manifest?.Name ?? Path.GetFileName(modRootPath),
                Version = manifest?.Version ?? "1.0.0",
                Author = manifest?.Author ?? "Unknown",
                Description = manifest?.Description ?? "",
                Category = DetermineCategory(manifest, modInfo),
                Status = ModStatus.Ready,
                Tags = manifest?.Tags ?? new List<string>(),
                Size = modInfo.TotalSize,
                Source = isZipFile ? ModSource.LocalZip : ModSource.LocalFolder,
                ContainsScripts = modInfo.ContainsScripts,
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow
            };

            // 复制 Mod 到存储目录
            var targetPath = Path.Combine(_modsStoragePath, mod.Id.ToString());
            Directory.CreateDirectory(targetPath);
            CopyDirectory(modRootPath, targetPath);
            mod.Path = targetPath;

            // 保存到数据库
            await _modRepository.AddAsync(mod, cancellationToken);

            _logger?.LogInformation("Mod 导入成功: {ModName} ({ModId})", mod.Name, mod.Id);
            return mod.Id;
        }
        finally
        {
            // 清理临时目录
            if (isZipFile && Directory.Exists(tempExtractPath))
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
    /// 批量导入 Mod
    /// </summary>
    public async Task<IEnumerable<Guid>> ImportModsAsync(IEnumerable<string> sourcePaths, CancellationToken cancellationToken = default)
    {
        var importedIds = new List<Guid>();

        foreach (var path in sourcePaths)
        {
            try
            {
                var modId = await ImportModAsync(path, cancellationToken);
                importedIds.Add(modId);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "导入 Mod 失败: {Path}", path);
            }
        }

        return importedIds;
    }

    /// <summary>
    /// 读取 Mod manifest 文件
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
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            return JsonSerializer.Deserialize<ModManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "读取 manifest.json 失败: {ManifestPath}", manifestPath);
            return null;
        }
    }

    /// <summary>
    /// 分析 Mod 内容
    /// </summary>
    private ModContentInfo AnalyzeModContent(string modPath)
    {
        var info = new ModContentInfo();

        if (!Directory.Exists(modPath))
        {
            return info;
        }

        var files = Directory.GetFiles(modPath, "*", SearchOption.AllDirectories);
        info.TotalSize = files.Sum(f => new FileInfo(f).Length);

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
    private void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var targetFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, targetFile, true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var targetSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, targetSubDir);
        }
    }

    private class ModContentInfo
    {
        public long TotalSize { get; set; }
        public bool ContainsScripts { get; set; }
        public List<string> FileTypes { get; set; } = new();
    }
}
