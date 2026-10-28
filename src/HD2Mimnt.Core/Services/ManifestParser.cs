using System.Text.Json;
using System.Text.Json.Nodes;
using HD2Mimnt.Core.Entities;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Core.Services;

/// <summary>
/// Mod 清单文件解析服务
/// 支持 Legacy 和 V1 两种格式
/// </summary>
public class ManifestParser
{
    private readonly ILogger<ManifestParser>? _logger;

    public ManifestParser(ILogger<ManifestParser>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// 从 JSON 文件解析清单
    /// </summary>
    public ModManifest? ParseFromFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _logger?.LogWarning("Manifest file not found: {Path}", filePath);
                return null;
            }

            return ParseFromJson(ManifestPackage.Read(filePath,
                error => _logger?.LogWarning(error, "读取 HD2Mimnt 扩展元数据失败，使用标准清单: {Path}", filePath)).ToJsonString());
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to parse manifest from file: {Path}", filePath);
            return null;
        }
    }

    public async Task<ModManifest?> ParseFromFileAsync(string filePath, CancellationToken token = default)
    {
        if (!File.Exists(filePath)) return null;
        var root = await ManifestPackage.ReadAsync(filePath, token,
            error => _logger?.LogWarning(error, "读取 HD2Mimnt 扩展元数据失败，使用标准清单: {Path}", filePath));
        return ParseFromJson(root.ToJsonString());
    }

    /// <summary>
    /// 从 JSON 字符串解析清单
    /// </summary>
    public ModManifest? ParseFromJson(string json)
    {
        try
        {
            // 与旧版管理器一致：容忍尾逗号与注释（真实模组清单常见手写错误）
            var options = new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            };
            using var doc = JsonDocument.Parse(json, options);
            var root = doc.RootElement;

            return ParseFromJsonElement(root);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to parse manifest from JSON");
            return null;
        }
    }

    /// <summary>
    /// 从 JsonElement 解析清单
    /// </summary>
    public ModManifest ParseFromJsonElement(JsonElement root)
    {
        // 解析 GUID
        var guid = ParseGuid(root);

        // 解析基础字段
        var name = GetStringProperty(root, "Name") ?? "Unknown Mod";
        var description = GetStringProperty(root, "Description") ?? string.Empty;
        var version = GetStringProperty(root, "ModVersion") ?? GetStringProperty(root, "Version") ?? "1.0.0";
        var author = GetStringProperty(root, "Author") ?? "Unknown";
        var iconPath = GetStringProperty(root, "IconPath");

        // 检测清单版本：与旧版管理器规则一致——只要存在 Version 字段就按 V1 宽容处理
        // （部分作者把模组自身版本号如 2、5、"1.0" 写进该字段），缺失时才是 Legacy 格式
        var manifestVersion = ManifestVersion.Legacy;
        if (TryGetPropertyIgnoreCase(root, "Version", out var versionProp))
        {
            if (!(versionProp.ValueKind == JsonValueKind.Number && versionProp.TryGetInt32(out var v) && v == 1))
            {
                _logger?.LogWarning("Manifest \"Version\" is not 1, automatically treating the manifest as V1.");
            }

            manifestVersion = ManifestVersion.V1;
        }

        var manifest = new ModManifest
        {
            Guid = guid,
            Name = name,
            Description = description,
            Version = version,
            Author = author,
            AuthorUrl = AuthorWebsite.Normalize(GetStringProperty(root, "AuthorUrl")),
            IconPath = iconPath,
            ManifestVersion = manifestVersion
        };

        // 解析选项
        if (manifestVersion == ManifestVersion.Legacy)
        {
            manifest.LegacyOptions = ParseLegacyOptions(root);
        }
        else
        {
            manifest.Options = ParseV1Options(root);
        }

        manifest.Tags = ParseStringArray(root, "Tags");
        manifest.Category = GetStringProperty(root, "Category");

        // 解析 Nexus 数据
        manifest.NexusData = ParseNexusData(root);

        return manifest;
    }

    /// <summary>
    /// 推断目录中的清单信息
    /// </summary>
    public ModManifest InferFromDirectory(string directory)
    {
        var dirInfo = new DirectoryInfo(directory);
        var guid = Guid.NewGuid();
        var name = dirInfo.Name;
        var description = $"Imported from {dirInfo.Name}";

        // 尝试检测图标文件
        string? iconPath = null;
        var iconExtensions = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".ico" };
        foreach (var ext in iconExtensions)
        {
            var iconFile = Directory.GetFiles(directory, $"*{ext}", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Contains("icon", StringComparison.OrdinalIgnoreCase));
            if (iconFile != null)
            {
                iconPath = Path.GetFileName(iconFile);
                break;
            }
        }

        return new ModManifest
        {
            Guid = guid,
            Name = name,
            Description = description,
            Version = "1",
            Author = "Unknown",
            IconPath = iconPath,
            ManifestVersion = ManifestVersion.V1
        };
    }

    /// <summary>
    /// 保存清单到文件
    /// </summary>
    public void SaveToFile(ModManifest manifest, string filePath, System.Text.Json.Nodes.JsonObject? original = null)
    {
        var combined = Serialize(manifest);
        if (original != null) combined = ManifestPackage.PreserveUnknownExtensions(combined, original);
        ManifestPackage.Save(filePath, combined);
    }

    /// <summary>
    /// 保存导入时自动生成的档案：标准字段与作者清单相同，扩展元数据带生成标记，
    /// 使内容哈希和判重按“作者内容”比较，不随来源名称或随机 Guid 变化。
    /// </summary>
    public void SaveGeneratedToFile(ModManifest manifest, string filePath)
    {
        var combined = Serialize(manifest);
        combined[ManifestPackage.GeneratedManifestExtension] = true;
        ManifestPackage.Save(filePath, combined);
    }

    private JsonObject Serialize(ModManifest manifest)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            WriteManifest(writer, manifest);
        return ManifestPackage.Parse(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    private void WriteManifest(Utf8JsonWriter writer, ModManifest manifest)
    {
        writer.WriteStartObject();

        if (manifest.ManifestVersion == ManifestVersion.V1)
        {
            writer.WriteNumber("Version", (int)manifest.ManifestVersion);
        }

        writer.WriteString("Guid", manifest.Guid.ToString());
        writer.WriteString("Name", manifest.Name);
        writer.WriteString("Description", manifest.Description);
        if (!string.IsNullOrEmpty(manifest.Version)) writer.WriteString("ModVersion", manifest.Version);

        // Version 是清单格式标记；Legacy 写入该字段会在再次读取时被误判为 V1。

        if (!string.IsNullOrEmpty(manifest.Author))
        {
            writer.WriteString("Author", manifest.Author);
        }
        if (AuthorWebsite.Normalize(manifest.AuthorUrl) is { } authorUrl)
            writer.WriteString("AuthorUrl", authorUrl);

        if (!string.IsNullOrEmpty(manifest.IconPath))
        {
            writer.WriteString("IconPath", manifest.IconPath);
        }

        if (manifest.Category != null) writer.WriteString("Category", manifest.Category);
        writer.WriteStartArray("Tags");
        foreach (var tag in manifest.Tags) writer.WriteStringValue(tag);
        writer.WriteEndArray();

        // 写入选项
        if (manifest.ManifestVersion == ManifestVersion.Legacy && manifest.LegacyOptions?.Count > 0)
        {
            writer.WriteStartArray("Options");
            foreach (var option in manifest.LegacyOptions)
            {
                writer.WriteStringValue(option);
            }
            writer.WriteEndArray();
        }
        else if (manifest.Options?.Count > 0)
        {
            writer.WriteStartArray("Options");
            foreach (var option in manifest.Options)
            {
                WriteModOption(writer, option);
            }
            writer.WriteEndArray();
        }

        // 写入 Nexus 数据
        if (manifest.NexusData != null)
        {
            writer.WriteStartObject("NexusData");
            writer.WriteNumber("ModId", manifest.NexusData.ModId);
            writer.WriteString("Version", manifest.NexusData.Version);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private void WriteModOption(Utf8JsonWriter writer, ModOption option)
    {
        writer.WriteStartObject();
        writer.WriteString("Name", option.Name);
        writer.WriteString("Description", option.Description);

        if (option.DefaultEnabled.HasValue)
        {
            writer.WriteBoolean("DefaultEnabled", option.DefaultEnabled.Value);
        }

        if (option.Include != null)
        {
            writer.WriteStartArray("Include");
            foreach (var include in option.Include)
            {
                writer.WriteStringValue(include);
            }
            writer.WriteEndArray();
        }

        if (!string.IsNullOrEmpty(option.Image))
        {
            writer.WriteString("Image", option.Image);
        }

        if (option.SubOptions?.Count > 0)
        {
            writer.WriteStartArray("SubOptions");
            foreach (var subOption in option.SubOptions)
            {
                WriteModSubOption(writer, subOption);
            }
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteModSubOption(Utf8JsonWriter writer, ModSubOption subOption)
    {
        writer.WriteStartObject();
        writer.WriteString("Name", subOption.Name);
        writer.WriteString("Description", subOption.Description);

        if (subOption.Include != null)
        {
            writer.WriteStartArray("Include");
            foreach (var include in subOption.Include)
            {
                writer.WriteStringValue(include);
            }
            writer.WriteEndArray();
        }

        if (!string.IsNullOrEmpty(subOption.Image))
        {
            writer.WriteString("Image", subOption.Image);
        }

        writer.WriteEndObject();
    }

    private Guid ParseGuid(JsonElement root)
    {
        if (Guid.TryParse(GetStringProperty(root, "Guid"), out var guid))
            return guid;

        _logger?.LogWarning("Invalid or missing GUID in manifest, generating new one");
        return Guid.NewGuid();
    }

    /// <summary>
    /// 大小写不敏感的属性查找：真实模组清单的字段名大小写混用
    /// （旧版格式用 PascalCase，也有作者全小写），先精确匹配再回退忽略大小写。
    /// </summary>
    private static bool TryGetPropertyIgnoreCase(JsonElement root, string propertyName, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty(propertyName, out value))
            {
                return true;
            }

            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private string? GetStringProperty(JsonElement root, string propertyName)
    {
        if (TryGetPropertyIgnoreCase(root, propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    private List<string>? ParseLegacyOptions(JsonElement root)
    {
        if (!TryGetPropertyIgnoreCase(root, "Options", out var optionsProp) || optionsProp.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var options = new List<string>();
        foreach (var element in optionsProp.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var optionName = element.GetString();
                if (!string.IsNullOrWhiteSpace(optionName))
                {
                    options.Add(optionName);
                }
            }
        }

        return options.Count > 0 ? options : null;
    }

    private List<ModOption>? ParseV1Options(JsonElement root)
    {
        if (!TryGetPropertyIgnoreCase(root, "Options", out var optionsProp) || optionsProp.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var options = new List<ModOption>();
        foreach (var element in optionsProp.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var option = ParseModOption(element);
                options.Add(option);
            }
        }

        return options.Count > 0 ? options : null;
    }

    private ModOption ParseModOption(JsonElement element)
    {
        var name = GetStringProperty(element, "Name") ?? "Unnamed Option";
        var description = GetStringProperty(element, "Description") ?? string.Empty;

        // 旧版清单没有 DefaultEnabled 字段，缺省保持 null（按“默认启用”处理）
        bool? defaultEnabled = null;
        if (TryGetPropertyIgnoreCase(element, "DefaultEnabled", out var defaultEnabledProp)
            && defaultEnabledProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            defaultEnabled = defaultEnabledProp.GetBoolean();
        }

        var option = new ModOption
        {
            Name = name,
            Description = description,
            DefaultEnabled = defaultEnabled,
            Include = ParseInclude(element),
            Image = GetStringProperty(element, "Image")
        };

        // 解析子选项
        if (TryGetPropertyIgnoreCase(element, "SubOptions", out var subOptionsProp) && subOptionsProp.ValueKind == JsonValueKind.Array)
        {
            var subOptions = new List<ModSubOption>();
            foreach (var subElement in subOptionsProp.EnumerateArray())
            {
                if (subElement.ValueKind == JsonValueKind.Object)
                {
                    subOptions.Add(new ModSubOption
                    {
                        Name = GetStringProperty(subElement, "Name") ?? "Unnamed SubOption",
                        Description = GetStringProperty(subElement, "Description") ?? string.Empty,
                        Include = ParseInclude(subElement),
                        Image = GetStringProperty(subElement, "Image")
                    });
                }
            }
            option.SubOptions = subOptions.Count > 0 ? subOptions : null;
        }

        return option;
    }

    private static List<string>? ParseInclude(JsonElement element)
        => TryGetPropertyIgnoreCase(element, "Include", out var include) && include.ValueKind == JsonValueKind.Array
            ? ParseStringArray(element, "Include") : null;

    private static List<string> ParseStringArray(JsonElement root, string propertyName)
    {
        var values = new List<string>();
        if (TryGetPropertyIgnoreCase(root, propertyName, out var prop) && prop.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in prop.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString()))
                {
                    values.Add(element.GetString()!);
                }
            }
        }

        return values;
    }

    private NexusData? ParseNexusData(JsonElement root)
    {
        if (!TryGetPropertyIgnoreCase(root, "NexusData", out var nexusProp) || nexusProp.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var modId = 0;
        if (TryGetPropertyIgnoreCase(nexusProp, "ModId", out var idProp))
        {
            if (idProp.ValueKind == JsonValueKind.Number)
                idProp.TryGetInt32(out modId);
            else if (idProp.ValueKind == JsonValueKind.String)
                int.TryParse(idProp.GetString(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out modId);
        }
        var version = GetStringProperty(nexusProp, "Version") ?? string.Empty;

        if (modId > 0)
        {
            return new NexusData { ModId = modId, Version = version };
        }

        return null;
    }
}
