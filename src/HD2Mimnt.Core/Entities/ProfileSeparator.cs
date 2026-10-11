namespace HD2Mimnt.Core.Entities;

/// <summary>配置文件中的用户分组分隔符。它只用于组织显示，不是可部署模组。</summary>
public sealed class ProfileSeparator
{
    public Guid Id { get; set; }
    public Guid ProfileId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>分隔符之前的普通模组数量；Lua 固定区域不参与用户分组。</summary>
    public int Order { get; set; }
    public bool IsCollapsed { get; set; }
    /// <summary>不透明 RGB 颜色（#RRGGBB）；空值跟随主题。</summary>
    public string Color { get; set; } = string.Empty;
}
