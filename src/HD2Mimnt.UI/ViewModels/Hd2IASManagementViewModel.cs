using System.ComponentModel;
using System.Net;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

public partial class Hd2IASManagementViewModel : ObservableObject, IDisposable, IAsyncDisposable
{
    private readonly Hd2IASIntegrationService _service;
    private readonly GameDetectionService _game;
    private readonly UiLocalization _localization;
    private readonly IConfirmationService? _confirmation;
    private Hd2IASManagementState? _managementState;
    private Hd2IASUninstallResult? _uninstalled;
    private bool _automaticChecksStopped;
    public event EventHandler? InstallationRequirementChanged;
    public bool NeedsRuntimeInstallation => _managementState?.NeedsInstallation == true;
    public string RuntimeRequiredMessage => _localization["IAS.RuntimeRequired"];
    public bool NeedsDataGeneration => _managementState?.NeedsDataGeneration == true;
    public string DataRequiredMessage => _localization["IAS.DataRequired"];
    public bool CanUninstall => !IsBusy && _managementState?.HasManagedInstallation == true && !string.IsNullOrWhiteSpace(GamePath);
    private Hd2IASInspection? _report;
    private Hd2IASInstalledBuild? _installedBuild;
    private string? _error;
    private string? _errorKey;
    private bool _acceptingDownload;
    private bool _versionsChecked;
    private DownloadProgress? _downloadProgress;
    private bool _cancelled;
    private string _workingKey = "IAS.Working";
    private Hd2IASGeneratedPackage? _generated;
    private bool _installed;
    [ObservableProperty] private string _operationLog = "";
    private CancellationTokenSource? _cancellation;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public string[] Routes { get; } = ["lua", "native"];

    [ObservableProperty] private string _packagePath = "";
    [ObservableProperty] private string _selectedRoute = "native";
    [ObservableProperty] private IReadOnlyList<Hd2IASUpdateVersion> _updateVersions = [];
    [ObservableProperty] private Hd2IASUpdateVersion? _selectedUpdate;
    [ObservableProperty] private bool _isBusy;
    public bool CanEdit => !IsBusy;
    public bool HasReport => _report != null;
    public bool HasInstallationReport => _installedBuild != null;
    public string InstalledBuildSummary => _installedBuild == null ? "" : _installedBuild.State == Hd2IASBuildState.Read
        ? _localization.Format("IAS.BuildRead", _installedBuild.Version!, _installedBuild.Route?.ToString() ?? "UNKNOWN")
        : _localization["IAS.Build" + _installedBuild.State];
    public string InstalledBuildContents => _installedBuild?.Contents ?? "";
    public string ConfigurationSummary => _installedBuild == null ? "" : string.Join("\n",
        _installedBuild.ConfigurationFiles.Select(file => $"{_localization["IAS.Config" + file.Kind]} · {file.Path}"));
    public string FilePlan => _report == null ? "" : string.Join("\n", _report.FilePlan.Select(file =>
        $"{_localization["IAS.Action" + file.Action]} · {file.Path}"));
    public string GamePath => _game.CurrentGameInfo?.Path ?? "";
    public string StatusMessage => IsBusy ? _localization[_workingKey]
        : _errorKey != null ? _localization[_errorKey]
        : _error != null ? _localization.Format("IAS.Failed", _error)
        : _cancelled ? _localization["IAS.Cancelled"]
        : _uninstalled != null ? _uninstalled.ModifiedFiles.Count == 0 ? _localization["IAS.Uninstalled"]
            : _localization.Format("IAS.UninstalledModified", string.Join(", ", _uninstalled.ModifiedFiles))
        : _generated != null ? _localization.Format(_installed ? "IAS.Installed" : "IAS.Generated", _generated.Path)
        : _report != null ? _localization[_report.Package.IsValid ? "IAS.Complete" : "IAS.PackageInvalid"]
        : _versionsChecked ? UpdateVersions.Count == 0 ? _localization["IAS.NoVersions"]
            : _localization.Format("IAS.VersionsReady", UpdateVersions.Count)
        : _localization["IAS.SelectPackage"];
    public string DownloadProgressText => !IsBusy || _downloadProgress == null ? "" :
        _localization.Format("IAS.DownloadProgress", (_downloadProgress.DownloadedBytes / 1048576d).ToString("F1"),
            _downloadProgress.TotalBytes > 0 ? (_downloadProgress.TotalBytes / 1048576d).ToString("F1") : "?");
    public string PackageSummary => _report is not { } report ? "" : _localization.Format("IAS.PackageSummary",
        report.Package.Version, report.Package.Foundation.Version, report.Package.ForRoute(report.SelectedRoute).AppearanceSource);
    public string InstallationSummary => _report is not { } report ? "" : report.InstalledRoute is { } route
        ? _localization.Format("IAS.RuntimeMatches", route == Hd2IASRoute.Lua ? "Lua" : "Native", report.InstalledVersion!)
        : _localization["IAS.Installation" + report.Installation];
    public string CompatibilitySummary => _report is not { } report ? "" : _localization[report.GameChecks.Count == 0
        ? "IAS.NoGame" : report.IsGameCompatible ? "IAS.Compatible" : "IAS.Incompatible"];
    public string IdentitySummary => _report is not { } report ? "" :
        $"{report.Package.ForRoute(report.SelectedRoute).IndependentLua.Module}\n{report.Package.ForRoute(report.SelectedRoute).IndependentLua.Identity}";
    public string PackageChecks => FormatChecks(_report?.Package.FileChecks);
    public string GameChecks => FormatChecks(_report?.GameChecks);
    public string RuntimeChecks => FormatChecks(_report?.RuntimeChecks);
    public string FoundationChecks => FormatChecks(_report?.FoundationChecks);
    public string UpdateNotes => _report == null ? "" : string.Join("\n", new[] { _report.Package.UpdateNotes }.Concat(_report.Package.ManualActions));
    public bool CanGenerate => !IsBusy && _report?.Package.IsValid == true && !string.IsNullOrWhiteSpace(GamePath);
    public bool CanInstall => CanInspect() && !string.IsNullOrWhiteSpace(GamePath);

    public Hd2IASManagementViewModel(Hd2IASIntegrationService service, GameDetectionService game, UiLocalization localization,
        IConfirmationService? confirmation = null)
    {
        _service = service; _game = game; _localization = localization;
        _confirmation = confirmation;
        _localization.PropertyChanged += OnLanguageChanged;
    }

    partial void OnPackagePathChanged(string value) { if (!_acceptingDownload) Invalidate(); }
    partial void OnSelectedUpdateChanged(Hd2IASUpdateVersion? value) => DownloadUpdateCommand.NotifyCanExecuteChanged();
    partial void OnSelectedRouteChanged(string value) => Invalidate();
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(CanGenerate));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanUninstall)); UninstallCommand.NotifyCanExecuteChanged();
        InspectCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); GenerateCommand.NotifyCanExecuteChanged();
        RefreshVersionsCommand.NotifyCanExecuteChanged(); DownloadUpdateCommand.NotifyCanExecuteChanged();
        RefreshInstallationCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
    }
    private bool CanInspect() => !IsBusy && !string.IsNullOrWhiteSpace(PackagePath) && Routes.Contains(SelectedRoute);
    private bool CanCancel() => IsBusy && _cancellation?.IsCancellationRequested == false;
    private bool CanDownload() => !IsBusy && SelectedUpdate != null;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task RefreshInstallationAsync() => OperateAsync("IAS.ReadingInstallation", async token =>
    {
        var gamePath = GamePath;
        await RefreshManagementStateAsync(gamePath, token);
    });

    public Task CheckRuntimeRequirementAsync() => _automaticChecksStopped ? Task.CompletedTask
        : IsBusy ? Completion : OperateAsync("IAS.ReadingInstallation", token => RefreshManagementStateAsync(GamePath, token), preserveResult: true);

    public async Task StopAutomaticChecksAsync() { _automaticChecksStopped = true; await CancelAndWaitAsync(); }

    private async Task RefreshManagementStateAsync(string gamePath, CancellationToken token)
    {
        var state = await _service.GetManagementStateAsync(gamePath, token);
        token.ThrowIfCancellationRequested();
        if (gamePath != GamePath) return;
        _managementState = state; _installedBuild = state.Build;
        OnPropertyChanged(nameof(NeedsRuntimeInstallation)); OnPropertyChanged(nameof(CanUninstall));
        OnPropertyChanged(nameof(NeedsDataGeneration));
        UninstallCommand.NotifyCanExecuteChanged();
        InstallationRequirementChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task RefreshVersionsAsync() => OperateAsync("IAS.CheckingUpdates", async token =>
    {
        var versions = await _service.GetVersionsAsync(token);
        token.ThrowIfCancellationRequested();
        UpdateVersions = versions; SelectedUpdate = versions.FirstOrDefault(); _versionsChecked = true;
    });

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private Task DownloadUpdateAsync()
    {
        if (SelectedUpdate is not { } version || !CanDownload()) return Task.CompletedTask;
        var route = SelectedRoute; var game = GamePath;
        _report = null;
        return OperateAsync("IAS.Downloading", async token =>
        {
            var progress = new Progress<DownloadProgress>(value =>
            {
                if (token.IsCancellationRequested || !IsBusy || _workingKey != "IAS.Downloading") return;
                _downloadProgress = value; OnPropertyChanged(nameof(DownloadProgressText));
            });
            var path = await _service.DownloadUpdateAsync(version, progress, token);
            token.ThrowIfCancellationRequested();
            try { _acceptingDownload = true; PackagePath = path; }
            finally { _acceptingDownload = false; }
            _workingKey = "IAS.Working"; _downloadProgress = null; Refresh();
            var report = await _service.InspectAsync(path, game,
                route == "native" ? Hd2IASRoute.Native : Hd2IASRoute.Lua, token);
            token.ThrowIfCancellationRequested();
            if (path == PackagePath && route == SelectedRoute && game == GamePath)
            { _report = report; _installedBuild = report.InstalledBuild; }
        });
    }

    [RelayCommand(CanExecute = nameof(CanInspect))]
    private async Task InspectAsync()
    {
        if (!CanInspect()) return;
        _report = null;
        var package = PackagePath.Trim(); var route = SelectedRoute; var gamePath = GamePath;
        await OperateAsync("IAS.Working", async token =>
        {
            var report = await _service.InspectAsync(package, gamePath,
                route == "native" ? Hd2IASRoute.Native : Hd2IASRoute.Lua, token);
            token.ThrowIfCancellationRequested();
            if (package == PackagePath.Trim() && route == SelectedRoute && gamePath == GamePath)
            { _report = report; _installedBuild = report.InstalledBuild; }
        });
    }

    private bool CanStartGeneration() => CanGenerate;
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallAsync() => !CanInstall ? Task.CompletedTask : OperateAsync("IAS.Installing", async token =>
    {
        var progress = new Progress<string>(message =>
        {
            if (token.IsCancellationRequested || !IsBusy) return;
            OperationLog += message + "\n";
        });
        var result = await _service.InstallAsync(PackagePath.Trim(), GamePath,
            SelectedRoute == "native" ? Hd2IASRoute.Native : Hd2IASRoute.Lua, progress, token);
        _generated = result; _installed = true;
        _report = await _service.InspectAsync(PackagePath.Trim(), GamePath, result.Route, CancellationToken.None);
        await RefreshManagementStateAsync(GamePath, CancellationToken.None);
    });

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync()
    {
        if (!CanUninstall || _confirmation == null) return;
        if (!await _confirmation.ConfirmAsync(_localization["IAS.Uninstall"], _localization["IAS.UninstallConfirm"])) return;
        if (!CanUninstall) return;
        await OperateAsync("IAS.Uninstalling", async token =>
        {
            var progress = new Progress<string>(message =>
            {
                if (!token.IsCancellationRequested && IsBusy) OperationLog += message + "\n";
            });
            _uninstalled = await _service.UninstallAsync(GamePath, progress, token);
            _report = null;
            await RefreshManagementStateAsync(GamePath, CancellationToken.None);
        });
    }

    [RelayCommand(CanExecute = nameof(CanStartGeneration))]
    private Task GenerateAsync(string outputPath) => !CanGenerate ? Task.CompletedTask : OperateAsync("IAS.Generating", async token =>
    {
        _generated = await _service.GenerateAsync(PackagePath.Trim(), GamePath,
            SelectedRoute == "native" ? Hd2IASRoute.Native : Hd2IASRoute.Lua, outputPath, token);
    });

    private async Task OperateAsync(string workingKey, Func<CancellationToken, Task> action, bool preserveResult = false)
    {
        if (IsBusy) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Completion = completion.Task;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation; _error = null; _errorKey = null; _downloadProgress = null;
        _cancelled = false;
        if (!preserveResult) { _generated = null; _installed = false; _uninstalled = null; OperationLog = ""; }
        _workingKey = workingKey;
        IsBusy = true; Refresh();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { _cancelled = true; }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.TooManyRequests)
        { _errorKey = "IAS.UpdateRateLimited"; }
        catch (Exception error) { _error = error.Message; }
        finally { _cancellation = null; IsBusy = false; Refresh(); completion.TrySetResult(); }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() { _cancellation?.Cancel(); CancelCommand.NotifyCanExecuteChanged(); }
    public async Task CancelAndWaitAsync() { _cancellation?.Cancel(); await Completion; }

    public void Invalidate()
    {
        _cancellation?.Cancel(); _report = null; _error = null; _errorKey = null; _cancelled = false; _generated = null;
        GenerateCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanGenerate));
        InstallCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanInstall));
        InspectCommand.NotifyCanExecuteChanged(); Refresh();
    }
    private string FormatChecks(IReadOnlyList<Hd2IASFileCheck>? checks) => checks == null ? "" : string.Join("\n",
        checks.Select(check => $"{_localization["IAS.File" + check.State]} · {check.Path}"));
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(UiLocalization.Revision)) Refresh(); }
    private void Refresh()
    {
        foreach (var property in new[] { nameof(HasReport), nameof(StatusMessage), nameof(PackageSummary), nameof(InstallationSummary),
            nameof(CompatibilitySummary), nameof(IdentitySummary), nameof(PackageChecks), nameof(GameChecks), nameof(RuntimeChecks),
            nameof(FoundationChecks), nameof(UpdateNotes), nameof(GamePath), nameof(DownloadProgressText), nameof(HasInstallationReport),
            nameof(InstalledBuildSummary), nameof(InstalledBuildContents), nameof(ConfigurationSummary), nameof(FilePlan),
            nameof(NeedsRuntimeInstallation), nameof(RuntimeRequiredMessage), nameof(NeedsDataGeneration), nameof(DataRequiredMessage),
            nameof(CanUninstall) }) OnPropertyChanged(property);
    }
    public async ValueTask DisposeAsync() { await StopAutomaticChecksAsync(); Dispose(); }
    public void Dispose() { _automaticChecksStopped = true; _cancellation?.Cancel(); _localization.PropertyChanged -= OnLanguageChanged; }
}
