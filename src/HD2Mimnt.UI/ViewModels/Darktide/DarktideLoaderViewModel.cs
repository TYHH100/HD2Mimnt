using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Application.Services.Darktide;
using HD2Mimnt.Core.Darktide;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Darktide;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels.Darktide;

public partial class DarktideLoaderViewModel : DarktidePageViewModelBase
{
    private readonly DarktideLoaderService _loader;
    private readonly DarktideManagerService _manager;
    private readonly IDarktideDialogService _dialogs;
    private readonly IBrowserLauncher _browser;
    private readonly TaskService _tasks;
    private DarktideReleaseInfo? _release;

    [ObservableProperty] private bool _loaderReady;
    [ObservableProperty] private bool _gameValid;
    [ObservableProperty] private bool _dmfMissing;
    [ObservableProperty] private string _gamePathText = "";
    [ObservableProperty] private string _loaderStatusText = "";
    [ObservableProperty] private string _dmfStatusText = "";
    [ObservableProperty] private string _patcherStatusText = "";
    [ObservableProperty] private string _patchStateText = "";
    [ObservableProperty] private string _alternativeText = "";
    [ObservableProperty] private bool _hasAlternative;
    [ObservableProperty] private string _recordedVersion = "";
    [ObservableProperty] private string _recordedAt = "";
    [ObservableProperty] private string _latestVersion = "";
    [ObservableProperty] private string _latestPublished = "";
    [ObservableProperty] private string _packageSummary = "";
    [ObservableProperty] private bool _hasRelease;
    [ObservableProperty] private bool _canInstallFull;
    [ObservableProperty] private bool _canInstallUpdateOnly;
    [ObservableProperty] private bool _hasProgress;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _autoPackageText = "";

    public DarktideLoaderViewModel(DarktideLoaderService loader, DarktideManagerService manager,
        IDarktideDialogService dialogs, IBrowserLauncher browser, TaskService tasks,
        NotificationService notifications) : base(notifications)
    {
        _loader = loader;
        _manager = manager;
        _dialogs = dialogs;
        _browser = browser;
        _tasks = tasks;
    }

    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
        => await RunAsync(LoadStatusAsync, failTitleKey: "Darktide.Loader.Title");

    private async Task LoadStatusAsync(CancellationToken cancellationToken)
    {
        var state = await Task.Run(_loader.GetGameState, cancellationToken);
        GameValid = state.RootPath != null && state.Status != DarktideGameStatus.InvalidPath;
        LoaderReady = state.Status == DarktideGameStatus.LoaderInstalled;
        GamePathText = state.RootPath ?? UiLocalization.Current["Darktide.Game.NotSet"];
        LoaderStatusText = state.Status switch
        {
            DarktideGameStatus.LoaderInstalled => UiLocalization.Current["Darktide.Loader.StatusInstalled"],
            DarktideGameStatus.LoaderIncomplete => UiLocalization.Current["Darktide.Loader.StatusIncomplete"],
            DarktideGameStatus.GameWithoutLoader => UiLocalization.Current["Darktide.Loader.StatusMissing"],
            DarktideGameStatus.InvalidPath => UiLocalization.Current["Darktide.Loader.StatusInvalid"],
            _ => UiLocalization.Current["Darktide.Loader.StatusNotConfigured"]
        };
        DmfStatusText = state.HasDmf
            ? UiLocalization.Current["Darktide.Loader.Present"]
            : UiLocalization.Current["Darktide.Loader.Absent"];
        DmfMissing = !state.HasDmf
                     && state.Status is DarktideGameStatus.LoaderInstalled or DarktideGameStatus.LoaderIncomplete;
        PatcherStatusText = state.HasPatcher
            ? UiLocalization.Current["Darktide.Loader.Present"]
            : UiLocalization.Current["Darktide.Loader.Absent"];
        PatchStateText = state.HasToggleScript
            ? UiLocalization.Current["Darktide.Loader.PatchUnknown"]
            : UiLocalization.Current["Darktide.Loader.ToggleMissing"];
        HasAlternative = state.AlternativeLoader != null;
        AlternativeText = state.AlternativeLoader == null ? ""
            : UiLocalization.Current.Format("Darktide.Loader.AlternativeDetected", state.AlternativeLoader);
        RecordedVersion = string.IsNullOrWhiteSpace(_loader.GetRecordedVersionTag())
            ? UiLocalization.Current["Darktide.Loader.VersionUnknown"] : _loader.GetRecordedVersionTag();
        var recordedAt = _loader.GetRecordedInstallTime();
        RecordedAt = DateTimeOffset.TryParse(recordedAt, out var recordedAtValue)
            ? recordedAtValue.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : UiLocalization.Current["Darktide.Loader.VersionUnknown"];
        UpdateReleaseText();
    }

    private void UpdateReleaseText()
    {
        HasRelease = _release != null && _release.Error == null;
        LatestVersion = _release == null ? UiLocalization.Current["Darktide.Loader.NotChecked"]
            : string.IsNullOrWhiteSpace(_release.Tag) ? UiLocalization.Current["Darktide.Loader.VersionUnknown"] : _release.Tag;
        LatestPublished = _release?.PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
        CanInstallFull = HasRelease && _release!.HasFullPackage;
        CanInstallUpdateOnly = HasRelease && _release!.HasUpdateOnlyPackage;
        PackageSummary = !HasRelease ? "" : string.Join(" · ", _release!.Packages
            .GroupBy(package => package.Type)
            .Select(group => group.Key == DarktideReleasePackageType.Full
                ? UiLocalization.Current["Darktide.Loader.PackageFull"]
                : UiLocalization.Current["Darktide.Loader.PackageUpdateOnly"]));
        AutoPackageText = !HasRelease ? ""
            : LoaderReady && _release!.HasUpdateOnlyPackage
                ? UiLocalization.Current["Darktide.Loader.AutoUpdateOnly"]
                : _release!.HasFullPackage ? UiLocalization.Current["Darktide.Loader.AutoFull"] : "";
    }

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        await RunAsync(async cancellationToken =>
        {
            var release = await _loader.CheckLatestAsync(cancellationToken);
            _release = release;
            UpdateReleaseText();
            if (release.Error != null) throw new InvalidOperationException(release.Error);
        }, failTitleKey: "Darktide.Loader.Title");
    }

    [RelayCommand]
    private async Task InstallLatestAsync()
    {
        if (_release == null) return;
        var type = LoaderReady && _release.HasUpdateOnlyPackage
            ? DarktideReleasePackageType.UpdateOnly : DarktideReleasePackageType.Full;
        await RunInstallAsync(type);
    }

    [RelayCommand]
    private async Task InstallFullAsync() => await RunInstallAsync(DarktideReleasePackageType.Full);

    [RelayCommand]
    private async Task InstallUpdateOnlyAsync() => await RunInstallAsync(DarktideReleasePackageType.UpdateOnly);

    private async Task RunInstallAsync(DarktideReleasePackageType type)
    {
        var release = _release;
        if (release == null) return;
        var package = release.Packages.FirstOrDefault(item => item.Type == type);
        if (package == null)
        {
            Notifications.ShowError(UiLocalization.Current["Darktide.Loader.ReleaseUnavailable"],
                UiLocalization.Current["Darktide.Loader.Title"]);
            return;
        }
        var state = _loader.GetGameState();
        if (state.RootPath == null)
        {
            Notifications.ShowWarning(UiLocalization.Current["Darktide.Loader.NeedGame"],
                UiLocalization.Current["Darktide.Loader.Title"]);
            return;
        }
        var packageLabel = type == DarktideReleasePackageType.Full
            ? UiLocalization.Current["Darktide.Loader.PackageFull"]
            : UiLocalization.Current["Darktide.Loader.PackageUpdateOnly"];
        var confirmed = await _dialogs.ConfirmAsync(UiLocalization.Current["Darktide.Loader.InstallTitle"],
            UiLocalization.Current.Format("Darktide.Loader.InstallMessage", release.Tag, packageLabel, state.RootPath));
        if (!confirmed) return;

        await RunAsync(async cancellationToken =>
        {
            HasProgress = true;
            ProgressPercent = 0;
            ProgressText = UiLocalization.Current["Darktide.Loader.Downloading"];
            DarktideLoaderInstallResult? result = null;
            try
            {
                await _tasks.RunTaskAsync($"安装 DML {release.Tag} ({packageLabel})", "DarktideLoaderInstall",
                    async (taskProgress, token) =>
                    {
                        taskProgress.Report(new TaskProgress
                        {
                            Stage = "Download", Message = UiLocalization.Current["Darktide.Loader.Downloading"]
                        });
                        var progress = new Progress<DarktideDownloadProgress>(value => OnUi(() =>
                        {
                            ProgressPercent = value.Percent ?? 0;
                            ProgressText = value.Percent is { } percent
                                ? $"{UiLocalization.Current["Darktide.Loader.Downloading"]} {percent:0}%"
                                : FormatBytes(value);
                            taskProgress.Report(new TaskProgress
                            {
                                Progress = 5 + (value.Percent ?? 0) * 0.85,
                                Stage = "Download",
                                Message = ProgressText
                            });
                        }));
                        taskProgress.Report(new TaskProgress { Progress = 5, Stage = "Download" });
                        result = await _loader.DownloadAndInstallAsync(release, type, progress, token);
                        if (!result.Success) throw new InvalidOperationException(result.Error ?? "安装失败。");
                        taskProgress.Report(new TaskProgress
                        {
                            Progress = 100, Stage = "Completed", Message = UiLocalization.Current["Darktide.Loader.InstallCompleted"]
                        });
                    }, cancellationToken);
            }
            finally
            {
                HasProgress = false;
                ProgressText = "";
            }
            await LoadStatusAsync(cancellationToken);
            Notifications.ShowSuccess(UiLocalization.Current.Format("Darktide.Loader.Installed", release.Tag),
                UiLocalization.Current["Darktide.Loader.Title"]);
            if (result?.BackupDirectory is { } backup)
                Notifications.ShowInfo(UiLocalization.Current.Format("Darktide.Loader.BackupCreated", backup),
                    UiLocalization.Current["Darktide.Loader.Title"]);
        }, failTitleKey: "Darktide.Loader.Title");
    }

    [RelayCommand]
    private void OpenDmfPage()
        => _browser.Open("https://www.nexusmods.com/warhammer40kdarktide/mods/8");

    [RelayCommand]
    private async Task ImportDmfAsync()
    {
        var game = _loader.GetGameState();
        if (game.RootPath == null)
        {
            Notifications.ShowWarning(UiLocalization.Current["Darktide.Loader.NeedGame"],
                UiLocalization.Current["Darktide.Loader.Title"]);
            return;
        }
        var archive = await _dialogs.PickArchiveAsync();
        if (archive == null) return;
        await RunAsync(async cancellationToken =>
        {
            var inspection = await Task.Run(() => _manager.InspectArchive(archive), cancellationToken);
            if (inspection.Error != null) throw new InvalidDataException(inspection.Error);
            var candidate = inspection.Candidates.FirstOrDefault(item =>
                item.InternalName.Equals(DarktidePaths.DmfFolderName, StringComparison.OrdinalIgnoreCase));
            if (candidate == null)
                throw new InvalidOperationException(UiLocalization.Current["Darktide.Loader.DmfNotFound"]);
            var allowReplace = false;
            if (Directory.Exists(Path.Combine(DarktidePaths.ModsDirectory(game.RootPath), DarktidePaths.DmfFolderName)))
            {
                allowReplace = await _dialogs.ConfirmAsync(UiLocalization.Current["Darktide.Loader.DmfReplaceTitle"],
                    UiLocalization.Current["Darktide.Loader.DmfReplaceMessage"]);
                if (!allowReplace) return;
            }
            await _tasks.RunTaskAsync("安装暗潮 DMF", "DarktideDmfInstall", async (progress, token) =>
            {
                progress.Report(new TaskProgress { Stage = "Install", CurrentItem = DarktidePaths.DmfFolderName });
                await _manager.ImportAsync(archive, candidate, allowReplace, register: false, cancellationToken: token);
                progress.Report(new TaskProgress
                {
                    Progress = 100, Stage = "Completed", Message = UiLocalization.Current["Darktide.Loader.DmfInstalled"]
                });
            }, cancellationToken);
            await LoadStatusAsync(cancellationToken);
            Notifications.ShowSuccess(UiLocalization.Current["Darktide.Loader.DmfInstalled"],
                UiLocalization.Current["Darktide.Loader.Title"]);
        }, failTitleKey: "Darktide.Loader.Title");
    }

    [RelayCommand]
    private async Task RunToggleAsync()
    {
        var state = _loader.GetGameState();
        if (state.RootPath == null)
        {
            Notifications.ShowWarning(UiLocalization.Current["Darktide.Loader.NeedGame"],
                UiLocalization.Current["Darktide.Loader.Title"]);
            return;
        }
        var confirmed = await _dialogs.ConfirmAsync(UiLocalization.Current["Darktide.Loader.ToggleTitle"],
            UiLocalization.Current.Format("Darktide.Loader.ToggleMessage", state.RootPath,
                DarktidePaths.ToggleScript(state.RootPath)));
        if (!confirmed) return;
        await RunAsync(async cancellationToken =>
        {
            var result = await _loader.RunToggleScriptAsync(state.RootPath, cancellationToken);
            if (!result.Started) throw new InvalidOperationException(result.Error ?? "无法启动切换脚本。");
            if (result.Error != null) Notifications.ShowWarning(result.Error, UiLocalization.Current["Darktide.Loader.Title"]);
            else
                Notifications.ShowInfo(UiLocalization.Current.Format("Darktide.Loader.ToggleFinished", result.ExitCode),
                    UiLocalization.Current["Darktide.Loader.Title"]);
            await LoadStatusAsync(cancellationToken);
        }, failTitleKey: "Darktide.Loader.Title");
    }

    [RelayCommand]
    private void OpenReleasePage()
        => _browser.Open(_release?.PageUrl ?? $"{DarktideReleaseParser.RepositoryPage}/releases");

    private static string FormatBytes(DarktideDownloadProgress value)
        => $"{value.ReceivedBytes / 1024d / 1024d:0.0} MB";
}
