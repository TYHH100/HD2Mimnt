namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Profile 中对 LogicalMod 的引用
/// 根据执行文档第 1208-1243 节（配置文件系统接入）
/// 配置文件应保存 LogicalModId，而不是 ZIP 文件路径
/// </summary>
public class ProfileLogicalMod
{
    public Guid ProfileId { get; set; }
    public Guid LogicalModId { get; set; }
    public bool Enabled { get; set; }
    public int Order { get; set; }

    /// <summary>
    /// 启用的组件 ID 列表
    /// 允许用户选择性启用 LogicalMod 的部分组件
    /// </summary>
    public List<Guid> EnabledComponentIds { get; set; } = new();

    /// <summary>
    /// 组件配置覆盖
    /// Key: ComponentId, Value: 配置 JSON
    /// </summary>
    public Dictionary<Guid, string> ComponentConfigurations { get; set; } = new();
}
