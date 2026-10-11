namespace HD2Mimnt.Core.Entities;

/// <summary>单个模组的实际导入阶段；无法测量的阶段使用不确定进度。</summary>
public sealed record ModImportProgress(double Percentage, string Step, string Detail, bool IsIndeterminate = false);
