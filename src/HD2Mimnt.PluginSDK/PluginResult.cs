namespace HD2Mimnt.PluginSDK;

/// <summary>
/// Result of a plugin operation
/// </summary>
public class PluginResult
{
    /// <summary>
    /// Whether the operation succeeded
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Result message
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Error message if operation failed
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Additional metadata
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>
    /// 可选的界面更新。插件通过声明式区块让管理器刷新页面/浮窗，
    /// 不接触任何 UI 对象；为空时管理器只展示 <see cref="Message"/>。
    /// </summary>
    public PluginUiUpdate? Ui { get; set; }

    /// <summary>
    /// Create a successful result
    /// </summary>
    public static PluginResult Ok(string? message = null, PluginUiUpdate? ui = null)
    {
        return new PluginResult { Success = true, Message = message, Ui = ui };
    }

    /// <summary>
    /// Create a failed result
    /// </summary>
    public static PluginResult Fail(string error)
    {
        return new PluginResult { Success = false, Error = error };
    }
}

/// <summary>
/// Result of a plugin analysis operation
/// </summary>
public class PluginAnalysisResult : PluginResult
{
    /// <summary>
    /// List of issues found during analysis
    /// </summary>
    public List<string> Issues { get; set; } = new();

    /// <summary>
    /// List of warnings found during analysis
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// Recommendations for the mod
    /// </summary>
    public List<string> Recommendations { get; set; } = new();
}
