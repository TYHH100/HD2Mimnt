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
    /// <summary>可选作者网站，保存到独立扩展 JSON。</summary>
    public string? AuthorUrl { get; set; }
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
    /// 组件分组（仅由 HD2IAS 角色包导入生成，保存到管理器自有 JSON 与数据库，
    /// 不写入作者 manifest.json）。用于把同一组件的选项组织到二级配置页。
    /// </summary>
    public List<ModOptionGroup>? OptionGroups { get; set; }

    /// <summary>
    /// Nexus Mods 数据
    /// </summary>
    public NexusData? NexusData { get; set; }
}

/// <summary>
/// 管理器生成的组件分组：一级配置页展示一个组件，其选项在二级页配置。
/// 只存在于 HD2Mimnt 自有元数据中，标准 manifest.json 不包含该结构。
/// </summary>
public class ModOptionGroup
{
    /// <summary>分组标识；<see cref="ModOption.GroupId"/> 指向它。</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>组件预览图相对路径（仅展示用，位于模组目录内）。</summary>
    public string? Image { get; set; }
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

    /// <summary>
    /// 清单未声明时的默认启用值；null（旧版清单没有该字段）表示默认启用
    /// </summary>
    public bool? DefaultEnabled { get; set; }

    /// <summary>
    /// 选项启用时参与部署的相对目录列表；空列表是合法的“无文件占位”选项。
    /// null 表示清单未声明映射，部署时无法按选项过滤，需回退全量部署
    /// </summary>
    public List<string>? Include { get; set; }

    /// <summary>选项预览图相对路径（仅展示用）</summary>
    public string? Image { get; set; }

    /// <summary>所属组件分组（HD2IAS 角色包导入生成）；null 表示不属于任何分组。</summary>
    public string? GroupId { get; set; }

    public List<ModSubOption>? SubOptions { get; set; }
}

/// <summary>
/// 子选项（互斥选择）
/// </summary>
public class ModSubOption
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 子选项被选中时参与部署的相对目录列表
    /// </summary>
    public List<string>? Include { get; set; }

    /// <summary>子选项预览图相对路径（仅展示用）</summary>
    public string? Image { get; set; }
}

/// <summary>
/// Nexus Mods 平台数据
/// </summary>
public class NexusData
{
    public int ModId { get; set; }
    public string Version { get; set; } = string.Empty;
}
