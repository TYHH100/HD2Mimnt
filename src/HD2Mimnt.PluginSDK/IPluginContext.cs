namespace HD2Mimnt.PluginSDK;

/// <summary>
/// Context provided to plugins by the host application
/// </summary>
public interface IPluginContext
{
    /// <summary>
    /// Logger for plugin messages
    /// </summary>
    IPluginLogger Logger { get; }

    /// <summary>
    /// Storage for plugin data
    /// </summary>
    IPluginStorage Storage { get; }

    /// <summary>
    /// Access to mod information
    /// </summary>
    IPluginModAccess ModAccess { get; }

    /// <summary>
    /// Plugin configuration management
    /// </summary>
    IPluginConfiguration Configuration { get; }
}

/// <summary>
/// Logger interface for plugins
/// </summary>
public interface IPluginLogger
{
    /// <summary>
    /// Log an informational message
    /// </summary>
    void Info(string message);

    /// <summary>
    /// Log a warning message
    /// </summary>
    void Warning(string message);

    /// <summary>
    /// Log an error message
    /// </summary>
    void Error(string message, Exception? exception = null);

    /// <summary>
    /// Log a debug message
    /// </summary>
    void Debug(string message);
}

/// <summary>
/// Storage interface for plugin data
/// </summary>
public interface IPluginStorage
{
    /// <summary>
    /// Get plugin data directory path
    /// </summary>
    string GetDataDirectory();

    /// <summary>
    /// Read a file from plugin storage
    /// </summary>
    Task<string> ReadFileAsync(string fileName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write a file to plugin storage
    /// </summary>
    Task WriteFileAsync(string fileName, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a file exists in plugin storage
    /// </summary>
    bool FileExists(string fileName);

    /// <summary>
    /// Delete a file from plugin storage
    /// </summary>
    Task DeleteFileAsync(string fileName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Mod access interface for plugins
/// </summary>
public interface IPluginModAccess
{
    /// <summary>
    /// Get mod by ID
    /// </summary>
    Task<PluginModInfo?> GetModAsync(Guid modId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all mods
    /// </summary>
    Task<List<PluginModInfo>> GetAllModsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get mods by category
    /// </summary>
    Task<List<PluginModInfo>> GetModsByCategoryAsync(string category, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get mods by tag
    /// </summary>
    Task<List<PluginModInfo>> GetModsByTagAsync(string tag, CancellationToken cancellationToken = default);
}

/// <summary>
/// Configuration interface for plugins
/// </summary>
public interface IPluginConfiguration
{
    /// <summary>
    /// Get a configuration value
    /// </summary>
    string? Get(string key);

    /// <summary>
    /// Set a configuration value
    /// </summary>
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all configuration keys
    /// </summary>
    IEnumerable<string> GetKeys();

    /// <summary>
    /// Delete a configuration value
    /// </summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}
