using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// HellForge 专页：安装状态（status）、玩家档案（profiles / settings --profile）、
/// 启动设置（settings）、会话日志（logs）与会话进度（play --wait yes 事件流）。
/// 全部经 Application 服务调用官方 CLI。
/// </summary>
public partial class HellForgeViewModel : ObservableObject, IDisposable
{
    // HellForge 档案名规则：1-32 位小写字母、数字或连字符（docs/external-launchers.md）。
    private static readonly Regex ProfileNameRule = new("^[a-z0-9-]{1,32}$", RegexOptions.Compiled);

    private readonly HellForgeService _service;
    private readonly HellForgeSessionService _session;
    private readonly HellForgeModSyncService _syncService;
    private readonly ProfileService _profiles;
    private readonly NotificationService _notificationService;
    private readonly UiLocalization _localization;
    private CancellationTokenSource? _cancellation;
    private HellForgeStatus? _status;
    private HellForgeSyncStatus? _syncStatus;
    private HellForgeLauncherSettings? _settingsSnapshot;
    private HellForgeLogsResult? _logs;
    private string? _syncProfileName;
    private string? _error;
    private bool _loaded;
    private string _workingKey = "HellForge.Loading";
    private int _lastActiveStep;
    private bool _sessionEndedNotified;

    public Task Completion { get; private set; } = Task.CompletedTask;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isStartingSession;
    [ObservableProperty] private bool _isCreateDialogOpen;
    [ObservableProperty] private string _newProfileName = string.Empty;
    [ObservableProperty] private string _createError = string.Empty;
    [ObservableProperty] private HellForgeTemplateInfo? _selectedTemplate;

    // 启动设置：保存时只发送与快照不同的项。
    [ObservableProperty] private HellForgeChoice? _selectedWar;
    [ObservableProperty] private HellForgeChoice? _selectedStrangers;
    [ObservableProperty] private bool _settingLocalPlayFab;
    [ObservableProperty] private bool _settingMultiplayer;
    [ObservableProperty] private bool _settingEngineLog;
    [ObservableProperty] private bool _settingCrashDump;
    [ObservableProperty] private bool _settingSandbox;
    [ObservableProperty] private bool _settingSteamUdp;
    [ObservableProperty] private bool _settingLiveChanges;
    [ObservableProperty] private decimal _forgeCreditChance = 30;
    [ObservableProperty] private string _gameOptions = string.Empty;

    [ObservableProperty] private HellForgeLogRunItem? _selectedLogRun;
    [ObservableProperty] private bool _includeCrashDump;

    public ObservableCollection<HellForgeProfileItem> Profiles { get; } = new();
    public ObservableCollection<HellForgeTemplateInfo> Templates { get; } = new();
    public ObservableCollection<HellForgeSessionStepItem> SessionSteps { get; } = new();
    public ObservableCollection<HellForgeChoice> WarOptions { get; } = new();
    public ObservableCollection<HellForgeChoice> StrangersOptions { get; } = new();
    public ObservableCollection<HellForgeLogRunItem> LogRuns { get; } = new();

    /// <summary>请求保存日志 / 崩溃报告（视图负责选路径，再回调 ExportLogsAsync / CreateCrashReportAsync）。</summary>
    public event EventHandler? ExportLogsRequested;
    public event EventHandler? CrashReportRequested;

    public HellForgeViewModel(HellForgeService service, HellForgeSessionService session,
        HellForgeModSyncService syncService, ProfileService profiles,
        NotificationService notificationService, UiLocalization localization)
    {
        _service = service;
        _session = session;
        _syncService = syncService;
        _profiles = profiles;
        _notificationService = notificationService;
        _localization = localization;
        _localization.PropertyChanged += OnLanguageChanged;
        _session.StateChanged += OnSessionStateChanged;
        RebuildChoiceLabels();
        UpdateSessionProgress();
    }

    public bool IsInstallationConfigured => _service.TryGetInstallationRoot(out _);
    public bool HasStatus => _status != null;
    public bool HasProfiles => Profiles.Count > 0;
    public string HellForgeVersion => _status?.Hellforge ?? string.Empty;
    public string GameBuild => _status?.GameBuild ?? string.Empty;
    public string EngineVersion => _status?.Engine ?? string.Empty;
    public string ModSdkVersion => _status?.ModSdk?.ToString() ?? string.Empty;
    public string NetworkBlock => _status?.NetworkBlock ?? string.Empty;
    public string CurrentProfile => _status?.Settings.Profile ?? string.Empty;
    public bool IsSessionRunning => _session.State.Phase == HellForgeSessionPhase.Running;
    public bool IsGameCopyReady => _status?.Ready.GameCopy == true;
    public string GameCopyText => _localization[IsGameCopyReady ? "HellForge.Ready" : "HellForge.NotReady"];
    public string SessionText => _localization[IsSessionRunning ? "HellForge.SessionRunning" : "HellForge.SessionIdle"];

    public bool HasSync => _syncStatus?.HasSync == true;
    public int SyncedModCount => _syncStatus?.ModCount ?? 0;
    public string SyncStatusText => HasSync
        ? _localization.Format("HellForge.Sync.Synced", SyncedModCount)
        : _localization["HellForge.Sync.None"];
    public string SyncSourceText => HasSync
        ? _localization.Format("HellForge.Sync.Source",
            _syncProfileName ?? _localization["HellForge.Sync.UnknownProfile"],
            _syncStatus?.SyncedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? string.Empty)
        : string.Empty;
    /// <summary>settings 已加载，设置卡可编辑。</summary>
    public bool HasSettings => _settingsSnapshot != null;

    public bool IsSettingsDirty => _settingsSnapshot is { } saved && (
        (SelectedWar?.Value ?? "live") != (saved.War ?? "live")
        || SettingLocalPlayFab != saved.LocalPlayFab
        || SettingMultiplayer != saved.Multiplayer
        || SettingEngineLog != saved.EngineLog
        || SettingCrashDump != saved.CrashDump
        || SettingSandbox != saved.Sandbox
        || SettingSteamUdp != saved.SteamUdp
        || SettingLiveChanges != saved.LiveChanges
        || (SelectedStrangers?.Value ?? "default") != StrangersValue(saved.PlayWithStrangersChosen)
        || (int)ForgeCreditChance != saved.ForgeCreditChance
        || (GameOptions ?? string.Empty) != (saved.GameOptions ?? string.Empty));

    public bool HasLogs => LogRuns.Count > 0;

    public bool IsSessionActive => _session.State.IsActive;
    public bool HasSessionProblems => _session.State.Problems.Count > 0;
    public bool HasSessionNotice => !string.IsNullOrEmpty(_session.State.Notice);
    public string SessionProblemsText => _localization["HellForge.ProblemsTitle"] + "\n" +
        string.Join("\n", _session.State.Problems);
    public string SessionNoticeText => _session.State.Notice ?? string.Empty;

    public string SessionStatusText
    {
        get
        {
            var state = _session.State;
            var phaseText = _localization["HellForge.Phase." + state.Phase];
            return string.IsNullOrEmpty(state.Failure) ? phaseText : $"{phaseText} — {state.Failure}";
        }
    }

    public string StatusMessage => IsBusy ? _localization[_workingKey]
        : _error != null ? _localization.Format("HellForge.Failed", _error)
        : !IsInstallationConfigured ? _localization["HellForge.NotConfigured"]
        : _loaded ? _localization["HellForge.Loaded"]
        : string.Empty;

    /// <summary>进入页面时加载；刷新按钮共用同一流程。</summary>
    public Task InitializeAsync() => OperateAsync("HellForge.Loading", async token =>
    {
        await LoadAsync(token);
        await _session.RefreshAsync(token);
    });

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task RefreshAsync() => OperateAsync("HellForge.Loading", async token =>
    {
        await LoadAsync(token);
        await _session.RefreshAsync(token);
    });

    [RelayCommand(CanExecute = nameof(CanStartSession))]
    private void StartSession()
    {
        IsStartingSession = true;
        _sessionEndedNotified = false;
        _ = StartSessionCoreAsync();
    }

    private async Task StartSessionCoreAsync()
    {
        try
        {
            await _session.StartAsync();
        }
        catch (Exception error)
        {
            IsStartingSession = false;
            _notificationService.ShowError(
                _localization.Format("HellForge.Failed", error.Message), "HellForge");
        }
    }

    private bool CanStartSession() => !IsBusy && !IsStartingSession && !IsSessionActive && IsInstallationConfigured;

    [RelayCommand(CanExecute = nameof(CanStopSession))]
    private Task StopSessionAsync() => OperateAsync("HellForge.Stopping", token => _session.StopAsync(token));

    private bool CanStopSession() => !IsBusy && IsSessionActive;

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task SwitchProfileAsync(HellForgeProfileItem? item)
    {
        if (item == null || item.IsSelected) return Task.CompletedTask;
        return OperateAsync("HellForge.Switching", async token =>
        {
            await _service.SelectProfileAsync(item.Name, token);
            await LoadAsync(token);
            _notificationService.ShowSuccess(
                _localization.Format("HellForge.ProfileSwitched", item.Name), "HellForge");
        });
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void OpenCreateDialog()
    {
        NewProfileName = string.Empty;
        CreateError = string.Empty;
        SelectedTemplate = Templates.FirstOrDefault();
        IsCreateDialogOpen = true;
    }

    [RelayCommand]
    private void CancelCreate() => IsCreateDialogOpen = false;

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task CreateProfileAsync()
    {
        var name = NewProfileName.Trim();
        if (!ProfileNameRule.IsMatch(name))
        {
            CreateError = _localization["HellForge.NameInvalid"];
            return Task.CompletedTask;
        }
        if (SelectedTemplate == null)
        {
            CreateError = _localization["HellForge.TemplateRequired"];
            return Task.CompletedTask;
        }
        var template = SelectedTemplate.Name;
        return OperateAsync("HellForge.Creating", async token =>
        {
            await _service.CreateProfileAsync(name, template, token);
            await LoadAsync(token);
            IsCreateDialogOpen = false;
            _notificationService.ShowSuccess(
                _localization.Format("HellForge.ProfileCreated", name), "HellForge");
        });
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _cancellation?.Cancel();
        CancelCommand.NotifyCanExecuteChanged();
    }

    public async Task CancelAndWaitAsync()
    {
        _cancellation?.Cancel();
        await Completion;
    }

    private bool CanOperate() => !IsBusy;
    private bool CanCancel() => IsBusy && _cancellation?.IsCancellationRequested == false;

    /// <summary>保存设置：只发送与快照不同的项（settings --key value ...），输出为保存后的完整设置。</summary>
    [RelayCommand(CanExecute = nameof(CanSaveSettings))]
    private Task SaveSettingsAsync()
    {
        var saved = _settingsSnapshot;
        if (saved == null) return Task.CompletedTask;
        var changes = new Dictionary<string, string>();
        var war = SelectedWar?.Value ?? saved.War ?? "live";
        if (war != (saved.War ?? "live")) changes["war"] = war;
        AddSwitch(changes, "local-playfab", SettingLocalPlayFab, saved.LocalPlayFab);
        AddSwitch(changes, "multiplayer", SettingMultiplayer, saved.Multiplayer);
        AddSwitch(changes, "engine-log", SettingEngineLog, saved.EngineLog);
        AddSwitch(changes, "crash-dump", SettingCrashDump, saved.CrashDump);
        AddSwitch(changes, "sandbox", SettingSandbox, saved.Sandbox);
        AddSwitch(changes, "steam-udp", SettingSteamUdp, saved.SteamUdp);
        AddSwitch(changes, "live-changes", SettingLiveChanges, saved.LiveChanges);
        var strangers = SelectedStrangers?.Value ?? "default";
        if (strangers != StrangersValue(saved.PlayWithStrangersChosen)) changes["strangers"] = strangers;
        if ((int)ForgeCreditChance != saved.ForgeCreditChance)
            changes["forge-credits"] = ((int)ForgeCreditChance).ToString(CultureInfo.InvariantCulture);
        if ((GameOptions ?? string.Empty) != (saved.GameOptions ?? string.Empty))
            changes["game-options"] = GameOptions ?? string.Empty;
        if (changes.Count == 0) return Task.CompletedTask;
        return OperateAsync("HellForge.Settings.Saving", async token =>
        {
            var updated = await _service.UpdateSettingsAsync(changes, token);
            ApplySettings(updated);
            _notificationService.ShowSuccess(_localization["HellForge.Settings.Saved"], "HellForge");
        });
    }

    private bool CanSaveSettings() => !IsBusy && HasSettings && IsSettingsDirty;

    private static void AddSwitch(Dictionary<string, string> changes, string key, bool value, bool saved)
    {
        if (value != saved) changes[key] = value ? "yes" : "no";
    }

    [RelayCommand(CanExecute = nameof(CanUseLogs))]
    private void RequestExportLogs() => ExportLogsRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(CanUseLogs))]
    private void RequestCrashReport() => CrashReportRequested?.Invoke(this, EventArgs.Empty);

    private bool CanUseLogs() => !IsBusy && HasLogs;

    /// <summary>打包所选（或最新）会话的日志到 zip；CLI 拒绝覆盖，服务已先删除同路径文件。</summary>
    public Task ExportLogsAsync(string zipPath) => OperateAsync("HellForge.Logs.Exporting", async token =>
    {
        var result = await _service.ExportLogsAsync(zipPath, SelectedLogRun?.Id, token);
        _notificationService.ShowSuccess(
            _localization.Format("HellForge.Logs.Exported", result.Files, result.Zip ?? zipPath), "HellForge");
    });

    /// <summary>生成所选（或最新）会话的崩溃报告；IncludeCrashDump 决定是否含崩溃转储。</summary>
    public Task CreateCrashReportAsync(string zipPath) => OperateAsync("HellForge.Logs.Exporting", async token =>
    {
        var result = await _service.CreateCrashReportAsync(zipPath, IncludeCrashDump, SelectedLogRun?.Id, token);
        _notificationService.ShowSuccess(
            _localization.Format("HellForge.Logs.CrashSaved", result.Zip ?? zipPath), "HellForge");
    });

    private async Task LoadAsync(CancellationToken token)
    {
        var status = await _service.GetStatusAsync(token);
        var profiles = await _service.GetProfilesAsync(token);
        var settings = await _service.GetSettingsAsync(token);
        var logs = await _service.GetLogsAsync(cancellationToken: token);
        _status = status;
        Profiles.Clear();
        foreach (var profile in profiles.Profiles)
            Profiles.Add(new HellForgeProfileItem(profile.Name, profile.Template, profile.Selected));
        Templates.Clear();
        foreach (var template in profiles.Templates) Templates.Add(template);
        ApplySettings(settings);
        ApplyLogs(logs);
        _syncStatus = await _syncService.GetStatusAsync();
        _syncProfileName = null;
        if (_syncStatus.HasSync && _syncStatus.ProfileId != null
            && Guid.TryParse(_syncStatus.ProfileId, out var syncedProfileId))
        {
            var syncedProfile = await _profiles.GetProfileByIdAsync(syncedProfileId, token);
            _syncProfileName = syncedProfile?.Name;
        }
        _loaded = true;
        Refresh();
    }

    private static string StrangersValue(bool? chosen) => chosen switch { true => "yes", false => "no", _ => "default" };

    /// <summary>把 settings 输出映射到编辑字段；同时作为“脏”比较的基准。</summary>
    private void ApplySettings(HellForgeLauncherSettings settings)
    {
        _settingsSnapshot = settings;
        SettingLocalPlayFab = settings.LocalPlayFab;
        SettingMultiplayer = settings.Multiplayer;
        SettingEngineLog = settings.EngineLog;
        SettingCrashDump = settings.CrashDump;
        SettingSandbox = settings.Sandbox;
        SettingSteamUdp = settings.SteamUdp;
        SettingLiveChanges = settings.LiveChanges;
        ForgeCreditChance = settings.ForgeCreditChance;
        GameOptions = settings.GameOptions ?? string.Empty;
        SelectedWar = WarOptions.FirstOrDefault(o => o.Value == (settings.War ?? "live")) ?? WarOptions.FirstOrDefault();
        SelectedStrangers = StrangersOptions.FirstOrDefault(o => o.Value == StrangersValue(settings.PlayWithStrangersChosen))
            ?? StrangersOptions.FirstOrDefault();
        NotifySettingsState();
    }

    /// <summary>重建本地化的枚举选项并保留当前值（语言切换时调用）。</summary>
    private void RebuildChoiceLabels()
    {
        var war = SelectedWar?.Value ?? _settingsSnapshot?.War ?? "live";
        var strangers = SelectedStrangers?.Value ?? StrangersValue(_settingsSnapshot?.PlayWithStrangersChosen);
        WarOptions.Clear();
        foreach (var value in new[] { "live", "frozen", "open" })
            WarOptions.Add(new HellForgeChoice(value, _localization["HellForge.War." + Capitalize(value)]));
        StrangersOptions.Clear();
        foreach (var value in new[] { "default", "yes", "no" })
            StrangersOptions.Add(new HellForgeChoice(value, _localization["HellForge.Strangers." + Capitalize(value)]));
        SelectedWar = WarOptions.FirstOrDefault(o => o.Value == war) ?? WarOptions.FirstOrDefault();
        SelectedStrangers = StrangersOptions.FirstOrDefault(o => o.Value == strangers) ?? StrangersOptions.FirstOrDefault();
    }

    private static string Capitalize(string value) => char.ToUpperInvariant(value[0]) + value[1..];

    private void ApplyLogs(HellForgeLogsResult logs)
    {
        _logs = logs;
        var selectedId = SelectedLogRun?.Id;
        LogRuns.Clear();
        foreach (var run in logs.Runs) LogRuns.Add(CreateLogItem(run));
        SelectedLogRun = LogRuns.FirstOrDefault(r => r.Id == selectedId) ?? LogRuns.FirstOrDefault();
        OnPropertyChanged(nameof(HasLogs));
        RequestExportLogsCommand.NotifyCanExecuteChanged();
        RequestCrashReportCommand.NotifyCanExecuteChanged();
    }

    private HellForgeLogRunItem CreateLogItem(HellForgeLogRun run)
    {
        var title = run.StartedUtc is { } started
            ? $"{started.ToLocalTime():yyyy-MM-dd HH:mm} · {run.Id}"
            : run.Id;
        var statusKey = LogStatusKey(run.Status);
        var status = statusKey == "Unknown" && !string.IsNullOrEmpty(run.Status)
            ? run.Status
            : _localization["HellForge.LogStatus." + statusKey];
        var parts = new List<string>();
        if (run.Crashed) parts.Add(_localization["HellForge.Logs.Crashed"]);
        if (!string.IsNullOrEmpty(run.Failure)) parts.Add(run.Failure);
        if (!string.IsNullOrEmpty(run.GameExitCode))
            parts.Add(_localization.Format("HellForge.Logs.ExitCode", run.GameExitCode));
        return new HellForgeLogRunItem(run, title, status, string.Join(" · ", parts));
    }

    private static string LogStatusKey(string? status) => status switch
    {
        "completed" => "Completed",
        "failed" => "Failed",
        "cleanup_failed" => "CleanupFailed",
        "observed" => "Observed",
        "running" => "Running",
        "crashed" => "Crashed",
        _ => "Unknown"
    };

    private void NotifySettingsState()
    {
        SaveSettingsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsSettingsDirty));
    }

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        SwitchProfileCommand.NotifyCanExecuteChanged();
        OpenCreateDialogCommand.NotifyCanExecuteChanged();
        CreateProfileCommand.NotifyCanExecuteChanged();
        StartSessionCommand.NotifyCanExecuteChanged();
        StopSessionCommand.NotifyCanExecuteChanged();
        SaveSettingsCommand.NotifyCanExecuteChanged();
        RequestExportLogsCommand.NotifyCanExecuteChanged();
        RequestCrashReportCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        Refresh();
    }

    partial void OnIsStartingSessionChanged(bool value) => StartSessionCommand.NotifyCanExecuteChanged();

    /// <summary>设置字段或日志选择变化时，更新“保存”与导出按钮的可用状态。</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(SelectedWar):
            case nameof(SelectedStrangers):
            case nameof(SettingLocalPlayFab):
            case nameof(SettingMultiplayer):
            case nameof(SettingEngineLog):
            case nameof(SettingCrashDump):
            case nameof(SettingSandbox):
            case nameof(SettingSteamUdp):
            case nameof(SettingLiveChanges):
            case nameof(ForgeCreditChance):
            case nameof(GameOptions):
                NotifySettingsState();
                break;
            case nameof(SelectedLogRun):
                RequestExportLogsCommand.NotifyCanExecuteChanged();
                RequestCrashReportCommand.NotifyCanExecuteChanged();
                break;
        }
    }

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) UpdateSessionProgress();
        else Dispatcher.UIThread.Post(UpdateSessionProgress);
    }

    /// <summary>把会话快照映射到界面：步骤条、状态文字、启停按钮与结束通知。</summary>
    private void UpdateSessionProgress()
    {
        var state = _session.State;
        if (state.IsActive)
        {
            IsStartingSession = false;
            _sessionEndedNotified = false;
            _lastActiveStep = StepOfPhase(state.Phase);
        }
        else if (!_sessionEndedNotified && state.Phase is HellForgeSessionPhase.Ended or HellForgeSessionPhase.Failed)
        {
            _sessionEndedNotified = true;
            IsStartingSession = false;
            if (state.Phase == HellForgeSessionPhase.Ended)
                _notificationService.ShowSuccess(_localization["HellForge.Phase.Ended"], "HellForge");
            else
                _notificationService.ShowError(
                    _localization.Format("HellForge.Failed", state.Failure ?? _localization["HellForge.Phase.Failed"]), "HellForge");
        }

        var current = state.Phase == HellForgeSessionPhase.Failed ? _lastActiveStep : StepOfPhase(state.Phase);
        SessionSteps.Clear();
        for (var step = 1; step <= 5; step++)
        {
            var done = state.Phase == HellForgeSessionPhase.Ended || step < current;
            SessionSteps.Add(new HellForgeSessionStepItem(
                step, _localization[StepLabelKey(step)],
                done, step == current && state.Phase != HellForgeSessionPhase.Ended,
                state.Phase == HellForgeSessionPhase.Failed && step == current));
        }

        OnPropertyChanged(nameof(IsSessionActive));
        OnPropertyChanged(nameof(IsSessionRunning));
        OnPropertyChanged(nameof(SessionText));
        OnPropertyChanged(nameof(SessionStatusText));
        OnPropertyChanged(nameof(HasSessionProblems));
        OnPropertyChanged(nameof(SessionProblemsText));
        OnPropertyChanged(nameof(HasSessionNotice));
        OnPropertyChanged(nameof(SessionNoticeText));
        StartSessionCommand.NotifyCanExecuteChanged();
        StopSessionCommand.NotifyCanExecuteChanged();
    }

    private static int StepOfPhase(HellForgeSessionPhase phase) => phase switch
    {
        HellForgeSessionPhase.Preparing => 1,
        HellForgeSessionPhase.AwaitingNetworkBlock or HellForgeSessionPhase.AwaitingSteamBroker => 3,
        HellForgeSessionPhase.Patching => 4,
        HellForgeSessionPhase.Running => 5,
        _ => 0
    };

    private static string StepLabelKey(int step) => step switch
    {
        1 => "HellForge.Step.BuildMods",
        2 => "HellForge.Step.VerifyFiles",
        3 => "HellForge.Step.NetworkBlock",
        4 => "HellForge.Step.Launching",
        _ => "HellForge.Step.Running"
    };

    private async Task OperateAsync(string workingKey, Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Completion = completion.Task;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _error = null;
        _workingKey = workingKey;
        IsBusy = true;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { _error = error.Message; }
        finally { _cancellation = null; IsBusy = false; completion.TrySetResult(); }
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(UiLocalization.Revision)) return;
        Refresh();
        UpdateSessionProgress();
        RebuildChoiceLabels();
        if (_logs != null) ApplyLogs(_logs);
    }

    private void Refresh()
    {
        foreach (var property in new[] { nameof(StatusMessage), nameof(IsInstallationConfigured), nameof(HasStatus),
            nameof(HasProfiles), nameof(HellForgeVersion), nameof(GameBuild), nameof(EngineVersion),
            nameof(ModSdkVersion), nameof(NetworkBlock), nameof(CurrentProfile), nameof(IsSessionRunning),
            nameof(IsGameCopyReady), nameof(GameCopyText), nameof(SessionText),
            nameof(HasSync), nameof(SyncedModCount), nameof(SyncStatusText), nameof(SyncSourceText),
            nameof(HasSettings), nameof(IsSettingsDirty), nameof(HasLogs) })
            OnPropertyChanged(property);
        SaveSettingsCommand.NotifyCanExecuteChanged();
        RequestExportLogsCommand.NotifyCanExecuteChanged();
        RequestCrashReportCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _cancellation?.Cancel();
        _localization.PropertyChanged -= OnLanguageChanged;
        _session.StateChanged -= OnSessionStateChanged;
    }
}

/// <summary>档案列表行；列表整体重建，行本身无需通知。</summary>
public sealed class HellForgeProfileItem(string name, string? template, bool isSelected)
{
    public string Name { get; } = name;
    public string? Template { get; } = template;
    public bool IsSelected { get; } = isSelected;
}

/// <summary>会话步骤条的一格；随进度整体重建。</summary>
public sealed class HellForgeSessionStepItem(int number, string label, bool isDone, bool isActive, bool isFailed)
{
    public int Number { get; } = number;
    public string Label { get; } = label;
    public bool IsDone { get; } = isDone;
    public bool IsActive { get; } = isActive;
    public bool IsFailed { get; } = isFailed;
}

/// <summary>设置卡里的枚举项（war、陌生人联机）；标签随语言重建。</summary>
public sealed record HellForgeChoice(string Value, string Label);

/// <summary>日志列表的一行；随刷新整体重建。</summary>
public sealed class HellForgeLogRunItem(HellForgeLogRun run, string title, string statusText, string detailText)
{
    public HellForgeLogRun Run { get; } = run;
    public string Id => Run.Id;
    public string Title { get; } = title;
    public string StatusText { get; } = statusText;
    public string DetailText { get; } = detailText;
    public bool HasDetail => DetailText.Length > 0;
    public bool Crashed => Run.Crashed;
}