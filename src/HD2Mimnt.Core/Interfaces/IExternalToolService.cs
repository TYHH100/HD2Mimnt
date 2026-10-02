namespace HD2Mimnt.Core.Interfaces;

/// <summary>
/// Service interface for external tool management and launching
/// </summary>
public interface IExternalToolService
{
    /// <summary>
    /// Get all registered external tools
    /// </summary>
    Task<List<Entities.ExternalTool>> GetAllToolsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get enabled external tools
    /// </summary>
    Task<List<Entities.ExternalTool>> GetEnabledToolsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get tools that can open a specific file
    /// </summary>
    Task<List<Entities.ExternalTool>> GetToolsForFileAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Register a new external tool
    /// </summary>
    Task<ToolOperationResult> RegisterToolAsync(Entities.ExternalTool tool, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update an existing external tool
    /// </summary>
    Task<ToolOperationResult> UpdateToolAsync(Entities.ExternalTool tool, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete an external tool
    /// </summary>
    Task<ToolOperationResult> DeleteToolAsync(string toolId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enable or disable a tool
    /// </summary>
    Task<ToolOperationResult> SetToolEnabledAsync(string toolId, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Launch an external tool with a file
    /// </summary>
    Task<ToolOperationResult> LaunchToolAsync(string toolId, string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validate tool configuration
    /// </summary>
    Task<ToolValidationResult> ValidateToolAsync(Entities.ExternalTool tool, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of an external tool operation
/// </summary>
public class ToolOperationResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public object? Data { get; set; }

    public static ToolOperationResult SuccessResult(string message, object? data = null)
        => new() { Success = true, Message = message, Data = data };

    public static ToolOperationResult ErrorResult(string error)
        => new() { Success = false, Error = error };
}

/// <summary>
/// Result of external tool validation
/// </summary>
public class ToolValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();

    public static ToolValidationResult Valid() => new() { IsValid = true };

    public static ToolValidationResult Invalid(params string[] errors)
        => new() { IsValid = false, Errors = new List<string>(errors) };
}
