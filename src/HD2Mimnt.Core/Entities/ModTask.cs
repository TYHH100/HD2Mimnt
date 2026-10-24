namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Task 是所有后台工作的统一出口
/// 包括：Import、Scan、Validation、Analysis、Deployment、Cleanup、Download、Update、Delete 等
/// </summary>
public class ModTask
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public TaskStatus Status { get; set; }
    public double Progress { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string CurrentItem { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public List<string> Errors { get; set; } = new();
    public string Result { get; set; } = string.Empty;
}

public enum TaskStatus
{
    Waiting,
    Running,
    Completed,
    PartiallyFailed,
    Failed,
    Cancelled,
    ForceTerminated
}
