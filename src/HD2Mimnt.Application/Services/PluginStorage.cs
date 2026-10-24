using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.PluginSDK;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Implementation of IPluginStorage that provides isolated file storage for plugins
/// </summary>
public class PluginStorage : IPluginStorage
{
    private readonly string _pluginId;
    private readonly string _dataDirectory;
    private readonly IPluginService _pluginService;

    public PluginStorage(string pluginId, string baseDirectory, IPluginService pluginService)
    {
        _pluginId = pluginId;
        _pluginService = pluginService;

        // Create isolated directory for this plugin
        _dataDirectory = Path.Combine(baseDirectory, "PluginData", SanitizePluginId(pluginId));

        if (!Directory.Exists(_dataDirectory))
        {
            Directory.CreateDirectory(_dataDirectory);
        }
    }

    public string GetDataDirectory()
    {
        return _dataDirectory;
    }

    public async Task<string> ReadFileAsync(string fileName, CancellationToken cancellationToken = default)
    {
        ValidateFileName(fileName);

        // Check permission
        if (!await _pluginService.CheckPermissionAsync(_pluginId, "storage.read", cancellationToken))
        {
            throw new UnauthorizedAccessException($"Plugin '{_pluginId}' does not have 'storage.read' permission");
        }

        var filePath = GetSafeFilePath(fileName);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File '{fileName}' not found in plugin storage");
        }

        return await File.ReadAllTextAsync(filePath, cancellationToken);
    }

    public async Task WriteFileAsync(string fileName, string content, CancellationToken cancellationToken = default)
    {
        ValidateFileName(fileName);

        // Check permission
        if (!await _pluginService.CheckPermissionAsync(_pluginId, "storage.write", cancellationToken))
        {
            throw new UnauthorizedAccessException($"Plugin '{_pluginId}' does not have 'storage.write' permission");
        }

        var filePath = GetSafeFilePath(fileName);
        var directory = Path.GetDirectoryName(filePath);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(filePath, content, cancellationToken);
    }

    public bool FileExists(string fileName)
    {
        ValidateFileName(fileName);
        var filePath = GetSafeFilePath(fileName);
        return File.Exists(filePath);
    }

    public async Task DeleteFileAsync(string fileName, CancellationToken cancellationToken = default)
    {
        ValidateFileName(fileName);

        // Check permission
        if (!await _pluginService.CheckPermissionAsync(_pluginId, "storage.write", cancellationToken))
        {
            throw new UnauthorizedAccessException($"Plugin '{_pluginId}' does not have 'storage.write' permission");
        }

        var filePath = GetSafeFilePath(fileName);

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    /// <summary>
    /// Get safe file path within plugin's data directory
    /// Prevents directory traversal attacks
    /// </summary>
    private string GetSafeFilePath(string fileName)
    {
        var filePath = Path.Combine(_dataDirectory, fileName);
        var fullPath = Path.GetFullPath(filePath);

        // Ensure the path is within the plugin's data directory
        if (!fullPath.StartsWith(_dataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Access denied: Path '{fileName}' is outside plugin data directory");
        }

        return fullPath;
    }

    /// <summary>
    /// Validate file name to prevent malicious paths
    /// </summary>
    private static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("File name cannot be empty", nameof(fileName));
        }

        // Check for invalid characters
        var invalidChars = Path.GetInvalidFileNameChars();
        if (fileName.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.IndexOfAny(invalidChars) >= 0))
        {
            throw new ArgumentException($"File name '{fileName}' contains invalid characters", nameof(fileName));
        }

        // Prevent absolute paths
        if (Path.IsPathRooted(fileName))
        {
            throw new ArgumentException($"File name '{fileName}' cannot be an absolute path", nameof(fileName));
        }
    }

    /// <summary>
    /// Sanitize plugin ID for use in file paths
    /// </summary>
    private static string SanitizePluginId(string pluginId)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Concat(pluginId.Select(c => invalidChars.Contains(c) ? '_' : c));
    }
}
