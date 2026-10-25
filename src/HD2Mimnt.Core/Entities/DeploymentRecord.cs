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
    /// <summary>部署时归一化后的选项；null 表示旧记录未知，不能用当前配置补造。</summary>
    public string? OptionsSnapshotJson { get; set; }
    public int? DeploymentOrderIndex { get; set; }
    public string? ModVersion { get; set; }
    /// <summary>当前状态所属游戏安装根目录；null 表示旧记录尚未归属到某个安装。</summary>
    public string? GamePath { get; set; }
    /// <summary>最近一次对账使用的部署方向。</summary>
    public string? DeploymentOrder { get; set; }
    /// <summary>最近一次实际写入或确认保留的时间。</summary>
    public DateTime? AppliedAt { get; set; }
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
    /// <summary>当前状态所属游戏安装根目录。</summary>
    public string? GamePath { get; set; }
    /// <summary>稳定文件身份：patch:&lt;源相对路径&gt; 或 native:&lt;游戏根相对目标&gt;；旧记录可为 null。</summary>
    public string? SourceKey { get; set; }
    /// <summary>相对模组根目录的源路径；旧记录可为 null。</summary>
    public string? SourceRelativePath { get; set; }
}
