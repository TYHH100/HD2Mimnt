namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Mod 清单文件的抽象表示
/// 用于描述 Mod 的元数据和配置
/// </summary>
public class ModManifest
{
    public Guid Guid { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string? IconPath { get; set; }
    public string? Category { get; set; }
    public List<string> Tags { get; set; } = new();
    public ManifestVersion ManifestVersion { get; set; }

    /// <summary>
    /// Legacy 格式的选项列表（互斥下拉）
    /// </summary>
    public List<string>? LegacyOptions { get; set; }

    /// <summary>
    /// V1 格式的选项列表（可独立启用）
    /// </summary>
    public List<ModOption>? Options { get; set; }

    /// <summary>
    /// Nexus Mods 数据
    /// </summary>
    public NexusData? NexusData { get; set; }
}

public enum ManifestVersion
{
    Legacy = 0,
    V1 = 1
}

/// <summary>
/// V1 格式的 Mod 选项
/// </summary>
public class ModOption
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool DefaultEnabled { get; set; }
    public List<ModSubOption>? SubOptions { get; set; }
}

/// <summary>
/// 子选项（互斥选择）
/// </summary>
public class ModSubOption
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Nexus Mods 平台数据
/// </summary>
public class NexusData
{
    public int ModId { get; set; }
    public string Version { get; set; } = string.Empty;
}
