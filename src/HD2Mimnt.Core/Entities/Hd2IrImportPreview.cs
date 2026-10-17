namespace HD2Mimnt.Core.Entities;

/// <summary>导入浮窗展示的组件组合；确认前不写入模组库。</summary>
public sealed record Hd2IrImportPreview(string Name, IReadOnlyList<string> Components, string Reason);
