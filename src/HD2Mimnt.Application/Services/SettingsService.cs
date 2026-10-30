using Microsoft.Extensions.Logging;
using System.Text.Json;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 设置服务
/// 负责应用设置的持久化存储和管理
/// </summary>
public class SettingsService
{
    private readonly string _legacySettingsPath;
    private readonly ISettingsRepository _repository;
    private readonly ILogger<SettingsService>? _logger;
    // 已发布的字典只读；写入成功后整体替换，读取不等待可能需要 UI 续体的持久化闸门。
    private Dictionary<string, object> _settings;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public event EventHandler<SettingChangedEventArgs>? SettingChanged;

    public SettingsService(string appDataPath, ILogger<SettingsService>? logger = null)
        : this(new SettingsRepository(Path.Combine(appDataPath, "hd2mimnt.db")), appDataPath, logger)
    {
    }

    public SettingsService(ISettingsRepository repository, string appDataPath, ILogger<SettingsService>? logger = null)
    {
        _repository = repository;
        _legacySettingsPath = Path.Combine(appDataPath, "settings.json");
        _logger = logger;
        _settings = new Dictionary<string, object>();

        // 必须同步加载。绝不能写 LoadSettingsAsync().Wait()：App 启动时本服务在 UI 线程的
        // DI 解析阶段构造，此时 Avalonia 调度器尚未开始泵消息，异步续体会被投递到调度器
        // 并永久等待，造成启动死锁（转储中表现为 ctor → Task.Wait → AvaloniaSynchronizationContext.Wait）。
        LoadSettings();
    }

    /// <summary>同步读取内存设置，供启动期服务构造使用；不要同步等待异步读取。</summary>
    public T GetSetting<T>(string key, T defaultValue)
        => ReadSetting(Volatile.Read(ref _settings), key, defaultValue);

    /// <summary>异步获取设置值。</summary>
    public Task<T> GetSettingAsync<T>(string key, T defaultValue)
        => Task.FromResult(GetSetting(key, defaultValue));

    /// <summary>当前启用的游戏来源；旧设置没有该键时按官方安装处理。</summary>
    public GameInstallationMode GetGameInstallationMode()
        => Enum.TryParse<GameInstallationMode>(
               GetSetting(SettingKeys.GameMode, nameof(GameInstallationMode.Official)), true, out var mode)
            ? mode
            : GameInstallationMode.Official;

    /// <summary>业务服务统一读取的游戏目录；HellForge 模式解析为安装根下的 game 副本。</summary>
    public string GetEffectiveGamePath()
        => GetGameInstallationMode() == GameInstallationMode.HellForge
            ? GameInstallationPaths.ResolveHellForgeGameDirectory(
                  GetSetting(SettingKeys.HellForgePath, string.Empty)) ?? string.Empty
            : GetSetting(SettingKeys.GamePath, string.Empty);

    private T ReadSetting<T>(IReadOnlyDictionary<string, object> settings, string key, T defaultValue)
    {
        if (!settings.TryGetValue(key, out var value)) return defaultValue;
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
            var oldValue = _settings.GetValueOrDefault(key);
            var next = new Dictionary<string, object>(_settings) { [key] = value! };
            await SaveSettingsAsync(next, new[] { key }, Array.Empty<string>());
            Volatile.Write(ref _settings, next);

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
    public Task<Dictionary<string, object>> GetAllSettingsAsync()
        => Task.FromResult(new Dictionary<string, object>(Volatile.Read(ref _settings)));

    /// <summary>Publishes a related group of settings together, with one write and rollback on failure.</summary>
    public async Task SetSettingsAsync(IReadOnlyDictionary<string, object> values)
    {
        var changes = new List<SettingChangedEventArgs>();
        await _lock.WaitAsync();
        try
        {
            var next = new Dictionary<string, object>(_settings);
            foreach (var (key, value) in values)
            {
                changes.Add(new SettingChangedEventArgs
                {
                    Key = key, OldValue = _settings.GetValueOrDefault(key), NewValue = value
                });
                next[key] = value;
            }
            await SaveSettingsAsync(next, values.Keys, Array.Empty<string>());
            Volatile.Write(ref _settings, next);
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
            var previous = _settings;
            var next = new Dictionary<string, object>();
            await _repository.ResetAsync(null);
            Volatile.Write(ref _settings, next);
            changes = FindChanges(previous, next);
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
        => await ResetCategoriesAsync(category);

    /// <summary>同一个设置标签中的多个分类在同一事务中恢复默认。</summary>
    public async Task ResetCategoriesAsync(params string[] categories)
    {
        if (categories.Length == 0) return;
        List<SettingChangedEventArgs> changes;
        await _lock.WaitAsync();
        try
        {
            var keysToRemove = _settings.Keys
                .Where(k => categories.Any(category => k.StartsWith(category + ".", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var previous = _settings;
            var next = new Dictionary<string, object>(previous);
            foreach (var key in keysToRemove) next.Remove(key);

            if (categories.Length == 1) await _repository.ResetAsync(categories[0]);
            else await _repository.ResetCategoriesAsync(categories);
            Volatile.Write(ref _settings, next);
            changes = FindChanges(previous, next);
            _logger?.LogInformation("分类设置已重置: {Category}", string.Join(", ", categories));
        }
        finally
        {
            _lock.Release();
        }
        foreach (var change in changes) SettingChanged?.Invoke(this, change);
    }

    /// <summary>
    /// 从数据库加载设置；旧 JSON 只在尚未迁移时导入。
    /// </summary>
    private void LoadSettings()
    {
        try
        {
            if (!_repository.IsLegacyMigrationComplete())
            {
                var legacy = File.Exists(_legacySettingsPath)
                    ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(_legacySettingsPath))
                        ?? throw new InvalidDataException("旧设置文件无效。")
                    : new Dictionary<string, JsonElement>();
                _repository.ImportLegacy(legacy.ToDictionary(pair => pair.Key, pair => pair.Value.GetRawText()));
            }

            var loaded = _repository.ReadAll().ToDictionary(pair => pair.Key,
                pair => (object)JsonSerializer.Deserialize<JsonElement>(pair.Value));
            Volatile.Write(ref _settings, loaded);
            // 标记与导入在同一事务提交。即使删除失败，重置后也不会重新导入旧值。
            try
            {
                File.Delete(_legacySettingsPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _logger?.LogWarning(ex, "旧设置已迁移，但文件暂时无法删除: {Path}", _legacySettingsPath); }
            _logger?.LogInformation("设置已从数据库加载");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "加载或迁移设置失败，保留原数据");
            throw;
        }
    }

    /// <summary>
    /// Reload settings after an external restore operation.
    /// </summary>
    public Task ReloadAsync() => ReloadAfterRestoreAsync(() => Task.CompletedTask);

    /// <summary>恢复覆盖与设置写入共用闸门，避免后台保存落到正在替换的数据库。</summary>
    internal async Task ReloadAfterRestoreAsync(Func<Task> restore)
    {
        List<SettingChangedEventArgs> changes;
        await _lock.WaitAsync();
        try
        {
            var previous = new Dictionary<string, object>(_settings);
            await restore();
            LoadSettings();
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
    /// 只提交本次变更的键，避免其他服务实例的设置被旧内存快照覆盖。
    /// </summary>
    private async Task SaveSettingsAsync(IReadOnlyDictionary<string, object> settings,
        IEnumerable<string> updatedKeys, IReadOnlyCollection<string> removedKeys)
    {
        try
        {
            var values = updatedKeys.ToDictionary(key => key, key => JsonSerializer.Serialize(settings[key]));
            await _repository.ApplyAsync(values, removedKeys);
            _logger?.LogDebug("设置已保存到数据库");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "保存设置到数据库失败");
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
    public const string GameMode = "Game.Mode";
    public const string HellForgePath = "Game.HellForgePath";

    // 配置文件相关
    public const string ProfilesAutoSwitchOnSelect = "Profiles.AutoSwitchOnSelect";
    public const string ProfilesAutoCheckConflicts = "Profiles.AutoCheckConflicts";
    public const string ProfilesLockLoaders = "Profiles.LockLoaders";
    public const string ProfilesAutoAddImportedMods = "Profiles.AutoAddImportedMods";
    public const string ProfilesChooseGroupWhenAdding = "Profiles.ChooseGroupWhenAdding";
    public const string ProfilesPinExpandedGroups = "Profiles.PinExpandedGroups";

    // 部署相关
    public const string DefaultDeploymentMethod = "Deployment.DefaultMethod";
    public const string DefaultDeploymentOrder = "Deployment.DefaultOrder";
    public const string DeploymentCheckBeforeDeploy = "Deployment.CheckBefore";
    public const string DeploymentAutoBackup = "Deployment.AutoBackup";

    // 搜索相关
    public const string SearchFuzzy = "Search.Fuzzy";
    public const string SearchPinyin = "Search.Pinyin";

    // 模组库相关
    public const string DefaultFilterCategory = "Library.DefaultFilterCategory";
    public const string ShowModThumbnails = "Library.ShowThumbnails";
    public const string ImportAutoClose = "Library.ImportAutoClose";
    public const string KeepModVersions = "Library.KeepModVersions";
    public const string AllowDuplicateImports = "Library.AllowDuplicateImports";

    // 兼容性：耗时的启动检查需要用户明确开启。
    public const string CompatibilityCheckOnStartup = "Compatibility.CheckOnStartup";
    public const string CompatibilityEnableBatchRepair = "Compatibility.EnableBatchRepair";
    public const string CompatibilityAutoCheckImport = "Compatibility.AutoCheckImport";

    // 下载相关
    public const string NexusOAuthTestClientId = "Nexus.OAuthTestClientId";
    public const string DownloadParallelSegments = "Download.ParallelSegments";
    public const string DownloadSegmentSizeMiB = "Download.SegmentSizeMiB";
    public const string DownloadMaxRetries = "Download.MaxRetries";
    public const string DownloadRetryDelayMs = "Download.RetryDelayMs";
    public const string DownloadEnableResume = "Download.EnableResume";

    // UI 相关
    public const string Language = "UI.Language";
    public const string HiddenEntries = "UI.HiddenEntries";
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

    // 清单相关
    public const string ForceManifestV1 = "Manifest.ForceV1";
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
