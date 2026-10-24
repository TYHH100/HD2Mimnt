namespace HD2Mimnt.PluginHost.IPC;

/// <summary>
/// Plugin host message for IPC communication
/// </summary>
public abstract class PluginHostMessage
{
    public string Type { get; set; } = string.Empty;
    public Guid PluginId { get; set; }
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
