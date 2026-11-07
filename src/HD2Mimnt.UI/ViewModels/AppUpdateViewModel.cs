using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// 应用自更新（"关于"页）：检查 GitHub Release 新版本、下载安装包并自动重启安装。
/// 检查/下载入口都来自 UI 命令或主窗口初始化，属性更新始终回到 UI 线程上下文。
/// </summary>
public partial class AppUpdateViewModel : ObservableObject
{
    private readonly IAppUpdateService _updateService;
    private readonly IBrowserLauncher _browserLauncher;
    private readonly SettingsService? _settings;
    private CancellationTokenSource? _operation;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadPercentage;

    [ObservableProperty]
    private bool _hasUpdate;

    [ObservableProperty]
    private bool _hasDirectAsset;

    [ObservableProperty]
    private bool _isUpToDate;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string? _errorDetail;

    [ObservableProperty]
    private string? _latestVersion;

    [ObservableProperty]
    private string? _changelog;

    [ObservableProperty]
    private string _releaseUrl = AppUpdateConstants.ReleasePageUrl;

    [ObservableProperty]
    private bool _autoCheckOnStartup;

    /// <summary>检测到新版本（手动或启动检查），由主窗口订阅弹通知。</summary>
    public event Action? UpdateAvailableDetected;

    /// <summary>安装脚本已启动，应用应立即退出，由主窗口订阅执行关闭。</summary>
    public event Action? InstallLaunched;

    public bool IsBusy => IsChecking || IsDownloading;

    public AppUpdateViewModel(IAppUpdateService updateService, IBrowserLauncher browserLauncher,
        SettingsService? settings = null)
    {
        _updateService = updateService;
        _browserLauncher = browserLauncher;
        _settings = settings;
        _autoCheckOnStartup = settings?.GetSetting(SettingKeys.UpdatesCheckOnStartup, false) == true;
    }

    partial void OnIsCheckingChanged(bool value) => OnPropertyChanged(nameof(IsBusy));
    partial void OnIsDownloadingChanged(bool value) => OnPropertyChanged(nameof(IsBusy));

    partial void OnAutoCheckOnStartupChanged(bool value)
    {
        if (_settings == null) return;
        _ = _settings.SetSettingAsync(SettingKeys.UpdatesCheckOnStartup, value);
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (IsBusy) return;
        await RunCheckAsync(quiet: false);
    }

    /// <summary>启动时按设置的静默检查：关闭开关直接返回，失败不提示，仅发现新版本时通知。</summary>
    public async Task CheckOnStartupAsync()
    {
        if (!AutoCheckOnStartup || IsBusy) return;
        await RunCheckAsync(quiet: true);
    }

    [RelayCommand]
    private async Task DownloadAndInstallAsync()
    {
        if (IsBusy || _currentInfo is not { } info) return;
        HasError = false;
        ErrorDetail = null;
        _operation = new CancellationTokenSource();
        IsDownloading = true;
        DownloadPercentage = 0;
        try
        {
            var progress = new Progress<DownloadProgress>(p => DownloadPercentage = p.Percentage);
            var staging = await _updateService.PrepareAsync(info, progress, _operation.Token);
            _updateService.LaunchPendingInstaller(staging);
            InstallLaunched?.Invoke();
        }
        catch (OperationCanceledException)
        {
            DownloadPercentage = 0;
        }
        catch (Exception error)
        {
            HasError = true;
            ErrorDetail = error.Message;
        }
        finally
        {
            IsDownloading = false;
            _operation.Dispose();
            _operation = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        HasError = false;
        _operation?.Cancel();
    }

    [RelayCommand]
    private void OpenReleasePage() => _browserLauncher.Open(ReleaseUrl);

    private AppUpdateInfo? _currentInfo;

    private async Task RunCheckAsync(bool quiet)
    {
        HasError = false;
        ErrorDetail = null;
        IsUpToDate = false;
        if (!quiet) IsChecking = true;
        _operation = new CancellationTokenSource();
        try
        {
            var result = await _updateService.CheckAsync(_operation.Token);
            if (result.Info is { } info)
            {
                _currentInfo = info;
                LatestVersion = info.Version;
                Changelog = string.IsNullOrWhiteSpace(info.ChangelogBody) ? null : info.ChangelogBody;
                ReleaseUrl = info.ReleaseUrl;
                HasDirectAsset = !string.IsNullOrEmpty(info.AssetUrl);
            }
            HasUpdate = result.HasUpdate;
            if (!result.HasUpdate) IsUpToDate = true;
            else UpdateAvailableDetected?.Invoke();
        }
        catch (OperationCanceledException)
        {
            _operation.Dispose();
            _operation = null;
        }
        catch (Exception error) when (!quiet)
        {
            HasError = true;
            ErrorDetail = error.Message;
        }
        catch (Exception)
        {
            // 启动静默检查失败不打扰用户。
        }
        finally
        {
            IsChecking = false;
            if (_operation is { } operation)
            {
                _operation = null;
                operation.Dispose();
            }
        }
    }
}
