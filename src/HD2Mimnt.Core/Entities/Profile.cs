namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Profile 是使用配置层
/// 保存用户当前想使用哪些 Mod 和 LogicalMod，以及它们的启用状态和顺序
/// </summary>
public class Profile
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public bool IsActive { get; set; }
    public bool IsDefault { get; set; }

    /// <summary>
    /// 配置文件在左侧列表中的显示顺序（0 起，最早创建的默认配置固定为 0）
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// 传统 Mod 引用列表
    /// </summary>
    public List<ProfileMod> Mods { get; set; } = new();

    /// <summary>
    /// LogicalMod 引用列表
    /// 根据执行文档第 1208-1243 节，配置文件应保存 LogicalModId
    /// </summary>
    public List<ProfileLogicalMod> LogicalMods { get; set; } = new();
}

/// <summary>
/// Profile 中对 Library Mod 的引用
/// </summary>
public class ProfileMod
{
    public Guid ProfileId { get; set; }
    public Guid ModId { get; set; }
    public bool Enabled { get; set; }
    public int Order { get; set; }

    /// <summary>
    /// 该配置中此模组的选项选择状态（ProfileModOptionsState 的 JSON；空串表示未做过选择，按默认值处理）
    /// </summary>
    public string OptionsJson { get; set; } = string.Empty;
}
