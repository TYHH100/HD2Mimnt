using System.Security.Cryptography;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Core.Services;

/// <summary>
/// Mod 导入服务
/// 负责从文件夹或压缩包导入 Mod
/// </summary>
public class ModImportService
{
    private readonly ILogger<ModImportService>? _logger;
    private readonly ManifestParser _manifestParser;

    public ModImportService(ILogger<ModImportService>? logger = null)
    {
        _logger = logger;
        _manifestParser = new ManifestParser(logger as ILogger<ManifestParser>);
    }

    /// <summary>
    /// 从目录导入 Mod
    /// </summary>
    public async Task<ImportResult> ImportFromDirectoryAsync(
        string sourceDirectory,
        string targetDirectory,
        string? customName = null,
        string? customDescription = null)
    {
        var result = new ImportResult();

        try
        {
            if (!Directory.Exists(sourceDirectory))
            {
                result.Success = false;
                result.ErrorMessage = "Source directory does not exist";
                return result;
            }

            _logger?.LogInformation("Importing mod from directory: {Source}", sourceDirectory);

            // 检查是否包含清单文件
            var manifestPath = Path.Combine(sourceDirectory, "mod.json");
            ModManifest manifest;

            if (File.Exists(manifestPath))
            {
                _logger?.LogInformation("Found existing manifest file");
                manifest = _manifestParser.ParseFromFile(manifestPath)
                    ?? _manifestParser.InferFromDirectory(sourceDirectory);
            }
            else
            {
                _logger?.LogInformation("No manifest found, inferring from directory structure");
                manifest = _manifestParser.InferFromDirectory(sourceDirectory);
            }

            // 使用自定义名称和描述（如果提供）
            if (!string.IsNullOrWhiteSpace(customName))
            {
                manifest.Name = customName;
            }

            if (!string.IsNullOrWhiteSpace(customDescription))
            {
                manifest.Description = customDescription;
            }

            // 创建目标目录
            var safeName = SanitizeFileName(manifest.Name);
            var modTargetPath = Path.Combine(targetDirectory, safeName);

            if (Directory.Exists(modTargetPath))
            {
                result.Success = false;
                result.ErrorMessage = $"Mod directory already exists: {safeName}";
                return result;
            }

            Directory.CreateDirectory(modTargetPath);

            // 复制文件
            _logger?.LogInformation("Copying files to: {Target}", modTargetPath);
            await CopyDirectoryAsync(sourceDirectory, modTargetPath);

            // 保存清单文件
            var newManifestPath = Path.Combine(modTargetPath, "mod.json");
            _manifestParser.SaveToFile(manifest, newManifestPath);

            // 计算大小和哈希
            var size = await CalculateDirectorySizeAsync(modTargetPath);
            var hash = await CalculateDirectoryHashAsync(modTargetPath);

            // 检测脚本文件
            var containsScripts = DetectScripts(modTargetPath);

            // 推断分类
            var category = InferCategory(modTargetPath, manifest);

            // 创建 Mod 实体
            var mod = new Mod
            {
                Id = manifest.Guid,
                Name = manifest.Name,
                Version = manifest.Version,
                Author = manifest.Author,
                Description = manifest.Description,
                Path = modTargetPath,
                Category = category,
                Tags = new List<string>(),
                ImportedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
                Size = size,
                Hash = hash,
                Source = ModSource.LocalFolder,
                Status = ModStatus.Ready,
                Manifest = manifest,
                ContainsScripts = containsScripts,
                ThumbnailPath = manifest.IconPath ?? string.Empty
            };

            result.Success = true;
            result.ImportedMod = mod;
            _logger?.LogInformation("Successfully imported mod: {Name}", mod.Name);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to import mod from directory: {Source}", sourceDirectory);
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// 递归复制目录
    /// </summary>
    private async Task CopyDirectoryAsync(string sourceDir, string targetDir)
    {
        var dir = new DirectoryInfo(sourceDir);

        if (!dir.Exists)
        {
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");
        }

        Directory.CreateDirectory(targetDir);

        // 复制文件
        foreach (var file in dir.GetFiles())
        {
            var targetFilePath = Path.Combine(targetDir, file.Name);
            await Task.Run(() => file.CopyTo(targetFilePath, false));
        }

        // 递归复制子目录
        foreach (var subDir in dir.GetDirectories())
        {
            var targetSubDir = Path.Combine(targetDir, subDir.Name);
            await CopyDirectoryAsync(subDir.FullName, targetSubDir);
        }
    }

    /// <summary>
    /// 计算目录总大小
    /// </summary>
    private async Task<long> CalculateDirectorySizeAsync(string directory)
    {
        return await Task.Run(() =>
        {
            var dir = new DirectoryInfo(directory);
            return dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        });
    }

    /// <summary>
    /// 计算目录内容的哈希值（简化版，仅基于文件名和大小）
    /// </summary>
    private async Task<string> CalculateDirectoryHashAsync(string directory)
    {
        return await Task.Run(() =>
        {
            using var md5 = MD5.Create();
            var dir = new DirectoryInfo(directory);
            var files = dir.EnumerateFiles("*", SearchOption.AllDirectories)
                .OrderBy(f => f.FullName)
                .ToList();

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);

            foreach (var file in files)
            {
                writer.Write(file.Name);
                writer.Write(file.Length);
            }

            stream.Position = 0;
            var hash = md5.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        });
    }

    /// <summary>
    /// 检测是否包含脚本文件
    /// </summary>
    private bool DetectScripts(string directory)
    {
        var scriptExtensions = new[] { ".lua", ".py", ".js", ".cs", ".vbs", ".bat", ".ps1", ".sh" };
        var dir = new DirectoryInfo(directory);

        return dir.EnumerateFiles("*", SearchOption.AllDirectories)
            .Any(file => scriptExtensions.Contains(file.Extension.ToLowerInvariant()));
    }

    /// <summary>
    /// 推断 Mod 分类
    /// </summary>
    private ModCategory InferCategory(string directory, ModManifest manifest)
    {
        var dirName = Path.GetFileName(directory).ToLowerInvariant();
        var manifestName = manifest.Name.ToLowerInvariant();

        if (dirName.Contains("armor") || manifestName.Contains("armor"))
            return ModCategory.Armor;
        if (dirName.Contains("weapon") || manifestName.Contains("weapon"))
            return ModCategory.Weapon;
        if (dirName.Contains("character") || manifestName.Contains("character"))
            return ModCategory.Character;
        if (dirName.Contains("audio") || dirName.Contains("sound") || manifestName.Contains("audio"))
            return ModCategory.Audio;
        if (dirName.Contains("ui") || manifestName.Contains("ui"))
            return ModCategory.UI;
        if (dirName.Contains("gameplay") || manifestName.Contains("gameplay"))
            return ModCategory.Gameplay;

        // 检查文件类型
        var dir = new DirectoryInfo(directory);
        var files = dir.EnumerateFiles("*", SearchOption.AllDirectories).ToList();

        var hasTextures = files.Any(f => f.Extension.Equals(".dds", StringComparison.OrdinalIgnoreCase) ||
                                          f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase));
        var hasMeshes = files.Any(f => f.Extension.Equals(".mesh", StringComparison.OrdinalIgnoreCase) ||
                                        f.Extension.Equals(".fbx", StringComparison.OrdinalIgnoreCase));
        var hasAudio = files.Any(f => f.Extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
                                       f.Extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase));

        if (hasTextures && hasMeshes)
            return ModCategory.Visual;
        if (hasAudio)
            return ModCategory.Audio;
        if (hasTextures)
            return ModCategory.Visual;

        return ModCategory.Unknown;
    }

    /// <summary>
    /// 清理文件名中的非法字符
    /// </summary>
    private string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", fileName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? "UnnamedMod" : sanitized;
    }
}

/// <summary>
/// 导入结果
/// </summary>
public class ImportResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public Mod? ImportedMod { get; set; }
}
