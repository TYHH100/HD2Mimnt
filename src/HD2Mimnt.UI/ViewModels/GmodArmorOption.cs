using HD2Mimnt.Application.Services;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// 目标护甲下拉项。<see cref="Target"/> 为 null 表示「不指定」——此时用高级面板里手填的
/// Archive / equipment / Native Unit，游戏中只有在这些 ID 属于某件真实护甲时才会显示。
/// </summary>
public sealed class GmodArmorOption(string label, GmodIasArmorTarget? target)
{
    /// <summary>下拉框显示文本。</summary>
    public string Label { get; } = label;

    public GmodIasArmorTarget? Target { get; } = target;

    public bool IsNone => Target is null;

    public override string ToString() => Label;
}
