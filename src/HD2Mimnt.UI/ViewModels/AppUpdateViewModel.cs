using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// 应用自更新（"关于"页）：检查 GitHub Release（或 Codeberg 镜像）新版本、下载安装包并自动重启安装。
/// 检查/下载入口都来自 UI 命令或主窗口初始化，属性更新始终回到 UI 线程上下文。
/// </summary>
public partial class AppUpdateViewModel : ObservableObject
{
    private readonly IAppUpdateService _updateService;
    private readonly IBrowserLauncher _browserLauncher;
    private readonly BackupService _backupService;
    private readonly IConfirmationService _confirmationService;
    private readonly NotificationService _notificationService;
    private readonly SettingsService? _settings;
    private CancellationTokenSource? _operation;
    // 绑定初始化可能回写控件默认值；只持久化用户真正改变的值，避免把默认值写进设置库。
    private bool _lastAutoCheckOnStartup;
    private string _lastChannel;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private bool _isBackingUp;

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

    /// <summary>首选更新渠道；不可用时服务会自动改用另一个渠道。</summary>
    [ObservableProperty]
    private string _channel = AppUpdateChannels.GitHub;

    public IReadOnlyList<string> ChannelOptions => AppUpdateChannels.All;

    /// <summary>检测到新版本（手动或启动检查），由主窗口订阅弹通知。</summary>
    public event Action? UpdateAvailableDetected;

    /// <summary>安装脚本已启动，应用应立即退出，由主窗口订阅执行关闭。</summary>
    public event Action? InstallLaunched;

    public bool IsBusy => IsChecking || IsDownloading || IsBackingUp;

    public AppUpdateViewModel(IAppUpdateService updateService, IBrowserLauncher browserLauncher,
        BackupService backupService, IConfirmationService confirmationService,
        NotificationService notificationService, SettingsService? settings = null)
    {
        _updateService = updateService;
        _browserLauncher = browserLauncher;
        _backupService = backupService;
        _confirmationService = confirmationService;
        _notificationService = notificationService;
        _settings = settings;
        _lastAutoCheckOnStartup = settings?.GetSetting(SettingKeys.UpdatesCheckOnStartup, true) == true;
        _autoCheckOnStartup = _lastAutoCheckOnStartup;
        _lastChannel = AppUpdateChannels.Normalize(
            settings?.GetSetting(SettingKeys.UpdatesChannel, AppUpdateChannels.GitHub));
        _channel = _lastChannel;
    }

    partial void OnIsCheckingChanged(bool value) => OnPropertyChanged(nameof(IsBusy));
    partial void OnIsDownloadingChanged(bool value) => OnPropertyChanged(nameof(IsBusy));
    partial void OnIsBackingUpChanged(bool value) => OnPropertyChanged(nameof(IsBusy));

    partial void OnAutoCheckOnStartupChanged(bool value)
    {
        if (_settings == null || value == _lastAutoCheckOnStartup) return;
        _lastAutoCheckOnStartup = value;
        _ = _settings.SetSettingAsync(SettingKeys.UpdatesCheckOnStartup, value);
    }

    partial void OnChannelChanged(string value)
    {
        if (value == _lastChannel) return;
        _lastChannel = value;
        // 旧渠道的检查结果不再可信，清掉状态让用户按新渠道重新检查。
        HasUpdate = false;
        IsUpToDate = false;
        HasError = false;
        ErrorDetail = null;
        _currentInfo = null;
        if (_settings == null) return;
        _ = _settings.SetSettingAsync(SettingKeys.UpdatesChannel, value);
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

        // 更新会替换程序目录，先在动手前确认是否留一份数据库备份。
        var backupChoice = await _confirmationService.AskAsync(
            "Updates.BackupBeforeUpdateTitle",
            "Updates.BackupBeforeUpdateMessage",
            "Updates.BackupAndUpdate",
            "Updates.UpdateWithoutBackup");
        if (backupChoice == ConfirmationChoice.Cancel) return;

        HasError = false;
        ErrorDetail = null;
        var operation = new CancellationTokenSource();
        _operation = operation;
        try
        {
            if (backupChoice == ConfirmationChoice.Primary)
            {
                IsBackingUp = true;
                try
                {
                    // VACUUM INTO 实际同步执行，放到线程池避免备份大库时冻结界面。
                    var backup = await Task.Run(() => _backupService.CreateBackupAsync(operation.Token),
                        operation.Token);
                    _notificationService.ShowSuccess(
                        UiLocalization.Current.Format("Updates.BackupCreated", backup.Name),
                        UiLocalization.Current["Updates.NotificationTitle"]);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    // 备份失败不继续更新：用户选的是“先备份”，静默跳过会让保险失效。
                    HasError = true;
                    ErrorDetail = UiLocalization.Current.Format("Updates.BackupFailed", error.Message);
                    _notificationService.ShowError(ErrorDetail,
                        UiLocalization.Current["Updates.NotificationTitle"]);
                    return;
                }
                finally
                {
                    IsBackingUp = false;
                }
            }

            IsDownloading = true;
            DownloadPercentage = 0;
            var progress = new Progress<DownloadProgress>(p => DownloadPercentage = p.Percentage);
            var staging = await _updateService.PrepareAsync(info, progress, operation.Token);
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
            operation.Dispose();
            if (ReferenceEquals(_operation, operation)) _operation = null;
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
