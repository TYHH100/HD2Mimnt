namespace HD2Mimnt.Core.Entities;

/// <summary>
/// Plugin entity representing installed plugins
/// </summary>
public class Plugin
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string ManifestPath { get; set; } = string.Empty;
    public string AssemblyPath { get; set; } = string.Empty;
    /// <summary>安装时记录的程序集 SHA-256（小写十六进制）；启动前校验，防安装后被替换。</summary>
    public string? AssemblySha256 { get; set; }
    /// <summary>安装时记录的 manifest.json SHA-256；启动前校验——命令权限门控运行时重读清单，防被改摘除门控。</summary>
    public string? ManifestSha256 { get; set; }
    /// <summary>混合插件（脚本 + 命令库 DLL）安装时记录的 DLL SHA-256；宿主加载前校验。</summary>
    public string? HybridAssemblySha256 { get; set; }
    public DateTime InstalledAt { get; set; }
    public bool Enabled { get; set; } = true;
    public string PermissionMode { get; set; } = "Sandbox"; // 'Sandbox' | 'FullAccess'
}

/// <summary>
/// Plugin permission entity
/// </summary>
public class PluginPermission
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PluginId { get; set; } = string.Empty;
    public string Permission { get; set; } = string.Empty;
    public bool Granted { get; set; } = false;
    public DateTime? GrantedAt { get; set; }
}

/// <summary>
/// Plugin execution history entity
/// </summary>
public class PluginExecutionHistory
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PluginId { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string Status { get; set; } = string.Empty; // Pending, Running, Completed, Failed
    public string? Result { get; set; }
    public string? Error { get; set; }
}
