namespace HD2Mimnt.Core.Entities;

/// <summary>
/// 自动拆包 Lua 的持久化记录：功能关闭再开启时按记录跳过已处理模组，不再重复提取。
/// ContentStamp 为提取时的模组目录内容戳，变化即模组内容已更新，需要重新提取。
/// </summary>
public sealed record LuaExtractionResult
{
    public required Guid ModId { get; init; }
    public DateTime ExtractedAtUtc { get; init; }
    public int SourceCount { get; init; }
    public int BytecodeCount { get; init; }
    public string ContentStamp { get; init; } = string.Empty;
}
