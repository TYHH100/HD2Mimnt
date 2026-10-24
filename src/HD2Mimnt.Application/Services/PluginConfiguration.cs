using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.PluginSDK;
using System.Text.Json;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Implementation of IPluginConfiguration that manages plugin settings
/// </summary>
public class PluginConfiguration : IPluginConfiguration
{
    private readonly string _pluginId;
    private readonly IPluginService _pluginService;
    private readonly string _configFilePath;
    private readonly Dictionary<string, string> _configCache;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public PluginConfiguration(string pluginId, string baseDirectory, IPluginService pluginService)
    {
        _pluginId = pluginId;
        _pluginService = pluginService;

        var configDirectory = Path.Combine(baseDirectory, "PluginConfig");
        if (!Directory.Exists(configDirectory))
        {
            Directory.CreateDirectory(configDirectory);
        }

        _configFilePath = Path.Combine(configDirectory, $"{SanitizePluginId(pluginId)}.json");
        _configCache = LoadConfiguration();
    }

    public string? Get(string key)
    {
        ValidateKey(key);

        lock (_lock)
        {
            return _configCache.TryGetValue(key, out var value) ? value : null;
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);

        // Check permission
        if (!await _pluginService.CheckPermissionAsync(_pluginId, "config.write", cancellationToken))
        {
            throw new UnauthorizedAccessException($"Plugin '{_pluginId}' does not have 'config.write' permission");
        }

        lock (_lock)
        {
            _configCache[key] = value;
        }

        await SaveConfigurationAsync(cancellationToken);
    }

    public IEnumerable<string> GetKeys()
    {
        lock (_lock)
        {
            return _configCache.Keys.ToList();
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);

        // Check permission
        if (!await _pluginService.CheckPermissionAsync(_pluginId, "config.write", cancellationToken))
        {
            throw new UnauthorizedAccessException($"Plugin '{_pluginId}' does not have 'config.write' permission");
        }

        lock (_lock)
        {
            _configCache.Remove(key);
        }

        await SaveConfigurationAsync(cancellationToken);
    }

    private Dictionary<string, string> LoadConfiguration()
    {
        if (!File.Exists(_configFilePath))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            var json = File.ReadAllText(_configFilePath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch
        {
            // If config is corrupted, return empty dictionary
            return new Dictionary<string, string>();
        }
    }

    private async Task SaveConfigurationAsync(CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> configCopy;

        lock (_lock)
        {
            configCopy = new Dictionary<string, string>(_configCache);
        }

        var json = JsonSerializer.Serialize(configCopy, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        // Use semaphore to ensure only one thread writes to the file at a time
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            // Use FileStream with proper FileShare settings to prevent access conflicts
            using var stream = new FileStream(_configFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            using var writer = new StreamWriter(stream);
            await writer.WriteAsync(json);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Configuration key cannot be empty", nameof(key));
        }

        if (key.Length > 256)
        {
            throw new ArgumentException("Configuration key is too long (max 256 characters)", nameof(key));
        }
    }

    private static string SanitizePluginId(string pluginId)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Concat(pluginId.Select(c => invalidChars.Contains(c) ? '_' : c));
    }
}
