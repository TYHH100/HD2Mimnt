namespace HD2Mimnt.PluginHost.IPC;

/// <summary>
/// Plugin host message for IPC communication
/// </summary>
public abstract class PluginHostMessage
{
    public string Type { get; set; } = string.Empty;

    /// <summary>插件标识，取自 manifest.Id（反向域名或 GUID 均可）。</summary>
    public string PluginId { get; set; } = string.Empty;
}

/// <summary>
/// Command to load and execute plugin
/// </summary>
public class LoadPluginCommand : PluginHostMessage
{
    public string PluginPath { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public Dictionary<string, string> Arguments { get; set; } = new();
}

/// <summary>
/// Command to execute plugin method
/// </summary>
public class ExecutePluginCommand : PluginHostMessage
{
    public string MethodName { get; set; } = string.Empty;
    public string? ArgumentsJson { get; set; }
}

/// <summary>
/// Plugin progress response
/// </summary>
public class PluginProgressResponse : PluginHostMessage
{
    public string Message { get; set; } = string.Empty;
    public int CurrentStep { get; set; }
    public int TotalSteps { get; set; }
}

/// <summary>
/// Plugin completed response
/// </summary>
public class PluginCompletedResponse : PluginHostMessage
{
    public bool Success { get; set; }
    public string? ResultJson { get; set; }

    /// <summary>声明式界面更新的 JSON（PluginUiUpdate），由主程序渲染；不含任何 UI 对象。</summary>
    public string? UiJson { get; set; }

    public List<string>? Errors { get; set; }
}

/// <summary>
/// Plugin error response
/// </summary>
public class PluginErrorResponse : PluginHostMessage
{
    public string ErrorMessage { get; set; } = string.Empty;
    public string? StackTrace { get; set; }
}

/// <summary>
/// Plugin log message
/// </summary>
public class PluginLogMessage : PluginHostMessage
{
    public string Level { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Exception { get; set; }
}

/// <summary>
/// 插件向主程序发起的 AI 请求（宿主进程内不存在任何密钥材料）。
/// </summary>
public class PluginAiRequest : PluginHostMessage
{
    public string RequestId { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string? SystemPrompt { get; set; }
    public int? MaxOutputTokens { get; set; }
}

/// <summary>
/// 主程序对 AI 请求的响应，只回传模型文本，不含请求细节或凭据。
/// </summary>
public class PluginAiResponse : PluginHostMessage
{
    public string RequestId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? Error { get; set; }
    public int TokensUsed { get; set; }
}
