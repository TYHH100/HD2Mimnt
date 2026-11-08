namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Result of a plugin operation (Core layer version)
/// </summary>
public class PluginOperationResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public object? Data { get; set; }

    /// <summary>插件回传的声明式界面更新（PluginUiUpdate 的 JSON），由 UI 层反序列化渲染。</summary>
    public string? UiJson { get; set; }

    public static PluginOperationResult SuccessResult(string? message = null, object? data = null)
    {
        return new PluginOperationResult
        {
            Success = true,
            Message = message,
            Data = data
        };
    }

    public static PluginOperationResult ErrorResult(string error, string? message = null)
    {
        return new PluginOperationResult
        {
            Success = false,
            Error = error,
            Message = message
        };
    }
}

/// <summary>
/// Manager for plugin host processes
/// </summary>
public interface IPluginHostManager
{
    /// <summary>
    /// Start a plugin host process
    /// </summary>
    Task<PluginOperationResult> StartPluginAsync(string pluginId, string assemblyPath, string manifestPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop a running plugin
    /// </summary>
    Task StopPluginAsync(string pluginId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute a plugin command
    /// </summary>
    Task<PluginOperationResult> ExecutePluginCommandAsync(string pluginId, string methodName, string? argumentsJson = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all running plugin hosts
    /// </summary>
    IReadOnlyList<PluginHostInfo> GetRunningPlugins();

    /// <summary>
    /// Force terminate a plugin host
    /// </summary>
    void ForceTerminate(string pluginId);
}

/// <summary>
/// Information about a running plugin host
/// </summary>
public class PluginHostInfo
{
    public Guid HostId { get; set; }
    public string PluginId { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public bool IsRunning { get; set; }
}
