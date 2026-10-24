using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Services;

namespace HD2Mimnt.Infrastructure.Workflows;

/// <summary>
/// V1 Workflow 实现
/// 处理 V1 格式的 Mod（独立启用选项 + 子选项）
/// 支持 .patch_0, .gpu_resources, .stream 文件
/// </summary>
public class V1Workflow : IWorkflow
{
    public string Name => "V1";

    public string Description => "V1 format Mod with independent options and .patch_0/.gpu_resources/.stream support";

    private static readonly string[] V1Extensions = { ".patch_0", ".gpu_resources", ".stream" };

    /// <summary>
    /// 检测 Mod 是否符合 V1 格式
    /// </summary>
    public async Task<bool> CanHandleAsync(string modPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modPath) || !Directory.Exists(modPath))
            return false;

        // 方法 1: 检查 manifest_version 字段
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

                    // 检查 manifest_version 是否为 1
                    if (doc.RootElement.TryGetProperty("manifest_version", out var version))
                    {
                        if (version.ValueKind == JsonValueKind.Number && version.GetInt32() == 1)
                        {
                            return true;
                        }
                    }

                    // 检查是否有 "options" 字段（对象数组，V1 格式）
                    if (doc.RootElement.TryGetProperty("options", out var options) &&
                        options.ValueKind == JsonValueKind.Array)
                    {
                        // 检查第一个选项是否是对象（V1 格式）
                        foreach (var option in options.EnumerateArray())
                        {
                            if (option.ValueKind == JsonValueKind.Object)
                            {
                                return true;
                            }
                            break;
                        }
                    }
                }
                catch
                {
                    // JSON 解析失败，继续检查下一个方法
                    continue;
                }
            }
        }

        // 方法 2: 检查是否有 V1 特有的文件扩展名
        var files = Directory.GetFiles(modPath, "*.*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (V1Extensions.Contains(extension))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 解析 V1 Manifest
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

                    // 标准 V1 包与独立扩展文件共用导入解析；保留下方旧 snake_case 格式兼容。
                    if (root.TryGetProperty("Version", out var format) && format.ValueKind == JsonValueKind.Number)
                        return await new ManifestParser().ParseFromFileAsync(manifestPath, cancellationToken);

                    var manifest = new ModManifest
                    {
                        ManifestVersion = ManifestVersion.V1
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

                    // 解析 V1 Options（独立启用选项 + 子选项）
                    if (root.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
                    {
                        manifest.Options = new List<ModOption>();

                        foreach (var option in options.EnumerateArray())
                        {
                            if (option.ValueKind != JsonValueKind.Object)
                                continue;

                            var modOption = new ModOption();

                            if (option.TryGetProperty("name", out var optionName))
                                modOption.Name = optionName.GetString() ?? string.Empty;

                            if (option.TryGetProperty("description", out var optionDesc))
                                modOption.Description = optionDesc.GetString() ?? string.Empty;

                            if (option.TryGetProperty("default_enabled", out var defaultEnabled))
                                modOption.DefaultEnabled = defaultEnabled.GetBoolean();

                            // 解析子选项（互斥选择）
                            if (option.TryGetProperty("sub_options", out var subOptions) &&
                                subOptions.ValueKind == JsonValueKind.Array)
                            {
                                modOption.SubOptions = new List<ModSubOption>();

                                foreach (var subOption in subOptions.EnumerateArray())
                                {
                                    if (subOption.ValueKind != JsonValueKind.Object)
                                        continue;

                                    var modSubOption = new ModSubOption();

                                    if (subOption.TryGetProperty("name", out var subOptionName))
                                        modSubOption.Name = subOptionName.GetString() ?? string.Empty;

                                    if (subOption.TryGetProperty("description", out var subOptionDesc))
                                        modSubOption.Description = subOptionDesc.GetString() ?? string.Empty;

                                    modOption.SubOptions.Add(modSubOption);
                                }
                            }

                            manifest.Options.Add(modOption);
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
    /// 验证 V1 Mod 结构
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

        // 统计 V1 特有文件
        var v1FileCount = 0;
        foreach (var file in files)
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (V1Extensions.Contains(extension))
            {
                v1FileCount++;
            }
        }

        if (v1FileCount > 0)
        {
            result.Metadata["V1FileCount"] = v1FileCount;
        }

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

                if (manifest.Options != null && manifest.Options.Count > 0)
                {
                    result.Metadata["OptionsCount"] = manifest.Options.Count;

                    // 统计子选项
                    var totalSubOptions = manifest.Options.Sum(o => o.SubOptions?.Count ?? 0);
                    if (totalSubOptions > 0)
                    {
                        result.Metadata["SubOptionsCount"] = totalSubOptions;
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 获取部署文件列表
    /// V1 Mod 需要特殊处理 .patch_0, .gpu_resources, .stream 文件
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
        var excludedFiles = new[] { "manifest.json", ManifestPackage.MetadataFileName, "mod.json", "README.md", "readme.txt" };

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);

            // 跳过 manifest 和 readme 文件
            if (excludedFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                continue;

            var relativePath = Path.GetRelativePath(modPath, file);
            var extension = Path.GetExtension(file).ToLowerInvariant();

            // V1 特有文件优先级更高
            var priority = V1Extensions.Contains(extension) ? 10 : 0;

            deploymentFiles.Add(new WorkflowDeploymentFile
            {
                SourcePath = file,
                TargetRelativePath = relativePath,
                Priority = priority,
                Metadata = new Dictionary<string, object>
                {
                    { "Extension", extension },
                    { "IsV1File", V1Extensions.Contains(extension) }
                }
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

        // V1 Mod 清理所有已部署的文件
        var files = Directory.GetFiles(modPath, "*.*", SearchOption.AllDirectories);
        var excludedFiles = new[] { "manifest.json", ManifestPackage.MetadataFileName, "mod.json", "README.md", "readme.txt" };

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
