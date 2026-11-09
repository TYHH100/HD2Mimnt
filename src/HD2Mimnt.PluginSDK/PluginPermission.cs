namespace HD2Mimnt.PluginSDK;

/// <summary>
/// Available plugin permissions
/// </summary>
public enum PluginPermission
{
    /// <summary>
    /// Read mod files from mod directories
    /// </summary>
    ReadModFiles,

    /// <summary>
    /// Write to mod files (requires FullAccess)
    /// </summary>
    WriteModFiles,

    /// <summary>
    /// Access network (requires FullAccess)
    /// </summary>
    AccessNetwork,

    /// <summary>
    /// Execute external processes (requires FullAccess)
    /// </summary>
    ExecuteProcesses,

    /// <summary>
    /// Read database via safe DTOs (requires FullAccess)
    /// </summary>
    ReadDatabase,

    /// <summary>
    /// Write plugin configuration
    /// </summary>
    WriteConfiguration,

    /// <summary>
    /// 通过主程序代理调用 AI。插件只能提交提示词并获取文本结果，
    /// 永远无法读取 API Key；提示词中出现密钥将被判定为违规并终止插件。
    /// manifest 中对应权限标识为 "ai.use"，且不会随安装自动授予。
    /// </summary>
    UseAI
}
