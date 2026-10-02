using HD2Mimnt.PluginSDK;

namespace HD2Mimnt.PluginHost;

/// <summary>
/// Implementation of IPluginLogger that sends logs via IPC
/// </summary>
public class PluginHostLogger : IPluginLogger
{
    private readonly Action<string, string, Exception?> _logCallback;

    public PluginHostLogger(Action<string, string, Exception?> logCallback)
    {
        _logCallback = logCallback;
    }

    public void Info(string message)
    {
        _logCallback("Info", message, null);
    }

    public void Warning(string message)
    {
        _logCallback("Warning", message, null);
    }

    public void Error(string message, Exception? exception = null)
    {
        _logCallback("Error", message, exception);
    }

    public void Debug(string message)
    {
        _logCallback("Debug", message, null);
    }
}

/// <summary>
/// Implementation of IPluginStorage with sandbox restrictions
/// </summary>
public class PluginHostStorage : IPluginStorage
{
    private readonly string _pluginDataDirectory;

    public PluginHostStorage(string pluginId)
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HD2Mimnt",
            "Plugins",
            pluginId);

        Directory.CreateDirectory(baseDir);
        _pluginDataDirectory = baseDir;
    }

    public string GetDataDirectory() => _pluginDataDirectory;

    public async Task<string> ReadFileAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var filePath = GetSafePath(fileName);
        return await File.ReadAllTextAsync(filePath, cancellationToken);
    }

    public async Task WriteFileAsync(string fileName, string content, CancellationToken cancellationToken = default)
    {
        var filePath = GetSafePath(fileName);
        await File.WriteAllTextAsync(filePath, content, cancellationToken);
    }

    public bool FileExists(string fileName)
    {
        var filePath = GetSafePath(fileName);
        return File.Exists(filePath);
    }

    public Task DeleteFileAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var filePath = GetSafePath(fileName);
        File.Delete(filePath);
        return Task.CompletedTask;
    }

    private string GetSafePath(string fileName)
    {
        // Prevent directory traversal
        var sanitized = Path.GetFileName(fileName);
        return Path.Combine(_pluginDataDirectory, sanitized);
    }
}

/// <summary>
/// Implementation of IPluginModAccess (read-only, via IPC)
/// </summary>
public class PluginHostModAccess : IPluginModAccess
{
    // TODO: Implement via IPC calls to main application
    // For now, throw not implemented

    public Task<PluginModInfo?> GetModAsync(Guid modId, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("ModAccess requires IPC implementation");
    }

    public Task<List<PluginModInfo>> GetAllModsAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("ModAccess requires IPC implementation");
    }

    public Task<List<PluginModInfo>> GetModsByCategoryAsync(string category, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("ModAccess requires IPC implementation");
    }

    public Task<List<PluginModInfo>> GetModsByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("ModAccess requires IPC implementation");
    }
}

/// <summary>
/// Implementation of IPluginConfiguration (stored locally)
/// </summary>
public class PluginHostConfiguration : IPluginConfiguration
{
    private readonly Dictionary<string, string> _config = new();
    private readonly string _configFilePath;

    public PluginHostConfiguration(string pluginId)
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HD2Mimnt",
            "Plugins",
            pluginId);

        Directory.CreateDirectory(baseDir);
        _configFilePath = Path.Combine(baseDir, "plugin.config.json");

        LoadConfig();
    }

    public string? Get(string key)
    {
        return _config.GetValueOrDefault(key);
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        _config[key] = value;
        await SaveConfigAsync(cancellationToken);
    }

    public IEnumerable<string> GetKeys()
    {
        return _config.Keys;
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _config.Remove(key);
        await SaveConfigAsync(cancellationToken);
    }

    private void LoadConfig()
    {
        if (File.Exists(_configFilePath))
        {
            var json = File.ReadAllText(_configFilePath);
            var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (loaded != null)
            {
                _config.Clear();
                foreach (var kvp in loaded)
                {
                    _config[kvp.Key] = kvp.Value;
                }
            }
        }
    }

    private async Task SaveConfigAsync(CancellationToken cancellationToken)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(_config);
        await File.WriteAllTextAsync(_configFilePath, json, cancellationToken);
    }
}

/// <summary>
/// Implementation of IPluginContext
/// </summary>
public class PluginHostContext : IPluginContext
{
    public IPluginLogger Logger { get; }
    public IPluginStorage Storage { get; }
    public IPluginModAccess ModAccess { get; }
    public IPluginConfiguration Configuration { get; }

    public PluginHostContext(
        IPluginLogger logger,
        IPluginStorage storage,
        IPluginModAccess modAccess,
        IPluginConfiguration configuration)
    {
        Logger = logger;
        Storage = storage;
        ModAccess = modAccess;
        Configuration = configuration;
    }
}
