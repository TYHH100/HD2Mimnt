namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Deployment Record 记录部署历史
/// 用于 Cleanup、Delete Mod、Diagnostics、History
/// </summary>
public class DeploymentRecord
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid ProfileId { get; set; }
    public Guid ModId { get; set; }
    public string Workflow { get; set; } = string.Empty;
    public string DeploymentMethod { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string Status { get; set; } = string.Empty;
    public List<DeploymentFile> Files { get; set; } = new();
}

/// <summary>
/// DeploymentFile 记录具体部署的文件
/// 用于精准 Cleanup
/// </summary>
public class DeploymentFile
{
    public Guid Id { get; set; }
    public Guid DeploymentId { get; set; }
    public Guid ModId { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
}
