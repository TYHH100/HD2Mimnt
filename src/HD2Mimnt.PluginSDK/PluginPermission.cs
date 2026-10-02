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
    WriteConfiguration
}
