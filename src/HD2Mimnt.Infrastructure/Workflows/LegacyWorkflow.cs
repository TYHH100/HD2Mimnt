using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Infrastructure.Workflows;

/// <summary>
/// Legacy Workflow 实现
/// 处理 Legacy 格式的 Mod（互斥下拉选项格式）
/// </summary>
public class LegacyWorkflow : IWorkflow
{
    public string Name => "Legacy";

    public string Description => "Legacy format Mod with mutually exclusive dropdown options";

    /// <summary>
    /// 检测 Mod 是否符合 Legacy 格式
    /// </summary>
    public async Task<bool> CanHandleAsync(string modPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modPath) || !Directory.Exists(modPath))
            return false;

        // 查找 manifest.json 或 mod.json
        var manifestFiles = new[] { "manifest.json", "mod.json" };

        foreach (var manifestFile in manifestFiles)
        {
            var manifestPath = Path.Combine(modPath, manifestFile);
            if (File.Exists(manifestPath))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                    var doc = JsonDocument.Parse(content);

                    // 检查是否有 "legacy_options" 或 "options" 字段（字符串数组）
                    if (doc.RootElement.TryGetProperty("legacy_options", out var legacyOptions) &&
                        legacyOptions.ValueKind == JsonValueKind.Array)
                    {
                        return true;
                    }

                    // 检查是否有 "manifest_version" 字段为 0 或不存在
                    if (doc.RootElement.TryGetProperty("manifest_version", out var version))
                    {
                        if (version.ValueKind == JsonValueKind.Number && version.GetInt32() == 0)
                        {
                            return true;
                        }
                    }
                    else
                    {
                        // 没有 manifest_version 字段，默认为 Legacy
                        return true;
                    }
                }
                catch
                {
                    // JSON 解析失败，继续检查下一个文件
                    continue;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 解析 Legacy Manifest
    /// </summary>
    public async Task<ModManifest?> ParseManifestAsync(string modPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modPath) || !Directory.Exists(modPath))
            return null;

        var manifestFiles = new[] { "manifest.json", "mod.json" };

        foreach (var manifestFile in manifestFiles)
        {
            var manifestPath = Path.Combine(modPath, manifestFile);
            if (File.Exists(manifestPath))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                    var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;

                    var manifest = new ModManifest
                    {
                        ManifestVersion = ManifestVersion.Legacy
                    };

                    // 解析基本字段
                    if (root.TryGetProperty("guid", out var guid))
                    {
                        manifest.Guid = Guid.TryParse(guid.GetString(), out var parsedGuid) ? parsedGuid : Guid.NewGuid();
                    }
                    else
                    {
                        manifest.Guid = Guid.NewGuid();
                    }

                    if (root.TryGetProperty("name", out var name))
                        manifest.Name = name.GetString() ?? string.Empty;

                    if (root.TryGetProperty("description", out var description))
                        manifest.Description = description.GetString() ?? string.Empty;

                    if (root.TryGetProperty("version", out var version))
                        manifest.Version = version.GetString() ?? "1.0.0";

                    if (root.TryGetProperty("author", out var author))
                        manifest.Author = author.GetString() ?? string.Empty;

                    if (root.TryGetProperty("icon", out var icon))
                        manifest.IconPath = icon.GetString();

                    if (root.TryGetProperty("category", out var category))
                        manifest.Category = category.GetString();

                    // 解析 Tags
                    if (root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tag in tags.EnumerateArray())
                        {
                            var tagValue = tag.GetString();
                            if (!string.IsNullOrWhiteSpace(tagValue))
                            {
                                manifest.Tags.Add(tagValue);
                            }
                        }
                    }

                    // 解析 Legacy Options（互斥下拉选项）
                    if (root.TryGetProperty("legacy_options", out var legacyOptions) &&
                        legacyOptions.ValueKind == JsonValueKind.Array)
                    {
                        manifest.LegacyOptions = new List<string>();
                        foreach (var option in legacyOptions.EnumerateArray())
                        {
                            var optionValue = option.GetString();
                            if (!string.IsNullOrWhiteSpace(optionValue))
                            {
                                manifest.LegacyOptions.Add(optionValue);
                            }
                        }
                    }

                    // 解析 Nexus Data
                    if (root.TryGetProperty("nexus", out var nexus))
                    {
                        manifest.NexusData = new NexusData();

                        if (nexus.TryGetProperty("mod_id", out var modId))
                            manifest.NexusData.ModId = modId.GetInt32();

                        if (nexus.TryGetProperty("version", out var nexusVersion))
                            manifest.NexusData.Version = nexusVersion.GetString() ?? string.Empty;
                    }

                    return manifest;
                }
                catch
                {
                    // JSON 解析失败，继续检查下一个文件
                    continue;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 验证 Legacy Mod 结构
    /// </summary>
    public async Task<WorkflowValidationResult> ValidateAsync(string modPath, CancellationToken cancellationToken = default)
    {
        var result = new WorkflowValidationResult { IsValid = true };

        if (string.IsNullOrWhiteSpace(modPath))
        {
            result.IsValid = false;
            result.Errors.Add("Mod path is null or empty");
            return result;
        }

        if (!Directory.Exists(modPath))
        {
            result.IsValid = false;
            result.Errors.Add($"Mod directory does not exist: {modPath}");
            return result;
        }

        // 验证 Manifest 是否存在
        var manifestFiles = new[] { "manifest.json", "mod.json" };
        var hasManifest = manifestFiles.Any(f => File.Exists(Path.Combine(modPath, f)));

        if (!hasManifest)
        {
            result.Warnings.Add("No manifest file found (manifest.json or mod.json)");
        }

        // 验证是否有文件
        var files = Directory.GetFiles(modPath, "*.*", SearchOption.AllDirectories);
        if (files.Length == 0)
        {
            result.IsValid = false;
            result.Errors.Add("Mod directory is empty");
            return result;
        }

        result.Metadata["FileCount"] = files.Length;

        // 尝试解析 Manifest
        if (hasManifest)
        {
            var manifest = await ParseManifestAsync(modPath, cancellationToken);
            if (manifest == null)
            {
                result.Warnings.Add("Failed to parse manifest file");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(manifest.Name))
                    result.Warnings.Add("Manifest: Name is empty");

                if (string.IsNullOrWhiteSpace(manifest.Version))
                    result.Warnings.Add("Manifest: Version is empty");

                if (manifest.LegacyOptions != null && manifest.LegacyOptions.Count > 0)
                {
                    result.Metadata["LegacyOptionsCount"] = manifest.LegacyOptions.Count;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 获取部署文件列表
    /// Legacy Mod 直接部署所有文件（除了 manifest.json）
    /// </summary>
    public Task<List<WorkflowDeploymentFile>> GetDeploymentFilesAsync(
        string modPath,
        ModManifest? manifest = null,
        CancellationToken cancellationToken = default)
    {
        var deploymentFiles = new List<WorkflowDeploymentFile>();

        if (string.IsNullOrWhiteSpace(modPath) || !Directory.Exists(modPath))
            return Task.FromResult(deploymentFiles);

        // 获取所有文件
        var files = Directory.GetFiles(modPath, "*.*", SearchOption.AllDirectories);

        // 排除 manifest 文件
        var excludedFiles = new[] { "manifest.json", "mod.json", "README.md", "readme.txt" };

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);

            // 跳过 manifest 和 readme 文件
            if (excludedFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                continue;

            var relativePath = Path.GetRelativePath(modPath, file);

            deploymentFiles.Add(new WorkflowDeploymentFile
            {
                SourcePath = file,
                TargetRelativePath = relativePath,
                Priority = 0 // Legacy Mod 默认优先级为 0
            });
        }

        return Task.FromResult(deploymentFiles);
    }

    /// <summary>
    /// 获取清理文件列表
    /// </summary>
    public Task<List<string>> GetCleanupFilesAsync(
        string modPath,
        ModManifest? manifest = null,
        CancellationToken cancellationToken = default)
    {
        var cleanupFiles = new List<string>();

        if (string.IsNullOrWhiteSpace(modPath) || !Directory.Exists(modPath))
            return Task.FromResult(cleanupFiles);

        // Legacy Mod 清理所有已部署的文件
        var files = Directory.GetFiles(modPath, "*.*", SearchOption.AllDirectories);
        var excludedFiles = new[] { "manifest.json", "mod.json", "README.md", "readme.txt" };

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);

            if (!excludedFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            {
                var relativePath = Path.GetRelativePath(modPath, file);
                cleanupFiles.Add(relativePath);
            }
        }

        return Task.FromResult(cleanupFiles);
    }
}
