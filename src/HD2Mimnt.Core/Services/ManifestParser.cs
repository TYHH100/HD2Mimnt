using System.Text.Json;
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

            var json = File.ReadAllText(filePath);
            return ParseFromJson(json);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to parse manifest from file: {Path}", filePath);
            return null;
        }
    }

    /// <summary>
    /// 从 JSON 字符串解析清单
    /// </summary>
    public ModManifest? ParseFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
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
        var version = GetStringProperty(root, "Version") ?? "1.0.0";
        var author = GetStringProperty(root, "Author") ?? "Unknown";
        var iconPath = GetStringProperty(root, "IconPath");

        // 检测清单版本
        var manifestVersion = ManifestVersion.Legacy;
        if (root.TryGetProperty("Version", out var versionProp) && versionProp.ValueKind == JsonValueKind.Number)
        {
            manifestVersion = (ManifestVersion)versionProp.GetInt32();
        }

        var manifest = new ModManifest
        {
            Guid = guid,
            Name = name,
            Description = description,
            Version = version,
            Author = author,
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
    public void SaveToFile(ModManifest manifest, string filePath)
    {
        var options = new JsonWriterOptions { Indented = true };
        using var stream = File.Create(filePath);
        using var writer = new Utf8JsonWriter(stream, options);

        WriteManifest(writer, manifest);
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

        if (!string.IsNullOrEmpty(manifest.Version))
        {
            writer.WriteString("Version", manifest.Version);
        }

        if (!string.IsNullOrEmpty(manifest.Author))
        {
            writer.WriteString("Author", manifest.Author);
        }

        if (!string.IsNullOrEmpty(manifest.IconPath))
        {
            writer.WriteString("IconPath", manifest.IconPath);
        }

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
        writer.WriteBoolean("DefaultEnabled", option.DefaultEnabled);

        if (option.SubOptions?.Count > 0)
        {
            writer.WriteStartArray("SubOptions");
            foreach (var subOption in option.SubOptions)
            {
                writer.WriteStartObject();
                writer.WriteString("Name", subOption.Name);
                writer.WriteString("Description", subOption.Description);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private Guid ParseGuid(JsonElement root)
    {
        if (root.TryGetProperty("Guid", out var guidProp))
        {
            var guidStr = guidProp.GetString();
            if (Guid.TryParse(guidStr, out var guid))
            {
                return guid;
            }
        }

        _logger?.LogWarning("Invalid or missing GUID in manifest, generating new one");
        return Guid.NewGuid();
    }

    private string? GetStringProperty(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    private List<string>? ParseLegacyOptions(JsonElement root)
    {
        if (!root.TryGetProperty("Options", out var optionsProp) || optionsProp.ValueKind != JsonValueKind.Array)
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
        if (!root.TryGetProperty("Options", out var optionsProp) || optionsProp.ValueKind != JsonValueKind.Array)
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
        var defaultEnabled = element.TryGetProperty("DefaultEnabled", out var prop) && prop.GetBoolean();

        var option = new ModOption
        {
            Name = name,
            Description = description,
            DefaultEnabled = defaultEnabled
        };

        // 解析子选项
        if (element.TryGetProperty("SubOptions", out var subOptionsProp) && subOptionsProp.ValueKind == JsonValueKind.Array)
        {
            var subOptions = new List<ModSubOption>();
            foreach (var subElement in subOptionsProp.EnumerateArray())
            {
                if (subElement.ValueKind == JsonValueKind.Object)
                {
                    var subOption = new ModSubOption
                    {
                        Name = GetStringProperty(subElement, "Name") ?? "Unnamed SubOption",
                        Description = GetStringProperty(subElement, "Description") ?? string.Empty
                    };
                    subOptions.Add(subOption);
                }
            }
            option.SubOptions = subOptions.Count > 0 ? subOptions : null;
        }

        return option;
    }

    private NexusData? ParseNexusData(JsonElement root)
    {
        if (!root.TryGetProperty("NexusData", out var nexusProp) || nexusProp.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var modId = nexusProp.TryGetProperty("ModId", out var idProp) ? idProp.GetInt32() : 0;
        var version = GetStringProperty(nexusProp, "Version") ?? string.Empty;

        if (modId > 0)
        {
            return new NexusData { ModId = modId, Version = version };
        }

        return null;
    }
}
