using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Service for managing and launching external tools
/// </summary>
public class ExternalToolService : IExternalToolService
{
    private readonly IExternalToolRepository _repository;
    private readonly ILogger<ExternalToolService> _logger;

    public ExternalToolService(
        IExternalToolRepository repository,
        ILogger<ExternalToolService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<List<ExternalTool>> GetAllToolsAsync(CancellationToken cancellationToken = default)
    {
        return await _repository.GetAllAsync(cancellationToken);
    }

    public async Task<List<ExternalTool>> GetEnabledToolsAsync(CancellationToken cancellationToken = default)
    {
        return await _repository.GetEnabledAsync(cancellationToken);
    }

    public async Task<List<ExternalTool>> GetToolsForFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new List<ExternalTool>();
        }

        var extension = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(extension))
        {
            return new List<ExternalTool>();
        }

        return await _repository.GetByExtensionAsync(extension, cancellationToken);
    }

    public async Task<ToolOperationResult> RegisterToolAsync(ExternalTool tool, CancellationToken cancellationToken = default)
    {
        // Validate tool
        var validation = await ValidateToolAsync(tool, cancellationToken);
        if (!validation.IsValid)
        {
            return ToolOperationResult.ErrorResult($"Tool validation failed: {string.Join(", ", validation.Errors)}");
        }

        // Check if tool already exists
        if (await _repository.ExistsAsync(tool.Id, cancellationToken))
        {
            return ToolOperationResult.ErrorResult($"Tool with ID '{tool.Id}' already exists");
        }

        try
        {
            tool.CreatedAt = DateTime.UtcNow;
            await _repository.AddAsync(tool, cancellationToken);
            _logger.LogInformation("Registered external tool: {ToolName} ({ToolId})", tool.Name, tool.Id);
            return ToolOperationResult.SuccessResult("Tool registered successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register external tool: {ToolName}", tool.Name);
            return ToolOperationResult.ErrorResult($"Failed to register tool: {ex.Message}");
        }
    }

    public async Task<ToolOperationResult> UpdateToolAsync(ExternalTool tool, CancellationToken cancellationToken = default)
    {
        // Validate tool
        var validation = await ValidateToolAsync(tool, cancellationToken);
        if (!validation.IsValid)
        {
            return ToolOperationResult.ErrorResult($"Tool validation failed: {string.Join(", ", validation.Errors)}");
        }

        // Check if tool exists
        var existing = await _repository.GetByIdAsync(tool.Id, cancellationToken);
        if (existing == null)
        {
            return ToolOperationResult.ErrorResult($"Tool with ID '{tool.Id}' not found");
        }

        try
        {
            await _repository.UpdateAsync(tool, cancellationToken);
            _logger.LogInformation("Updated external tool: {ToolName} ({ToolId})", tool.Name, tool.Id);
            return ToolOperationResult.SuccessResult("Tool updated successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update external tool: {ToolName}", tool.Name);
            return ToolOperationResult.ErrorResult($"Failed to update tool: {ex.Message}");
        }
    }

    public async Task<ToolOperationResult> DeleteToolAsync(string toolId, CancellationToken cancellationToken = default)
    {
        var tool = await _repository.GetByIdAsync(toolId, cancellationToken);
        if (tool == null)
        {
            return ToolOperationResult.ErrorResult($"Tool with ID '{toolId}' not found");
        }

        try
        {
            await _repository.DeleteAsync(toolId, cancellationToken);
            _logger.LogInformation("Deleted external tool: {ToolName} ({ToolId})", tool.Name, toolId);
            return ToolOperationResult.SuccessResult("Tool deleted successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete external tool: {ToolId}", toolId);
            return ToolOperationResult.ErrorResult($"Failed to delete tool: {ex.Message}");
        }
    }

    public async Task<ToolOperationResult> SetToolEnabledAsync(string toolId, bool enabled, CancellationToken cancellationToken = default)
    {
        var tool = await _repository.GetByIdAsync(toolId, cancellationToken);
        if (tool == null)
        {
            return ToolOperationResult.ErrorResult($"Tool with ID '{toolId}' not found");
        }

        try
        {
            tool.IsEnabled = enabled;
            await _repository.UpdateAsync(tool, cancellationToken);
            _logger.LogInformation("{Action} external tool: {ToolName} ({ToolId})",
                enabled ? "Enabled" : "Disabled", tool.Name, toolId);
            return ToolOperationResult.SuccessResult($"Tool {(enabled ? "enabled" : "disabled")} successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to {Action} external tool: {ToolId}",
                enabled ? "enable" : "disable", toolId);
            return ToolOperationResult.ErrorResult($"Failed to {(enabled ? "enable" : "disable")} tool: {ex.Message}");
        }
    }

    public async Task<ToolOperationResult> LaunchToolAsync(string toolId, string filePath, CancellationToken cancellationToken = default)
    {
        var tool = await _repository.GetByIdAsync(toolId, cancellationToken);
        if (tool == null)
        {
            return ToolOperationResult.ErrorResult($"Tool with ID '{toolId}' not found");
        }

        if (!tool.IsEnabled)
        {
            return ToolOperationResult.ErrorResult($"Tool '{tool.Name}' is disabled");
        }

        if (!File.Exists(tool.ExecutablePath))
        {
            return ToolOperationResult.ErrorResult($"Executable not found: {tool.ExecutablePath}");
        }

        if (!File.Exists(filePath))
        {
            return ToolOperationResult.ErrorResult($"File not found: {filePath}");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = tool.ExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = false
            };

            // Build arguments with placeholder replacement
            if (!string.IsNullOrWhiteSpace(tool.Arguments))
            {
                var arguments = ReplacePlaceholders(tool.Arguments, filePath);
                startInfo.Arguments = arguments;
            }
            else
            {
                // Default: just pass the file path
                startInfo.Arguments = $"\"{filePath}\"";
            }

            // Set working directory
            if (!string.IsNullOrWhiteSpace(tool.WorkingDirectory))
            {
                startInfo.WorkingDirectory = tool.WorkingDirectory;
            }
            else
            {
                // Default to file's directory
                startInfo.WorkingDirectory = Path.GetDirectoryName(filePath) ?? Environment.CurrentDirectory;
            }

            _logger.LogInformation("Launching external tool: {ToolName} with file: {FilePath}", tool.Name, filePath);
            _logger.LogDebug("Command: {Executable} {Arguments}", tool.ExecutablePath, startInfo.Arguments);

            Process.Start(startInfo);

            return ToolOperationResult.SuccessResult($"Launched {tool.Name}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch external tool: {ToolName} with file: {FilePath}", tool.Name, filePath);
            return ToolOperationResult.ErrorResult($"Failed to launch tool: {ex.Message}");
        }
    }

    public async Task<ToolValidationResult> ValidateToolAsync(ExternalTool tool, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // Validate required fields
        if (string.IsNullOrWhiteSpace(tool.Id))
        {
            errors.Add("Tool ID is required");
        }

        if (string.IsNullOrWhiteSpace(tool.Name))
        {
            errors.Add("Tool name is required");
        }

        if (string.IsNullOrWhiteSpace(tool.ExecutablePath))
        {
            errors.Add("Executable path is required");
        }
        else if (!File.Exists(tool.ExecutablePath))
        {
            errors.Add($"Executable not found: {tool.ExecutablePath}");
        }

        // Validate working directory if specified
        if (!string.IsNullOrWhiteSpace(tool.WorkingDirectory) && !Directory.Exists(tool.WorkingDirectory))
        {
            warnings.Add($"Working directory does not exist: {tool.WorkingDirectory}");
        }

        // Validate supported extensions format
        if (!string.IsNullOrWhiteSpace(tool.SupportedExtensions))
        {
            var extensions = tool.SupportedExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var ext in extensions)
            {
                var trimmed = ext.Trim();
                if (!trimmed.StartsWith("."))
                {
                    warnings.Add($"Extension '{trimmed}' should start with a dot (e.g., '.txt')");
                }
            }
        }

        await Task.CompletedTask; // For consistency with async signature

        if (errors.Count > 0)
        {
            return new ToolValidationResult
            {
                IsValid = false,
                Errors = errors,
                Warnings = warnings
            };
        }

        return new ToolValidationResult
        {
            IsValid = true,
            Warnings = warnings
        };
    }

    private static string ReplacePlaceholders(string template, string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var directoryPath = Path.GetDirectoryName(filePath) ?? string.Empty;

        return template
            .Replace("{FilePath}", $"\"{filePath}\"")
            .Replace("{FileName}", $"\"{fileName}\"")
            .Replace("{DirectoryPath}", $"\"{directoryPath}\"");
    }
}
