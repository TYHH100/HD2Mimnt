using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 设置服务
/// 负责应用设置的持久化存储和管理
/// </summary>
public class SettingsService
{
    private readonly string _settingsFilePath;
    private readonly ILogger<SettingsService>? _logger;
    private Dictionary<string, object> _settings;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public event EventHandler<SettingChangedEventArgs>? SettingChanged;

    public SettingsService(string appDataPath, ILogger<SettingsService>? logger = null)
    {
        _settingsFilePath = Path.Combine(appDataPath, "settings.json");
        _logger = logger;
        _settings = new Dictionary<string, object>();

        // 必须同步加载。绝不能写 LoadSettingsAsync().Wait()：App 启动时本服务在 UI 线程的
        // DI 解析阶段构造，此时 Avalonia 调度器尚未开始泵消息，异步续体会被投递到调度器
        // 并永久等待，造成启动死锁（转储中表现为 ctor → Task.Wait → AvaloniaSynchronizationContext.Wait）。
        LoadSettings();
    }

    /// <summary>同步读取内存设置，供启动期服务构造使用；不要同步等待异步读取。</summary>
    public T GetSetting<T>(string key, T defaultValue)
    {
        _lock.Wait();
        try { return ReadSetting(key, defaultValue); }
        finally { _lock.Release(); }
    }

    /// <summary>异步获取设置值。</summary>
    public async Task<T> GetSettingAsync<T>(string key, T defaultValue)
    {
        await _lock.WaitAsync();
        try
        {
            return ReadSetting(key, defaultValue);
        }
        finally
        {
            _lock.Release();
        }
    }

    private T ReadSetting<T>(string key, T defaultValue)
    {
        if (!_settings.TryGetValue(key, out var value)) return defaultValue;
        try
        {
            if (value is JsonElement jsonElement) return jsonElement.Deserialize<T>() ?? defaultValue;
            if (value is T typedValue) return typedValue;
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "设置值类型转换失败: {Key}", key);
            return defaultValue;
        }
    }

    /// <summary>
    /// 设置值
    /// </summary>
    public async Task SetSettingAsync<T>(string key, T value)
    {
        SettingChangedEventArgs change;
        await _lock.WaitAsync();
        try
        {
            var hadOldValue = _settings.TryGetValue(key, out var existing);
            var oldValue = existing;
            _settings[key] = value!;

            try
            {
                await SaveSettingsAsync();
            }
            catch
            {
                if (hadOldValue) _settings[key] = oldValue!;
                else _settings.Remove(key);
                throw;
            }

            change = new SettingChangedEventArgs
            {
                Key = key,
                OldValue = oldValue,
                NewValue = value
            };

            _logger?.LogInformation("设置已更新: {Key} = {Value}", key, value);
        }
        finally
        {
            _lock.Release();
        }
        // 持久化成功且释放闸门后通知，监听者可以重新读取设置。
        SettingChanged?.Invoke(this, change);
    }

    /// <summary>
    /// 批量获取所有设置
    /// </summary>
    public async Task<Dictionary<string, object>> GetAllSettingsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return new Dictionary<string, object>(_settings);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Publishes a related group of settings together, with one write and rollback on failure.</summary>
    public async Task SetSettingsAsync(IReadOnlyDictionary<string, object> values)
    {
        var changes = new List<SettingChangedEventArgs>();
        await _lock.WaitAsync();
        try
        {
            var previous = new Dictionary<string, object>(_settings);
            foreach (var (key, value) in values)
            {
                changes.Add(new SettingChangedEventArgs
                {
                    Key = key, OldValue = _settings.GetValueOrDefault(key), NewValue = value
                });
                _settings[key] = value;
            }
            try { await SaveSettingsAsync(); }
            catch { _settings = previous; throw; }
        }
        finally { _lock.Release(); }
        foreach (var change in changes) SettingChanged?.Invoke(this, change);
    }

    /// <summary>
    /// 重置所有设置为默认值
    /// </summary>
    public async Task ResetToDefaultAsync()
    {
        List<SettingChangedEventArgs> changes;
        await _lock.WaitAsync();
        try
        {
            var previous = new Dictionary<string, object>(_settings);
            _settings.Clear();
            try
            {
                await SaveSettingsAsync();
            }
            catch
            {
                _settings = previous;
                throw;
            }
            changes = FindChanges(previous, _settings);
            _logger?.LogInformation("所有设置已重置为默认值");
        }
        finally
        {
            _lock.Release();
        }
        foreach (var change in changes) SettingChanged?.Invoke(this, change);
    }

    /// <summary>
    /// 重置某个分类的设置
    /// </summary>
    public async Task ResetCategoryAsync(string category)
    {
        List<SettingChangedEventArgs> changes;
        await _lock.WaitAsync();
        try
        {
            var keysToRemove = _settings.Keys
                .Where(k => k.StartsWith(category + ".", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var previous = new Dictionary<string, object>(_settings);
            foreach (var key in keysToRemove) _settings.Remove(key);

            try
            {
                await SaveSettingsAsync();
            }
            catch
            {
                _settings = previous;
                throw;
            }
            changes = FindChanges(previous, _settings);
            _logger?.LogInformation("分类设置已重置: {Category}", category);
        }
        finally
        {
            _lock.Release();
        }
        foreach (var change in changes) SettingChanged?.Invoke(this, change);
    }

    /// <summary>
    /// 从文件加载设置
    /// </summary>
    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

                if (loaded != null)
                {
                    _settings = new Dictionary<string, object>();
                    foreach (var kvp in loaded)
                    {
                        _settings[kvp.Key] = kvp.Value;
                    }
                }

                _logger?.LogInformation("设置已从文件加载: {Path}", _settingsFilePath);
            }
            else
            {
                _logger?.LogInformation("设置文件不存在，使用默认值");
                _settings = new Dictionary<string, object>();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "加载设置文件失败");
            _settings = new Dictionary<string, object>();
        }
    }

    private Task LoadSettingsAsync()
    {
        LoadSettings();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reload settings after an external restore operation.
    /// </summary>
    public async Task ReloadAsync()
    {
        List<SettingChangedEventArgs> changes;
        await _lock.WaitAsync();
        try
        {
            var previous = new Dictionary<string, object>(_settings);
            await LoadSettingsAsync();
            changes = FindChanges(previous, _settings);
        }
        finally
        {
            _lock.Release();
        }
        foreach (var change in changes) SettingChanged?.Invoke(this, change);
    }

    private static List<SettingChangedEventArgs> FindChanges(
        IReadOnlyDictionary<string, object> previous, IReadOnlyDictionary<string, object> current)
    {
        var changes = new List<SettingChangedEventArgs>();
        foreach (var key in previous.Keys.Union(current.Keys))
        {
            var hadOld = previous.TryGetValue(key, out var oldValue);
            var hasNew = current.TryGetValue(key, out var newValue);
            if (hadOld == hasNew && JsonSerializer.Serialize(oldValue) == JsonSerializer.Serialize(newValue))
                continue;
            changes.Add(new SettingChangedEventArgs { Key = key, OldValue = oldValue, NewValue = newValue });
        }
        return changes;
    }

    /// <summary>
    /// 保存设置到文件
    /// </summary>
    private async Task SaveSettingsAsync()
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            var json = JsonSerializer.Serialize(_settings, options);
            await File.WriteAllTextAsync(_settingsFilePath, json);

            _logger?.LogDebug("设置已保存到文件: {Path}", _settingsFilePath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "保存设置文件失败");
            throw;
        }
    }
}

/// <summary>
/// 设置键常量
/// </summary>
public static class SettingKeys
{
    // 游戏相关
    public const string GamePath = "Game.Path";
    public const string GameAutoDetect = "Game.AutoDetect";

    // 配置文件相关
    public const string ProfilesAutoSwitchOnSelect = "Profiles.AutoSwitchOnSelect";
    public const string ProfilesAutoCheckConflicts = "Profiles.AutoCheckConflicts";

    // 部署相关
    public const string DefaultDeploymentMethod = "Deployment.DefaultMethod";
    public const string DefaultDeploymentOrder = "Deployment.DefaultOrder";
    public const string DeploymentCheckBeforeDeploy = "Deployment.CheckBefore";
    public const string DeploymentAutoBackup = "Deployment.AutoBackup";

    // 模组库相关
    public const string DefaultFilterCategory = "Library.DefaultFilterCategory";
    public const string ShowModThumbnails = "Library.ShowThumbnails";

    // 下载相关
    public const string DownloadParallelSegments = "Download.ParallelSegments";
    public const string DownloadSegmentSizeMiB = "Download.SegmentSizeMiB";
    public const string DownloadMaxRetries = "Download.MaxRetries";
    public const string DownloadRetryDelayMs = "Download.RetryDelayMs";
    public const string DownloadEnableResume = "Download.EnableResume";

    // UI 相关
    public const string Language = "UI.Language";
    public const string Theme = "UI.Theme";
    public const string ListDensity = "UI.ListDensity";
    public const string BackgroundImagePath = "UI.BackgroundImagePath";
    public const string BackgroundImageOpacity = "UI.BackgroundOpacity";
    public const string BackgroundBlur = "UI.BackgroundBlur";
    public const string CardOpacity = "UI.CardOpacity";
    public const string WindowWidth = "UI.WindowWidth";
    public const string WindowHeight = "UI.WindowHeight";
    public const string WindowMaximized = "UI.WindowMaximized";

    // 性能相关
    public const string ImageCacheSize = "Performance.ImageCacheSize";
    public const string EnableAnimations = "Performance.EnableAnimations";
    public const string VirtualizationThreshold = "Performance.VirtualizationThreshold";

    // 高级设置
    public const string EnableLogging = "Advanced.EnableLogging";
    public const string LogLevel = "Advanced.LogLevel";
    public const string ConcurrentImports = "Advanced.ConcurrentImports";
}

/// <summary>
/// 设置变化事件参数
/// </summary>
public class SettingChangedEventArgs : EventArgs
{
    public string Key { get; set; } = string.Empty;
    public object? OldValue { get; set; }
    public object? NewValue { get; set; }
}
