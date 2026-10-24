using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using HD2Mimnt.Application.Localization;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Localization;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 持久化语言选择并实时发布资源变更；所有查找使用明确的 Culture，
/// 不修改线程/进程 Culture，避免影响文件格式、协议和已有后台任务。
/// </summary>
public sealed class LocalizationService : ILocalizationService, IDisposable
{
    private readonly SettingsService _settings;
    private readonly ISystemLanguageProvider _systemLanguage;
    private readonly TranslationCatalog _catalog;
    private readonly ILogger<LocalizationService>? _logger;
    private readonly TimeSpan _monitorInterval;
    private readonly object _gate = new();
    private LanguageState _state;
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private bool _disposed;

    public LocalizationService(
        SettingsService settings, ISystemLanguageProvider systemLanguage,
        TranslationCatalog catalog, ILogger<LocalizationService>? logger = null,
        TimeSpan? monitorInterval = null)
    {
        _settings = settings;
        _systemLanguage = systemLanguage;
        _catalog = catalog;
        _logger = logger;
        _monitorInterval = monitorInterval ?? TimeSpan.FromSeconds(3);
        if (_monitorInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(monitorInterval));

        // 启动阶段同步读取已加载的设置，不阻塞等待异步 UI 续体。
        var selected = NormalizeLanguage(_settings.GetSetting(SettingKeys.Language, LanguageCodes.Automatic))
            ?? LanguageCodes.Automatic;
        _state = new(selected, ResolveCulture(selected));
        _settings.SettingChanged += OnSettingChanged;
    }

    public IReadOnlyList<LanguageInfo> SupportedLanguages => _catalog.SupportedLanguages;
    public string SelectedLanguage => State.SelectedLanguage;
    public bool IsAutomatic => SelectedLanguage == LanguageCodes.Automatic;
    public string CurrentLanguage => State.Culture.Name;
    public CultureInfo CurrentCulture => State.Culture;
    public string this[string key] => GetString(key);
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<LanguageChangedEventArgs>? LanguageChanged;

    private LanguageState State => Volatile.Read(ref _state);

    public string GetString(string key) => _catalog.GetString(key, State.Culture);

    public string Format(string key, params object?[] arguments)
    {
        var culture = State.Culture;
        return string.Format(culture, _catalog.GetString(key, culture), arguments);
    }

    public async Task SetLanguageAsync(string languageCode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var selected = NormalizeLanguage(languageCode)
            ?? throw new ArgumentException("不支持的语言代码。", nameof(languageCode));
        // 只有成功写入后才应用；失败保留当前语言。
        await _settings.SetSettingAsync(SettingKeys.Language, selected).ConfigureAwait(false);
    }

    public void RefreshSystemLanguage()
    {
        ApplyLanguage(null);
    }

    /// <summary>应用启动时调用一次；自动模式下持续检测系统显示语言。</summary>
    public void StartMonitoring()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_monitorTask != null) return;
            _monitorCancellation = new CancellationTokenSource();
            var token = _monitorCancellation.Token;
            _monitorTask = Task.Run(() => MonitorAsync(token));
        }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_monitorInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (IsAutomatic) RefreshSystemLanguage();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void OnSettingChanged(object? sender, SettingChangedEventArgs change)
    {
        if (change.Key != SettingKeys.Language) return;
        var code = change.NewValue switch
        {
            string value => value,
            JsonElement { ValueKind: JsonValueKind.String } value => value.GetString(),
            _ => null
        };
        ApplyLanguage(NormalizeLanguage(code) ?? LanguageCodes.Automatic);
    }

    private void ApplyLanguage(string? selected)
    {
        LanguageState previous;
        LanguageState current;
        lock (_gate)
        {
            if (_disposed) return;
            previous = _state;
            selected ??= previous.SelectedLanguage;
            current = new(selected, ResolveCulture(selected));
            if (previous == current) return;
            Volatile.Write(ref _state, current);
        }

        // 状态与资源已经切换，监听者即刻取得新值，无需重启。
        PropertyChanged?.Invoke(this, new(nameof(SelectedLanguage)));
        PropertyChanged?.Invoke(this, new(nameof(IsAutomatic)));
        PropertyChanged?.Invoke(this, new(nameof(CurrentLanguage)));
        PropertyChanged?.Invoke(this, new(nameof(CurrentCulture)));
        PropertyChanged?.Invoke(this, new("Item[]"));
        LanguageChanged?.Invoke(this, new(previous.SelectedLanguage, current.SelectedLanguage,
            previous.Culture, current.Culture));
    }

    private string? NormalizeLanguage(string? code)
    {
        code = code?.Trim();
        if (string.Equals(code, LanguageCodes.Automatic, StringComparison.OrdinalIgnoreCase))
            return LanguageCodes.Automatic;
        return SupportedLanguages.FirstOrDefault(language =>
            string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase))?.Code;
    }

    private CultureInfo ResolveCulture(string selected)
    {
        if (selected != LanguageCodes.Automatic) return CultureInfo.GetCultureInfo(selected);
        CultureInfo system;
        try { system = _systemLanguage.GetCurrentCulture(); }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "读取系统显示语言失败，使用英语");
            return CultureInfo.GetCultureInfo(LanguageCodes.English);
        }

        for (var culture = system; !string.IsNullOrEmpty(culture.Name); culture = culture.Parent)
        {
            var exact = SupportedLanguages.FirstOrDefault(language =>
                string.Equals(language.Code, culture.Name, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return CultureInfo.GetCultureInfo(exact.Code);
            // 区分简繁脚本；香港、澳门等地区通过父 Culture 匹配 zh-Hant。
            if (culture.Name.Equals("zh-Hant", StringComparison.OrdinalIgnoreCase))
                return CultureInfo.GetCultureInfo(LanguageCodes.TraditionalChinese);
            if (culture.Name.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase) ||
                culture.Name.Equals("zh", StringComparison.OrdinalIgnoreCase))
                return CultureInfo.GetCultureInfo(LanguageCodes.SimplifiedChinese);
            var sameLanguage = SupportedLanguages.FirstOrDefault(language =>
                CultureInfo.GetCultureInfo(language.Code).Name.Split('-')[0]
                    .Equals(culture.Name, StringComparison.OrdinalIgnoreCase));
            if (sameLanguage != null) return CultureInfo.GetCultureInfo(sameLanguage.Code);
        }
        return CultureInfo.GetCultureInfo(LanguageCodes.English);
    }

    public void Dispose()
    {
        Task? monitor;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _settings.SettingChanged -= OnSettingChanged;
            _monitorCancellation?.Cancel();
            monitor = _monitorTask;
        }
        // 检测循环始终在后台且不捕获 UI 上下文，退出时等待计时器清理完毕。
        monitor?.GetAwaiter().GetResult();
        _monitorCancellation?.Dispose();
    }

    private sealed record LanguageState(string SelectedLanguage, CultureInfo Culture);
}
