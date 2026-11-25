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
    private readonly IConfirmationService _confirmation;
    private CancellationTokenSource? _cancellation;
    private HellForgeStatus? _status;
    private HellForgeVersions? _versions;
    private HellForgeSyncStatus? _syncStatus;
    private HellForgeLauncherSettings? _settingsSnapshot;
    private HellForgeLogsResult? _logs;
    private string? _syncProfileName;
    private string? _error;
    private bool _loaded;
    private string _workingKey = "HellForge.Loading";
    private int _lastActiveStep;
    private bool _sessionEndedNotified;
    private bool _suppressPackEvents;
    private bool _suppressModEvents;
    private bool _supportsModernWar;
    private bool _supportsPlanets;
    private bool _planetsLoaded;
    private HellForgePlanetsResult? _planets;
    private HellForgePlanetEditor? _observedPlanetEditor;

    public Task Completion { get; private set; } = Task.CompletedTask;

    /// <summary>共享游戏性模组变化后后台重读 mods 的任务；测试与页面关闭可等待它。</summary>
    public Task ModsRefresh { get; private set; } = Task.CompletedTask;

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
    [ObservableProperty] private string _warServer = string.Empty;
    [ObservableProperty] private bool _settingDealSubfactions;
    [ObservableProperty] private bool _settingDealSeaf;
    [ObservableProperty] private decimal _warSeed;
    [ObservableProperty] private bool _settingShareMods;
    [ObservableProperty] private bool _settingListPublic;
    [ObservableProperty] private bool _settingShowEditedStats;
    [ObservableProperty] private bool _settingHostStats;

    [ObservableProperty] private HellForgeLogRunItem? _selectedLogRun;
    [ObservableProperty] private bool _includeCrashDump;
    [ObservableProperty] private HellForgeCategoryItem? _selectedCategory;
    [ObservableProperty] private HellForgePlanetEditor? _planetEditor;
    [ObservableProperty] private string _planetQuery = string.Empty;

    public ObservableCollection<HellForgeCategoryItem> Categories { get; } = new();
    public ObservableCollection<HellForgeProfileItem> Profiles { get; } = new();
    public ObservableCollection<HellForgeTemplateInfo> Templates { get; } = new();
    public ObservableCollection<HellForgeSessionStepItem> SessionSteps { get; } = new();
    public ObservableCollection<HellForgeChoice> WarOptions { get; } = new();
    public ObservableCollection<HellForgeChoice> StrangersOptions { get; } = new();
    public ObservableCollection<HellForgeLogRunItem> LogRuns { get; } = new();
    public ObservableCollection<HellForgeModItem> HellForgeMods { get; } = new();
    public ObservableCollection<HellForgePlanetItem> Planets { get; } = new();
    /// <summary>展示用：模组行与分组标题混排（分组标题按 group 变化插入）。</summary>
    public ObservableCollection<object> ModRows { get; } = new();

    /// <summary>请求保存日志 / 崩溃报告（视图负责选路径，再回调 ExportLogsAsync / CreateCrashReportAsync）。</summary>
    public event EventHandler? ExportLogsRequested;
    public event EventHandler? CrashReportRequested;
    /// <summary>请求添加 HellForge 模组（视图选文件后回调 AddModAsync，直接进 HF 的 mods\）。</summary>
    public event EventHandler? AddModRequested;

    private bool _modsLoaded;
    private HellForgeModsList? _modsList;
    private ModlistCompareOutcome _modlistCompareOutcome;
    private int _modsRefreshVersion;

    [ObservableProperty] private string _modlistCompareInput = string.Empty;

    /// <summary>好友模组码比对的结果；只影响提示文字与可见性。</summary>
    private enum ModlistCompareOutcome
    {
        None,
        Match,
        Mismatch,
        Invalid,
        NoLocal
    }

    public HellForgeViewModel(HellForgeService service, HellForgeSessionService session,
        HellForgeModSyncService syncService, ProfileService profiles,
        NotificationService notificationService, UiLocalization localization,
        IConfirmationService confirmation)
    {
        _service = service;
        _session = session;
        _syncService = syncService;
        _profiles = profiles;
        _notificationService = notificationService;
        _localization = localization;
        _confirmation = confirmation;
        _localization.PropertyChanged += OnLanguageChanged;
        _session.StateChanged += OnSessionStateChanged;
        RebuildChoiceLabels();
        RebuildCategories();
        UpdateSessionProgress();
    }

    public bool IsOverviewCategoryVisible => CategoryIs("Overview");
    public bool IsSessionCategoryVisible => CategoryIs("Session");
    public bool IsProfilesCategoryVisible => CategoryIs("Profiles");
    public bool IsModsCategoryVisible => CategoryIs("Mods");
    public bool IsPlanetsCategoryVisible => CategoryIs("Planets");
    public bool IsSettingsCategoryVisible => CategoryIs("Settings");
    public bool IsLogsCategoryVisible => CategoryIs("Logs");

    public string OverviewLabel => CategoryLabel("Overview");
    public string SessionLabel => CategoryLabel("Session");
    public string ProfilesLabel => CategoryLabel("Profiles");
    public string ModsLabel => CategoryLabel("Mods");
    public string PlanetsLabel => CategoryLabel("Planets");
    public string SettingsLabel => CategoryLabel("Settings");
    public string LogsLabel => CategoryLabel("Logs");
    public string SelectedCategoryLabel => SelectedCategory?.Label ?? string.Empty;

    private bool CategoryIs(string key) => SelectedCategory?.Key == key;
    private string CategoryLabel(string key) => Categories.FirstOrDefault(category => category.Key == key)?.Label ?? string.Empty;

    public bool IsInstallationConfigured => _service.TryGetInstallationRoot(out _);
    public bool HasStatus => _status != null;
    public bool HasProfiles => Profiles.Count > 0;
    public string HellForgeVersion => _status?.Hellforge ?? string.Empty;
    public string GameBuild => _status?.GameBuild ?? string.Empty;
    public string EngineVersion => _status?.Engine ?? string.Empty;
    public string ModSdkVersion => _status?.ModSdk?.ToString() ?? string.Empty;
    public string PlayingVersionText => _versions?.Playing?.Version is { Length: > 0 } version
        ? $"{version} ({_versions.Playing.Commit})"
        : string.Empty;
    public string LanguagesText => _status?.Languages.Count > 0 ? string.Join(", ", _status.Languages) : string.Empty;
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

    public bool HasMods => HellForgeMods.Count > 0;
    public bool HasModProblems => _modsList?.Problems.Count > 0;
    public string ModProblemsText => HasModProblems
        ? _localization["HellForge.Mods.Problems"] + "\n" + string.Join("\n", _modsList!.Problems)
        : string.Empty;
    /// <summary>联机模组码：好友比对用；没有启用的游戏性模组时 CLI 给 null。</summary>
    public bool HasModlistCode => !string.IsNullOrEmpty(_modsList?.Modlist);
    public string ModlistCode => _modsList?.Modlist ?? string.Empty;
    public string ModlistText => HasModlistCode
        ? _localization.Format("HellForge.Mods.Modlist", _modsList!.Modlist!,
            HellForgeMods.Count(mod => mod.Enabled && mod.Group == "gameplay"))
        : _localization["HellForge.Mods.ModlistEmpty"];

    public bool HasModlistCompareResult => _modlistCompareOutcome != ModlistCompareOutcome.None;
    public bool IsModlistCompareMatch => _modlistCompareOutcome == ModlistCompareOutcome.Match;
    public bool IsModlistCompareProblem => _modlistCompareOutcome is ModlistCompareOutcome.Mismatch
        or ModlistCompareOutcome.Invalid or ModlistCompareOutcome.NoLocal;
    public string ModlistCompareResultText => _modlistCompareOutcome switch
    {
        ModlistCompareOutcome.Match => _localization["HellForge.Mods.CompareMatch"],
        ModlistCompareOutcome.Mismatch => _localization["HellForge.Mods.CompareMismatch"],
        ModlistCompareOutcome.Invalid => _localization["HellForge.Mods.CompareInvalid"],
        ModlistCompareOutcome.NoLocal => _localization["HellForge.Mods.CompareNoLocal"],
        _ => string.Empty
    };
    /// <summary>HF 安装根的 mods 目录（打开资源管理器用）；未配置时为空。</summary>
    public string ModsDirectory => _service.TryGetInstallationRoot(out var root)
        ? Path.Combine(root, "mods")
        : string.Empty;

    public bool HasWarSettings => _settingsSnapshot is { } saved &&
        (saved.WarServer != null || saved.DealSubfactions.HasValue || saved.DealSeaf.HasValue || saved.WarSeed.HasValue);
    public bool HasSharingSettings => _settingsSnapshot is { } saved &&
        (saved.ShareMods.HasValue || saved.ListPublic.HasValue || saved.ShowEditedStats.HasValue || saved.HostStats.HasValue);

    public bool IsSettingsDirty => _settingsSnapshot is { } saved && (
        (SelectedWar?.Value ?? saved.War ?? "live") != (saved.War ?? "live")
        || SettingLocalPlayFab != saved.LocalPlayFab
        || SettingMultiplayer != saved.Multiplayer
        || SettingEngineLog != saved.EngineLog
        || SettingCrashDump != saved.CrashDump
        || SettingSandbox != saved.Sandbox
        || SettingSteamUdp != saved.SteamUdp
        || SettingLiveChanges != saved.LiveChanges
        || (SelectedStrangers?.Value ?? "default") != StrangersValue(saved.PlayWithStrangersChosen)
        || (int)ForgeCreditChance != saved.ForgeCreditChance
        || (GameOptions ?? string.Empty) != (saved.GameOptions ?? string.Empty)
        || (HasWarSettings && ((WarServer ?? string.Empty) != (saved.WarServer ?? string.Empty)
            || SettingDealSubfactions != (saved.DealSubfactions ?? true)
            || SettingDealSeaf != (saved.DealSeaf ?? true)
            || (int)WarSeed != (saved.WarSeed ?? 0)))
        || (HasSharingSettings && (SettingShareMods != (saved.ShareMods ?? true)
            || SettingListPublic != (saved.ListPublic ?? true)
            || SettingShowEditedStats != (saved.ShowEditedStats ?? false)
            || SettingHostStats != (saved.HostStats ?? false))));

    public bool HasLogs => LogRuns.Count > 0;

    public bool SupportsPlanets => _supportsPlanets;
    public bool IsCustomWarSelected => SelectedWar?.Value == "custom" && SupportsPlanets;
    public bool HasPlanets => Planets.Count > 0;
    public bool HasPlanetEditor => PlanetEditor != null;
    public bool IsPlanetDirty => PlanetEditor?.IsDirty == true;
    public bool CanSavePlanet => !IsBusy && HasPlanetEditor && IsPlanetDirty;
    public bool CanResetPlanet => !IsBusy && HasPlanetEditor && PlanetEditor?.HasOverride == true;
    public bool CanResetAllPlanets => !IsBusy && HasPlanets;
    public bool CanOpenPlanet => !IsBusy && SupportsPlanets && _planets != null && !string.IsNullOrWhiteSpace(PlanetQuery);
    public bool HasPlanetProblems => _planets?.Problems.Count > 0;
    public string PlanetProblemsText => HasPlanetProblems
        ? _localization["HellForge.Planets.Problems"] + "\n" + string.Join("\n", _planets!.Problems)
        : string.Empty;
    public string PlanetActiveText => _planets?.Active == true
        ? _localization["HellForge.Planets.Active"]
        : _localization["HellForge.Planets.Inactive"];
    public string PlanetWarText => _localization.Format("HellForge.Planets.War", _planets?.War ?? string.Empty);
    public string PlanetSessionText => _planets?.SessionRunning == true
        ? _localization["HellForge.Planets.SessionRunning"]
        : _localization["HellForge.Planets.SessionIdle"];

    public bool IsSessionActive => _session.State.IsActive;
    public bool HasSessionProblems => _session.State.Problems.Count > 0;
    public bool HasSessionNotice => _session.State.Restarting || !string.IsNullOrEmpty(_session.State.Notice);
    public string SessionProblemsText => _localization["HellForge.ProblemsTitle"] + "\n" +
        string.Join("\n", _session.State.Problems);
    public string SessionNoticeText => _session.State.Restarting
        ? _localization["HellForge.Session.Restarting"]
        : _session.State.Notice ?? string.Empty;
    public HellForgeModSyncOffer? ModSyncOffer => _session.State.ModSyncOffer;
    public bool HasModSyncOffer => ModSyncOffer != null;
    public string ModSyncTrustNote => ModSyncOffer?.TrustNote ?? string.Empty;
    public bool HasModSyncTrustNote => !string.IsNullOrWhiteSpace(ModSyncTrustNote);
    public bool CanAcceptModSync => ModSyncOffer?.Answers.Contains("accept") == true && !IsBusy;
    public bool CanRestartModSync => ModSyncOffer?.Answers.Contains("restart") == true && !IsBusy;
    public bool CanDeclineModSync => ModSyncOffer?.Answers.Contains("decline") == true && !IsBusy;
    public string ModSyncSizeText => ModSyncOffer == null
        ? string.Empty
        : _localization.Format("HellForge.ModSync.Size", FormatBytes(ModSyncOffer.Bytes));
    public string ModSyncProgressText => ModSyncOffer == null || ModSyncOffer.Bytes == 0
        ? string.Empty
        : _localization.Format("HellForge.ModSync.Progress", FormatBytes(ModSyncOffer.Done), FormatBytes(ModSyncOffer.Bytes));
    public string ModSyncHostText => ModSyncOffer == null
        ? string.Empty
        : _localization.Format("HellForge.ModSync.Host", ModSyncOffer.Host ?? string.Empty,
            ModSyncOffer.Stranger ? _localization["HellForge.ModSync.Stranger"] : _localization["HellForge.ModSync.Friend"]);
    public bool HasRestartIssue => _session.State.RestartIssue != HellForgeRestartIssue.None;
    public string RestartIssueText => _session.State.RestartIssue switch
    {
        HellForgeRestartIssue.None => string.Empty,
        HellForgeRestartIssue.UnsupportedPlatform => _localization["HellForge.Restart.UnsupportedPlatform"],
        HellForgeRestartIssue.MissingSyncFile => _localization.Format("HellForge.Restart.MissingSyncFile",
            _session.State.RestartIssueDetail ?? string.Empty),
        HellForgeRestartIssue.DryRunFailed => _localization.Format("HellForge.Restart.DryRunFailed",
            _session.State.RestartIssueDetail ?? string.Empty),
        HellForgeRestartIssue.StartFailed => _localization.Format("HellForge.Restart.StartFailed",
            _session.State.RestartIssueDetail ?? string.Empty),
        HellForgeRestartIssue.NewRunTimeout => _localization["HellForge.Restart.NewRunTimeout"],
        HellForgeRestartIssue.TooManyRestarts => _localization["HellForge.Restart.TooManyRestarts"],
        _ => string.Empty
    };

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
    public Task InitializeAsync() => OperateAsync("HellForge.Loading", ReloadAsync);

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task RefreshAsync() => OperateAsync("HellForge.Loading", ReloadAsync);

    /// <summary>状态/档案/设置/日志一次读齐；模组列表已加载过时顺带刷新，避免常驻额外 CLI 开销。</summary>
    private async Task ReloadAsync(CancellationToken token)
    {
        await LoadAsync(token);
        await _session.RefreshAsync(token);
        if (_modsLoaded) await LoadModsCoreAsync(token);
        if (_planetsLoaded) await LoadPlanetsCoreAsync(token);
    }

    /// <summary>模组分区首次进入时按需读取；CLI mods 会扫描整个 mods\，不放进页面初始化。</summary>
    [RelayCommand(CanExecute = nameof(CanRefreshMods))]
    private Task RefreshModsAsync() => OperateAsync("HellForge.Mods.Loading", LoadModsCoreAsync);

    private bool CanRefreshMods() => !IsBusy && IsInstallationConfigured;

    /// <summary>添加 HellForge 模组：视图选文件（压缩包或模组清单）后回调 AddModAsync。</summary>
    [RelayCommand(CanExecute = nameof(CanAddMod))]
    private void RequestAddMod() => AddModRequested?.Invoke(this, EventArgs.Empty);

    private bool CanAddMod() => !IsBusy && IsInstallationConfigured && !IsSessionActive;

    /// <summary>添加文件夹模组：先找清单再走 mod-add；找不到清单直接提示。</summary>
    public async Task AddModFromFolderAsync(string folder)
    {
        var manifest = new[] { "hellforge-mod.json", "manifest.json" }
            .Select(name => Path.Combine(folder, name))
            .FirstOrDefault(File.Exists);
        if (manifest == null)
        {
            ReportUiError(_localization["HellForge.Mods.FolderNoManifest"]);
            return;
        }
        await AddModAsync(manifest);
    }

    /// <summary>行内开关：开关变化经 mod-switch 落盘，失败或忙碌/会话中时回滚。</summary>
    private void OnModEnabledChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_suppressModEvents || sender is not HellForgeModItem item || !item.CanToggle) return;
        if (args.PropertyName != nameof(HellForgeModItem.Enabled)) return;
        if (IsBusy || IsSessionActive)
        {
            RevertModEnabled(item);
            return;
        }
        _ = ApplyModEnabledAsync(item);
    }

    private async Task ApplyModEnabledAsync(HellForgeModItem item)
    {
        var wanted = item.Enabled;
        await OperateAsync("HellForge.Mods.Switching", async token =>
        {
            var result = await _service.SwitchModAsync(item.Id, wanted, token);
            _suppressModEvents = true;
            try
            {
                item.Enabled = result.Enabled;
                item.MarkEnabledApplied();
            }
            finally { _suppressModEvents = false; }
            OnPropertyChanged(nameof(ModlistText));
        });
        if (_error != null) RevertModEnabled(item);
        else if (item.Group == "gameplay") ModsRefresh = RefreshModlistCodeAsync();
    }

    private void RevertModEnabled(HellForgeModItem item)
    {
        _suppressModEvents = true;
        try { item.Enabled = item.AppliedEnabled; }
        finally { _suppressModEvents = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveMod))]
    private Task RemoveModAsync(HellForgeModItem? item) => OperateAsync("HellForge.Mods.Removing", async token =>
    {
        if (item == null) return;
        var confirmed = await _confirmation.ConfirmAsync(
            _localization["HellForge.Mods.RemoveTitle"],
            _localization.Format("HellForge.Mods.RemoveConfirm", item.Name));
        if (!confirmed) return;
        await _service.RemoveModAsync(item.Id, token);
        HellForgeMods.Remove(item);
        UpdateModMoveFlags();
        OnPropertyChanged(nameof(HasMods));
        if (item.Group == "gameplay") ModsRefresh = RefreshModlistCodeAsync();
        _notificationService.ShowSuccess(
            _localization.Format("HellForge.Mods.Removed", item.Name), "HellForge");
    });

    private bool CanRemoveMod(HellForgeModItem? item) =>
        item is { Removable: true } && !IsBusy && !IsSessionActive;

    [RelayCommand(CanExecute = nameof(CanMoveModUp))]
    private Task MoveModUpAsync(HellForgeModItem? item) => MoveModAsync(item, -1);

    private bool CanMoveModUp(HellForgeModItem? item) => item?.CanMoveUp == true && !IsBusy && !IsSessionActive;

    [RelayCommand(CanExecute = nameof(CanMoveModDown))]
    private Task MoveModDownAsync(HellForgeModItem? item) => MoveModAsync(item, 1);

    private bool CanMoveModDown(HellForgeModItem? item) => item?.CanMoveDown == true && !IsBusy && !IsSessionActive;

    /// <summary>上/下移一格：只在同组内移动，发 mod-order 后本地同步顺序，避免再跑一次很慢的 mods。</summary>
    private Task MoveModAsync(HellForgeModItem? item, int delta)
    {
        if (item == null) return Task.CompletedTask;
        var index = HellForgeMods.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= HellForgeMods.Count) return Task.CompletedTask;
        if (HellForgeMods[target].Group != item.Group) return Task.CompletedTask;
        var ids = HellForgeMods.Select(mod => mod.Id).ToList();
        (ids[index], ids[target]) = (ids[target], ids[index]);
        return OperateAsync("HellForge.Mods.Reordering", async token =>
        {
            await _service.OrderModsAsync(ids, token);
            HellForgeMods.Move(index, target);
            UpdateModMoveFlags();
        });
    }

    /// <summary>视图层异常（文件对话框等）上报到通知，避免 async void 静默失败。</summary>
    public void ReportUiError(string message)
        => _notificationService.ShowError(_localization.Format("HellForge.Failed", message), "HellForge");

    /// <summary>直接写入 HellForge 的 mods\（CLI mod-add），不经过 HD2Mimnt 档案与部署流程。</summary>
    public Task AddModAsync(string file) => OperateAsync("HellForge.Mods.Adding", async token =>
    {
        var result = await _service.AddModAsync(file, token);
        await LoadModsCoreAsync(token);
        _notificationService.ShowSuccess(
            _localization.Format("HellForge.Mods.Added", result.Name), "HellForge");
    });

    private async Task EnsureModsLoadedAsync()
    {
        if (_modsLoaded || IsBusy || !IsInstallationConfigured) return;
        await OperateAsync("HellForge.Mods.Loading", LoadModsCoreAsync);
    }

    private async Task LoadModsCoreAsync(CancellationToken token)
    {
        _modsRefreshVersion++; // 手动刷新优先，丢弃之前启动的后台重读结果
        ApplyMods(await _service.GetModsAsync(token));
        _modsLoaded = true;
    }

    /// <summary>共享游戏性模组开关/移除后联机模组码会变；后台重读 mods（较慢），只保留最新一次结果。</summary>
    private async Task RefreshModlistCodeAsync()
    {
        var version = ++_modsRefreshVersion;
        try
        {
            var list = await _service.GetModsAsync();
            if (version == _modsRefreshVersion) ApplyMods(list);
        }
        catch (Exception)
        {
            // 读取失败时保留旧码；下次刷新列表会更新。
        }
    }

    partial void OnSelectedCategoryChanged(HellForgeCategoryItem? value)
    {
        OnPropertyChanged(nameof(IsOverviewCategoryVisible));
        OnPropertyChanged(nameof(IsSessionCategoryVisible));
        OnPropertyChanged(nameof(IsProfilesCategoryVisible));
        OnPropertyChanged(nameof(IsModsCategoryVisible));
        OnPropertyChanged(nameof(IsPlanetsCategoryVisible));
        OnPropertyChanged(nameof(IsSettingsCategoryVisible));
        OnPropertyChanged(nameof(IsLogsCategoryVisible));
        OnPropertyChanged(nameof(SelectedCategoryLabel));
        if (value?.Key == "Mods") _ = EnsureModsLoadedAsync();
        if (value?.Key == "Planets") _ = EnsurePlanetsLoadedAsync();
    }

    [RelayCommand]
    private void SelectCategory(string? key)
    {
        var category = Categories.FirstOrDefault(candidate => candidate.Key == key);
        if (category != null) SelectedCategory = category;
    }

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

    private bool CanStartSession() => !IsBusy && !IsStartingSession && !IsSessionActive
        && !_session.State.Restarting && IsInstallationConfigured;

    [RelayCommand(CanExecute = nameof(CanStopSession))]
    private Task StopSessionAsync() => OperateAsync("HellForge.Stopping", token => _session.StopAsync(token));

    private bool CanStopSession() => !IsBusy && IsSessionActive;

    [RelayCommand(CanExecute = nameof(CanAnswerModSync))]
    private Task AnswerModSync(string? answer)
    {
        var offer = ModSyncOffer;
        if (answer == null || offer == null || !offer.Answers.Contains(answer)) return Task.CompletedTask;
        return OperateAsync("HellForge.ModSync.Answering", token => _session.AnswerModSyncOfferAsync(answer, null, token));
    }

    private bool CanAnswerModSync() => !IsBusy && HasModSyncOffer;

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task TrustModSyncMod(HellForgeModSyncMod? mod)
    {
        if (mod == null || !mod.CanTrust) return Task.CompletedTask;
        return TrustModSyncCoreAsync(mod);
    }

    private async Task TrustModSyncCoreAsync(HellForgeModSyncMod mod)
    {
        var warning = string.Join("\n\n", new[] { mod.TrustWarning, ModSyncOffer?.TrustNote }
            .Where(text => !string.IsNullOrWhiteSpace(text)));
        var confirmed = await _confirmation.ConfirmAsync(_localization["HellForge.ModSync.TrustTitle"], warning);
        if (confirmed) await AnswerModSyncModCore("trust", mod.Id);
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task BlockModSyncMod(HellForgeModSyncMod? mod) =>
        mod != null && mod.CanBlock ? AnswerModSyncModCore("block", mod.Id) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task OpenModSyncMod(HellForgeModSyncMod? mod) =>
        mod != null && mod.CanOpen ? AnswerModSyncModCore("open", mod.Id) : Task.CompletedTask;

    private Task AnswerModSyncModCore(string answer, string modId) =>
        OperateAsync("HellForge.ModSync.Answering", token => _session.AnswerModSyncOfferAsync(answer, modId, token));

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
        if (saved.WarServer != null || saved.DealSubfactions.HasValue || saved.DealSeaf.HasValue || saved.WarSeed.HasValue)
        {
            var warServer = (WarServer ?? string.Empty).Trim();
            var savedServer = saved.WarServer ?? string.Empty;
            if (warServer != savedServer) changes["war-server"] = warServer.Length == 0 ? "default" : warServer;
            AddSwitch(changes, "deal-subfactions", SettingDealSubfactions, saved.DealSubfactions ?? true);
            AddSwitch(changes, "deal-seaf", SettingDealSeaf, saved.DealSeaf ?? true);
            if ((int)WarSeed != (saved.WarSeed ?? 0))
                changes["war-seed"] = ((int)WarSeed).ToString(CultureInfo.InvariantCulture);
        }
        if (saved.ShareMods.HasValue || saved.ListPublic.HasValue || saved.ShowEditedStats.HasValue || saved.HostStats.HasValue)
        {
            AddSwitch(changes, "share-mods", SettingShareMods, saved.ShareMods ?? true);
            AddSwitch(changes, "list-public", SettingListPublic, saved.ListPublic ?? true);
            AddSwitch(changes, "show-edited-stats", SettingShowEditedStats, saved.ShowEditedStats ?? false);
            AddSwitch(changes, "host-stats", SettingHostStats, saved.HostStats ?? false);
        }
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

    [RelayCommand(CanExecute = nameof(CanRefreshPlanets))]
    private Task RefreshPlanetsAsync() => OperateAsync("HellForge.Planets.Loading", LoadPlanetsCoreAsync);

    private bool CanRefreshPlanets() => !IsBusy && SupportsPlanets && IsInstallationConfigured;

    private async Task EnsurePlanetsLoadedAsync()
    {
        if (_planetsLoaded || IsBusy || !SupportsPlanets) return;
        await OperateAsync("HellForge.Planets.Loading", LoadPlanetsCoreAsync);
    }

    private async Task LoadPlanetsCoreAsync(CancellationToken token)
    {
        var result = await _service.GetPlanetsAsync(token);
        if (result == null)
        {
            _supportsPlanets = false;
            RebuildCategories();
            return;
        }
        ApplyPlanets(result);
        _planetsLoaded = true;
    }

    private void ApplyPlanets(HellForgePlanetsResult result, string? selectSelector = null, bool clearEditor = false)
    {
        _planets = result;
        Planets.Clear();
        foreach (var planet in result.Planets)
            Planets.Add(new HellForgePlanetItem(planet, PlanetDisplayName(planet), PlanetDetail(planet)));
        if (clearEditor)
        {
            PlanetEditor = null;
        }
        else if (PlanetEditor != null)
        {
            var selector = selectSelector ?? PlanetEditor.Selector;
            if (selectSelector != null || !IsPlanetDirty)
            {
                var selected = result.Planets.FirstOrDefault(planet =>
                    string.Equals(planet.Index.ToString(), selector, StringComparison.Ordinal)
                    || string.Equals(planet.Name, selector, StringComparison.OrdinalIgnoreCase));
                PlanetEditor = selected == null ? null : CreatePlanetEditor(selected);
            }
        }
        NotifyPlanetsState();
    }

    private static string PlanetDisplayName(HellForgePlanetInfo planet) => planet.Name ?? $"#{planet.Index}";

    private string PlanetDetail(HellForgePlanetInfo planet)
    {
        var parts = new List<string> { $"#{planet.Index}", PlanetFactionLabel(planet.Faction) };
        parts.Add(planet.UsesWarSubfactions
            ? _localization["HellForge.Planets.SubfactionsWarShort"]
            : _localization.Format("HellForge.Planets.SubfactionCount", planet.SubfactionKeys.Count));
        if (planet.Enemies.Count > 0)
            parts.Add(_localization.Format("HellForge.Planets.EnemyOverrideCount", planet.Enemies.Count));
        return string.Join(" · ", parts);
    }

    private HellForgePlanetEditor CreatePlanetEditor(HellForgePlanetInfo planet) =>
        new(planet, _planets?.Catalogue ?? new HellForgePlanetCatalogue(), planet.Index.ToString(), _localization);

    [RelayCommand(CanExecute = nameof(CanOpenPlanet))]
    private Task OpenPlanetAsync() => OperateAsync("HellForge.Planets.Loading", _ =>
    {
        var query = PlanetQuery.Trim();
        if (query.Length == 0 || _planets == null) return Task.CompletedTask;
        var existing = Planets.FirstOrDefault(planet =>
            string.Equals(planet.Info.Index.ToString(), query, StringComparison.Ordinal)
            || string.Equals(planet.Info.Name, query, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            PlanetEditor = CreatePlanetEditor(existing.Info);
            return Task.CompletedTask;
        }
        PlanetEditor = new HellForgePlanetEditor(null, _planets.Catalogue ?? new HellForgePlanetCatalogue(), query, _localization);
        NotifyPlanetsState();
        return Task.CompletedTask;
    });

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void SelectPlanet(HellForgePlanetItem? planet)
    {
        if (planet == null || IsBusy) return;
        PlanetEditor = CreatePlanetEditor(planet.Info);
        PlanetQuery = PlanetDisplayName(planet.Info);
    }

    [RelayCommand(CanExecute = nameof(CanSavePlanet))]
    private Task SavePlanetAsync() => OperateAsync("HellForge.Planets.Saving", async token =>
    {
        var editor = PlanetEditor;
        if (editor == null) return;
        var result = await _service.UpdatePlanetAsync(editor.Selector, editor.SelectedFaction!.Value,
            editor.SelectedSubfactions, editor.EnemyOverrides, cancellationToken: token);
        ApplyPlanets(result, editor.Selector);
        _notificationService.ShowSuccess(_localization["HellForge.Planets.Saved"], "HellForge");
    });

    [RelayCommand(CanExecute = nameof(CanResetPlanet))]
    private Task ResetPlanetAsync() => OperateAsync("HellForge.Planets.Resetting", async token =>
    {
        var editor = PlanetEditor;
        if (editor == null) return;
        var confirmed = await _confirmation.ConfirmAsync(_localization["HellForge.Planets.ResetTitle"],
            _localization.Format("HellForge.Planets.ResetConfirm", editor.Name));
        if (!confirmed) return;
        var result = await _service.UpdatePlanetAsync(editor.Selector, "war", null, new Dictionary<string, string>(),
            reset: true, cancellationToken: token);
        ApplyPlanets(result, clearEditor: true);
        _notificationService.ShowSuccess(_localization["HellForge.Planets.ResetDone"], "HellForge");
    });

    [RelayCommand(CanExecute = nameof(CanResetAllPlanets))]
    private Task ResetAllPlanetsAsync() => OperateAsync("HellForge.Planets.Resetting", async token =>
    {
        var confirmed = await _confirmation.ConfirmAsync(_localization["HellForge.Planets.ResetAllTitle"],
            _localization["HellForge.Planets.ResetAllConfirm"]);
        if (!confirmed) return;
        var result = await _service.ResetAllPlanetsAsync(token);
        ApplyPlanets(result, clearEditor: true);
        _notificationService.ShowSuccess(_localization["HellForge.Planets.ResetAllDone"], "HellForge");
    });

    private string PlanetFactionLabel(string? faction) => faction switch
    {
        null or "war" => _localization["HellForge.Planets.Faction.War"],
        "terminids" => _localization["HellForge.Planets.Faction.Terminids"],
        "automatons" => _localization["HellForge.Planets.Faction.Automatons"],
        "illuminate" => _localization["HellForge.Planets.Faction.Illuminate"],
        _ => faction
    };

    partial void OnPlanetEditorChanged(HellForgePlanetEditor? value)
    {
        if (_observedPlanetEditor != null) _observedPlanetEditor.DirtyChanged -= OnPlanetEditorDirtyChanged;
        _observedPlanetEditor = value;
        if (value != null) value.DirtyChanged += OnPlanetEditorDirtyChanged;
        NotifyPlanetsState();
    }

    partial void OnPlanetQueryChanged(string value) => OpenPlanetCommand.NotifyCanExecuteChanged();

    private void OnPlanetEditorDirtyChanged(object? sender, EventArgs e) => NotifyPlanetsState();

    private void NotifyPlanetsState()
    {
        OnPropertyChanged(nameof(SupportsPlanets));
        OnPropertyChanged(nameof(IsCustomWarSelected));
        OnPropertyChanged(nameof(HasPlanets));
        OnPropertyChanged(nameof(HasPlanetEditor));
        OnPropertyChanged(nameof(IsPlanetDirty));
        OnPropertyChanged(nameof(CanSavePlanet));
        OnPropertyChanged(nameof(CanResetPlanet));
        OnPropertyChanged(nameof(CanResetAllPlanets));
        OnPropertyChanged(nameof(CanOpenPlanet));
        OnPropertyChanged(nameof(HasPlanetProblems));
        OnPropertyChanged(nameof(PlanetProblemsText));
        OnPropertyChanged(nameof(PlanetActiveText));
        OnPropertyChanged(nameof(PlanetWarText));
        OnPropertyChanged(nameof(PlanetSessionText));
        SavePlanetCommand.NotifyCanExecuteChanged();
        ResetPlanetCommand.NotifyCanExecuteChanged();
        ResetAllPlanetsCommand.NotifyCanExecuteChanged();
        OpenPlanetCommand.NotifyCanExecuteChanged();
        RefreshPlanetsCommand.NotifyCanExecuteChanged();
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
        try { _versions = await _service.GetVersionsAsync(token); }
        catch (InvalidOperationException) { _versions = null; }
        _supportsPlanets = await _service.SupportsCommandAsync("planets", token);
        RebuildCategories();
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
        WarServer = settings.WarServer ?? string.Empty;
        SettingDealSubfactions = settings.DealSubfactions ?? true;
        SettingDealSeaf = settings.DealSeaf ?? true;
        WarSeed = settings.WarSeed ?? 0;
        SettingShareMods = settings.ShareMods ?? true;
        SettingListPublic = settings.ListPublic ?? true;
        SettingShowEditedStats = settings.ShowEditedStats ?? false;
        SettingHostStats = settings.HostStats ?? false;
        _supportsModernWar = settings.WarServer != null || settings.DealSubfactions.HasValue
            || settings.DealSeaf.HasValue || settings.WarSeed.HasValue || settings.War is "hellforge" or "custom";
        RebuildChoiceLabels();
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
        var wars = _supportsModernWar
            ? new[] { "hellforge", "live", "frozen", "open", "custom" }
            : new[] { "live", "frozen", "open" };
        foreach (var value in wars)
            WarOptions.Add(new HellForgeChoice(value, _localization["HellForge.War." + Capitalize(value)]));
        if (!string.IsNullOrEmpty(war) && WarOptions.All(option => option.Value != war))
            WarOptions.Add(new HellForgeChoice(war, war));
        StrangersOptions.Clear();
        foreach (var value in new[] { "default", "yes", "no" })
            StrangersOptions.Add(new HellForgeChoice(value, _localization["HellForge.Strangers." + Capitalize(value)]));
        SelectedWar = WarOptions.FirstOrDefault(o => o.Value == war) ?? WarOptions.FirstOrDefault();
        SelectedStrangers = StrangersOptions.FirstOrDefault(o => o.Value == strangers) ?? StrangersOptions.FirstOrDefault();
    }

    private static string Capitalize(string value) => char.ToUpperInvariant(value[0]) + value[1..];

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.##} GiB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):0.##} MiB",
        >= 1024L => $"{bytes / 1024d:0.##} KiB",
        _ => $"{bytes} B"
    };

    /// <summary>重建分类标签并保留当前分类（语言切换时调用）。</summary>
    private void RebuildCategories()
    {
        var key = SelectedCategory?.Key ?? "Profiles";
        Categories.Clear();
        var categories = new List<(string Key, string Resource)>
        {
            ("Overview", "HellForge.Category.Overview"),
            ("Session", "HellForge.Category.Session"),
            ("Profiles", "HellForge.Category.Profiles"),
            ("Mods", "HellForge.Category.Mods")
        };
        if (_supportsPlanets) categories.Add(("Planets", "HellForge.Category.Planets"));
        categories.Add(("Settings", "HellForge.Category.Settings"));
        categories.Add(("Logs", "HellForge.Category.Logs"));
        foreach (var (categoryKey, resource) in categories)
            Categories.Add(new HellForgeCategoryItem(categoryKey, _localization[resource]));
        SelectedCategory = Categories.FirstOrDefault(category => category.Key == key)
            ?? Categories.First(category => category.Key == "Profiles");
        OnPropertyChanged(nameof(OverviewLabel));
        OnPropertyChanged(nameof(SessionLabel));
        OnPropertyChanged(nameof(ProfilesLabel));
        OnPropertyChanged(nameof(ModsLabel));
        OnPropertyChanged(nameof(PlanetsLabel));
        OnPropertyChanged(nameof(SettingsLabel));
        OnPropertyChanged(nameof(LogsLabel));
    }

    /// <summary>HF 模组列表来自 CLI mods（mods\ 的权威列表）；按客户端分组顺序展示（游戏性在上）。</summary>
    private void ApplyMods(HellForgeModsList list)
    {
        _modsList = list;
        HellForgeMods.Clear();
        foreach (var mod in list.Mods
                     .Where(mod => !mod.Tags.Contains("local-stats", StringComparer.OrdinalIgnoreCase))
                     .OrderBy(mod => GroupRank(mod.Group)))
            HellForgeMods.Add(CreateModItem(mod));
        RebuildModRows();
        UpdateModMoveFlags();
        OnPropertyChanged(nameof(HasMods));
        OnPropertyChanged(nameof(HasModProblems));
        OnPropertyChanged(nameof(ModProblemsText));
        OnPropertyChanged(nameof(HasModlistCode));
        OnPropertyChanged(nameof(ModlistCode));
        OnPropertyChanged(nameof(ModlistText));
        OnPropertyChanged(nameof(ModsDirectory));
        // 模组变化后旧比对结果不再可信：保留输入，清掉结果。
        _modlistCompareOutcome = ModlistCompareOutcome.None;
        NotifyModlistCompareChanged();
        RemoveModCommand.NotifyCanExecuteChanged();
    }

    /// <summary>比对好友的联机模组码：一致/不一致/输入无效/本机没有可比对的码。</summary>
    [RelayCommand]
    private void CompareModlist()
    {
        var code = ModlistCompareInput.Trim();
        _modlistCompareOutcome = code.Length != 8 || !code.All(Uri.IsHexDigit)
            ? ModlistCompareOutcome.Invalid
            : !HasModlistCode
                ? ModlistCompareOutcome.NoLocal
                : string.Equals(code, ModlistCode, StringComparison.OrdinalIgnoreCase)
                    ? ModlistCompareOutcome.Match
                    : ModlistCompareOutcome.Mismatch;
        NotifyModlistCompareChanged();
    }

    partial void OnModlistCompareInputChanged(string value)
    {
        if (_modlistCompareOutcome == ModlistCompareOutcome.None) return;
        _modlistCompareOutcome = ModlistCompareOutcome.None;
        NotifyModlistCompareChanged();
    }

    private void NotifyModlistCompareChanged()
    {
        OnPropertyChanged(nameof(HasModlistCompareResult));
        OnPropertyChanged(nameof(IsModlistCompareMatch));
        OnPropertyChanged(nameof(IsModlistCompareProblem));
        OnPropertyChanged(nameof(ModlistCompareResultText));
    }

    /// <summary>视图把联机模组码写进剪贴板后的反馈。</summary>
    public void ReportModlistCopied() =>
        _notificationService.ShowSuccess(_localization["HellForge.Mods.Copied"], "HellForge");

    /// <summary>扁平列表 + 分组标题，整体重建（标题里带该组数量）。</summary>
    private void RebuildModRows()
    {
        ModRows.Clear();
        string? current = null;
        foreach (var mod in HellForgeMods)
        {
            if (mod.Group != current)
            {
                current = mod.Group;
                ModRows.Add(new HellForgeModGroupHeader(GroupText(mod.Group),
                    HellForgeMods.Count(candidate => candidate.Group == mod.Group)));
            }
            ModRows.Add(mod);
        }
    }

    private HellForgeModItem CreateModItem(HellForgeModInfo mod)
    {
        var detail = new List<string> { mod.Id };
        if (!string.IsNullOrEmpty(mod.Version)) detail.Add($"v{mod.Version}");
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(mod.Unsupported))
            warnings.Add(_localization.Format("HellForge.Mods.Unsupported", mod.Unsupported));
        if (mod.Premium?.Count > 0)
            warnings.Add(_localization.Format("HellForge.Mods.Premium",
                string.Join(", ", mod.Premium.Select(item => item.Name ?? item.Requirement).Where(name => !string.IsNullOrEmpty(name)))));
        var build = mod.Build == null ? string.Empty : _localization.Format("HellForge.Mods.Build",
            _localization["HellForge.Mods.BuildState." + BuildStateKey(mod.Build.State)],
            mod.Build.Error ?? string.Empty);
        var item = new HellForgeModItem(mod.Id, mod.Name, mod.Group ?? string.Empty, mod.Removable,
            mod.Enabled, string.Join(" · ", detail), mod.Tags, mod.Description ?? string.Empty,
            string.Join("\n", warnings), build, string.IsNullOrWhiteSpace(mod.Unsupported));
        item.PropertyChanged += OnModEnabledChanged;
        foreach (var option in mod.Options ?? [])
        {
            var optionItem = new HellForgePackOptionItem(mod.Id, option.Name, option.On,
                option.Variants ?? [], option.Picked);
            optionItem.PropertyChanged += OnPackOptionChanged;
            item.Options.Add(optionItem);
        }
        return item;
    }

    /// <summary>外观包选项变化：经 mod-pack 落盘，失败或忙碌时回滚勾选状态。</summary>
    private void OnPackOptionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_suppressPackEvents || sender is not HellForgePackOptionItem option) return;
        if (args.PropertyName is not (nameof(HellForgePackOptionItem.On) or nameof(HellForgePackOptionItem.Picked))) return;
        if (IsBusy || IsSessionActive)
        {
            RevertPackOption(option);
            return;
        }
        _ = ApplyPackOptionAsync(option, args.PropertyName);
    }

    private async Task ApplyPackOptionAsync(HellForgePackOptionItem option, string property)
    {
        var wantedOn = option.On;
        var wantedPicked = option.Picked;
        await OperateAsync("HellForge.Mods.OptionUpdate", async token =>
        {
            var result = property == nameof(HellForgePackOptionItem.On)
                ? await _service.SetPackOptionAsync(option.ModId, option.Name, on: wantedOn, cancellationToken: token)
                : await _service.SetPackOptionAsync(option.ModId, option.Name, variant: wantedPicked, cancellationToken: token);
            ApplyPackOptions(option.ModId, result.Options);
        });
        if (_error != null) RevertPackOption(option);
    }

    private void ApplyPackOptions(string modId, List<HellForgeModOption> options)
    {
        var item = HellForgeMods.FirstOrDefault(mod => mod.Id == modId);
        if (item == null) return;
        _suppressPackEvents = true;
        try
        {
            foreach (var option in options)
            {
                var target = item.Options.FirstOrDefault(candidate => candidate.Name == option.Name);
                if (target == null) continue;
                target.On = option.On;
                target.Picked = option.Picked;
                target.MarkApplied();
            }
        }
        finally { _suppressPackEvents = false; }
    }

    private void RevertPackOption(HellForgePackOptionItem option)
    {
        _suppressPackEvents = true;
        try
        {
            option.On = option.AppliedOn;
            option.Picked = option.AppliedPicked;
        }
        finally { _suppressPackEvents = false; }
    }

    private void UpdateModMoveFlags()
    {
        for (var index = 0; index < HellForgeMods.Count; index++)
        {
            var item = HellForgeMods[index];
            item.CanMoveUp = index > 0 && HellForgeMods[index - 1].Group == item.Group;
            item.CanMoveDown = index + 1 < HellForgeMods.Count && HellForgeMods[index + 1].Group == item.Group;
        }
        MoveModUpCommand.NotifyCanExecuteChanged();
        MoveModDownCommand.NotifyCanExecuteChanged();
        RemoveModCommand.NotifyCanExecuteChanged();
    }

    private static string BuildStateKey(string? state) => state switch
    {
        "not-built" => "NotBuilt",
        "built" => "Built",
        "did-not-compile" => "DidNotCompile",
        "tools-failed" => "ToolsFailed",
        "changed" => "Changed",
        "unreadable" => "Unreadable",
        _ => "Unknown"
    };

    private static int GroupRank(string? group) => group switch
    {
        "gameplay" => 0,
        "per-player" => 1,
        "cosmetic" => 2,
        _ => 3
    };

    private string GroupText(string? group) => group switch
    {
        "gameplay" => _localization["HellForge.Mods.Group.Gameplay"],
        "per-player" => _localization["HellForge.Mods.Group.PerPlayer"],
        "cosmetic" => _localization["HellForge.Mods.Group.Cosmetic"],
        _ => group ?? string.Empty
    };

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
        OnPropertyChanged(nameof(HasWarSettings));
        OnPropertyChanged(nameof(HasSharingSettings));
        OnPropertyChanged(nameof(IsSettingsDirty));
        OnPropertyChanged(nameof(IsCustomWarSelected));
    }

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        SwitchProfileCommand.NotifyCanExecuteChanged();
        OpenCreateDialogCommand.NotifyCanExecuteChanged();
        CreateProfileCommand.NotifyCanExecuteChanged();
        StartSessionCommand.NotifyCanExecuteChanged();
        StopSessionCommand.NotifyCanExecuteChanged();
        AnswerModSyncCommand.NotifyCanExecuteChanged();
        TrustModSyncModCommand.NotifyCanExecuteChanged();
        BlockModSyncModCommand.NotifyCanExecuteChanged();
        OpenModSyncModCommand.NotifyCanExecuteChanged();
        SaveSettingsCommand.NotifyCanExecuteChanged();
        RequestExportLogsCommand.NotifyCanExecuteChanged();
        RequestCrashReportCommand.NotifyCanExecuteChanged();
        RefreshModsCommand.NotifyCanExecuteChanged();
        RequestAddModCommand.NotifyCanExecuteChanged();
        RemoveModCommand.NotifyCanExecuteChanged();
        MoveModUpCommand.NotifyCanExecuteChanged();
        MoveModDownCommand.NotifyCanExecuteChanged();
        SavePlanetCommand.NotifyCanExecuteChanged();
        ResetPlanetCommand.NotifyCanExecuteChanged();
        ResetAllPlanetsCommand.NotifyCanExecuteChanged();
        OpenPlanetCommand.NotifyCanExecuteChanged();
        RefreshPlanetsCommand.NotifyCanExecuteChanged();
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
            case nameof(WarServer):
            case nameof(SettingDealSubfactions):
            case nameof(SettingDealSeaf):
            case nameof(WarSeed):
            case nameof(SettingShareMods):
            case nameof(SettingListPublic):
            case nameof(SettingShowEditedStats):
            case nameof(SettingHostStats):
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
        else if (!_sessionEndedNotified && !state.Restarting && state.Phase is HellForgeSessionPhase.Ended or HellForgeSessionPhase.Failed)
        {
            _sessionEndedNotified = true;
            IsStartingSession = false;
            if (state.Phase == HellForgeSessionPhase.Ended && state.RestartIssue != HellForgeRestartIssue.None)
                _notificationService.ShowError(RestartIssueText, "HellForge");
            else if (state.Phase == HellForgeSessionPhase.Ended)
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
        OnPropertyChanged(nameof(HasRestartIssue));
        OnPropertyChanged(nameof(RestartIssueText));
        OnPropertyChanged(nameof(HasModSyncOffer));
        OnPropertyChanged(nameof(ModSyncOffer));
        OnPropertyChanged(nameof(ModSyncTrustNote));
        OnPropertyChanged(nameof(HasModSyncTrustNote));
        OnPropertyChanged(nameof(CanAcceptModSync));
        OnPropertyChanged(nameof(CanRestartModSync));
        OnPropertyChanged(nameof(CanDeclineModSync));
        OnPropertyChanged(nameof(ModSyncSizeText));
        OnPropertyChanged(nameof(ModSyncProgressText));
        OnPropertyChanged(nameof(ModSyncHostText));
        StartSessionCommand.NotifyCanExecuteChanged();
        StopSessionCommand.NotifyCanExecuteChanged();
        AnswerModSyncCommand.NotifyCanExecuteChanged();
        TrustModSyncModCommand.NotifyCanExecuteChanged();
        BlockModSyncModCommand.NotifyCanExecuteChanged();
        OpenModSyncModCommand.NotifyCanExecuteChanged();
        RequestAddModCommand.NotifyCanExecuteChanged();
        RemoveModCommand.NotifyCanExecuteChanged();
        MoveModUpCommand.NotifyCanExecuteChanged();
        MoveModDownCommand.NotifyCanExecuteChanged();
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
        finally
        {
            _cancellation = null;
            IsBusy = false;
            completion.TrySetResult();
            // 页面加载期间切到模组/星球分区会被忙碌拦截，这里补一次按需加载；失败不再自动重试（避免循环）。
            if (_error == null && SelectedCategory?.Key == "Mods" && !_modsLoaded) _ = EnsureModsLoadedAsync();
            if (_error == null && SelectedCategory?.Key == "Planets" && !_planetsLoaded) _ = EnsurePlanetsLoadedAsync();
        }
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(UiLocalization.Revision)) return;
        Refresh();
        UpdateSessionProgress();
        RebuildChoiceLabels();
        RebuildCategories();
        if (_logs != null) ApplyLogs(_logs);
        if (_modsList != null) ApplyMods(_modsList);
        if (_planets != null)
        {
            Planets.Clear();
            foreach (var planet in _planets.Planets)
                Planets.Add(new HellForgePlanetItem(planet, PlanetDisplayName(planet), PlanetDetail(planet)));
            PlanetEditor?.RebuildLabels(_localization);
            NotifyPlanetsState();
        }
    }

    private void Refresh()
    {
        foreach (var property in new[] { nameof(StatusMessage), nameof(IsInstallationConfigured), nameof(HasStatus),
            nameof(HasProfiles), nameof(HellForgeVersion), nameof(PlayingVersionText), nameof(LanguagesText),
            nameof(GameBuild), nameof(EngineVersion), nameof(ModSdkVersion), nameof(NetworkBlock), nameof(CurrentProfile), nameof(IsSessionRunning),
            nameof(IsGameCopyReady), nameof(GameCopyText), nameof(SessionText),
            nameof(HasSync), nameof(SyncedModCount), nameof(SyncStatusText), nameof(SyncSourceText),
            nameof(HasSettings), nameof(IsSettingsDirty), nameof(HasLogs),
            nameof(SupportsPlanets), nameof(HasPlanets), nameof(HasPlanetEditor), nameof(IsPlanetDirty),
            nameof(HasPlanetProblems), nameof(PlanetProblemsText), nameof(PlanetActiveText), nameof(PlanetWarText), nameof(PlanetSessionText),
            nameof(HasMods), nameof(HasModProblems), nameof(ModProblemsText),
            nameof(HasModlistCode), nameof(ModlistCode), nameof(ModlistText), nameof(ModsDirectory),
            nameof(HasModlistCompareResult), nameof(IsModlistCompareMatch), nameof(IsModlistCompareProblem),
            nameof(ModlistCompareResultText), nameof(HasRestartIssue), nameof(RestartIssueText) })
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
        if (_observedPlanetEditor != null) _observedPlanetEditor.DirtyChanged -= OnPlanetEditorDirtyChanged;
    }
}

public sealed partial class HellForgePlanetEditor : ObservableObject
{
    private readonly string _originalFaction;
    private readonly bool _originalUseWarSubfactions;
    private readonly HashSet<string> _originalSubfactions;
    private readonly Dictionary<string, string> _originalEnemies;
    private readonly string? _servedEnemy;
    private readonly bool _isHomeWorld;
    private readonly bool _isSuperEarth;
    private bool _suppressDirty;

    public event EventHandler? DirtyChanged;

    public HellForgePlanetEditor(HellForgePlanetInfo? original, HellForgePlanetCatalogue catalogue,
        string selector, UiLocalization localization)
    {
        Selector = selector;
        Name = original?.Name ?? selector;
        Index = original?.Index ?? (int.TryParse(selector, out var parsed) ? parsed : null);
        _isSuperEarth = Index == 0 || string.Equals(Name, "Super Earth", StringComparison.OrdinalIgnoreCase);
        _isHomeWorld = _isSuperEarth || string.Equals(Name, "Cyberstan", StringComparison.OrdinalIgnoreCase);
        HasOverride = original != null;
        _originalFaction = original?.Faction ?? "war";
        _originalUseWarSubfactions = original?.UsesWarSubfactions ?? true;
        _originalSubfactions = original?.SubfactionKeys.ToHashSet() ?? new HashSet<string>();
        _originalEnemies = original?.Enemies.ToDictionary(pair => pair.Key, pair => pair.Value) ?? new Dictionary<string, string>();
        _servedEnemy = original?.Served?.Enemy;
        foreach (var entry in catalogue.Subfactions)
        {
            var item = new HellForgePlanetSubfactionItem(entry.Key, entry.Name ?? entry.Key, entry.Faction ?? "any",
                _originalSubfactions.Contains(entry.Key));
            item.PropertyChanged += OnSubfactionChanged;
            Subfactions.Add(item);
        }
        foreach (var entry in catalogue.Enemies)
        {
            var item = new HellForgePlanetEnemyItem(entry.Key, entry.Name ?? entry.Key, entry.Faction ?? "any");
            item.PropertyChanged += OnEnemyChanged;
            Enemies.Add(item);
        }
        RebuildLabels(localization);
        UseWarSubfactions = _originalUseWarSubfactions;
        UpdateVisibility();
    }

    public string Selector { get; }
    public string Name { get; }
    public int? Index { get; }
    public bool HasOverride { get; }
    public ObservableCollection<HellForgeChoice> FactionOptions { get; } = new();
    public ObservableCollection<HellForgeChoice> EnemyModeOptions { get; } = new();
    public ObservableCollection<HellForgePlanetSubfactionItem> Subfactions { get; } = new();
    public ObservableCollection<HellForgePlanetEnemyItem> Enemies { get; } = new();
    [ObservableProperty] private HellForgeChoice? _selectedFaction;
    [ObservableProperty] private bool _useWarSubfactions;

    public bool CanEditFaction => !_isHomeWorld;
    public bool CanEditSubfactions => !_isSuperEarth;
    public bool CanEditEnemies => !_isSuperEarth;
    public string? EffectiveFaction => SelectedFaction?.Value is { } faction && faction != "war" ? faction : _servedEnemy;
    public bool HasSubfactionChoices => CanEditSubfactions && !UseWarSubfactions && !string.IsNullOrEmpty(EffectiveFaction)
        && Subfactions.Any(item => item.IsVisible);
    public bool HasEnemyChoices => CanEditEnemies && !string.IsNullOrEmpty(EffectiveFaction)
        && Enemies.Any(item => item.IsVisible);
    public bool ShowChooseFactionHint => CanEditSubfactions && string.IsNullOrEmpty(EffectiveFaction);

    public bool IsDirty => SelectedFaction != null && (
        SelectedFaction.Value != _originalFaction
        || UseWarSubfactions != _originalUseWarSubfactions
        || (!UseWarSubfactions && !_originalSubfactions.SetEquals(Subfactions.Where(item => item.IsVisible && item.IsChecked).Select(item => item.Key)))
        || Enemies.Where(item => item.IsVisible).Any(item => item.SelectedMode?.Value != _originalEnemies.GetValueOrDefault(item.Key, "normal")));

    public IReadOnlyList<string>? SelectedSubfactions => !CanEditSubfactions || UseWarSubfactions
        ? null
        : Subfactions.Where(item => item.IsVisible && item.IsChecked).Select(item => item.Key).ToArray();

    public IReadOnlyDictionary<string, string> EnemyOverrides
    {
        get
        {
            var result = new Dictionary<string, string>(_originalEnemies);
            if (!CanEditEnemies) return result;
            foreach (var item in Enemies.Where(item => item.IsVisible))
            {
                result[item.Key] = item.SelectedMode?.Value ?? "normal";
            }
            return result;
        }
    }

    public void RebuildLabels(UiLocalization localization)
    {
        var faction = SelectedFaction?.Value ?? _originalFaction;
        var modes = Enemies.Select(item => item.SelectedMode?.Value).ToArray();
        _suppressDirty = true;
        try
        {
            FactionOptions.Clear();
            FactionOptions.Add(new HellForgeChoice("war", localization["HellForge.Planets.Faction.War"]));
            foreach (var key in new[] { "terminids", "automatons", "illuminate" })
                FactionOptions.Add(new HellForgeChoice(key, localization["HellForge.Planets.Faction." + Capitalize(key)]));
            EnemyModeOptions.Clear();
            foreach (var value in new[] { "normal", "surge", "none" })
                EnemyModeOptions.Add(new HellForgeChoice(value, localization["HellForge.Planets.EnemyMode." + Capitalize(value)]));
            SelectedFaction = FactionOptions.FirstOrDefault(option => option.Value == faction) ?? FactionOptions[0];
            for (var i = 0; i < Enemies.Count; i++)
                Enemies[i].SelectedMode = EnemyModeOptions.FirstOrDefault(option => option.Value == modes[i])
                    ?? EnemyModeOptions.FirstOrDefault(option => option.Value == _originalEnemies.GetValueOrDefault(Enemies[i].Key, "normal"))
                    ?? EnemyModeOptions[0];
        }
        finally { _suppressDirty = false; }
        UpdateVisibility();
    }

    partial void OnSelectedFactionChanged(HellForgeChoice? value)
    {
        UpdateVisibility();
        RaiseDirtyChanged();
    }

    partial void OnUseWarSubfactionsChanged(bool value)
    {
        OnPropertyChanged(nameof(HasSubfactionChoices));
        RaiseDirtyChanged();
    }

    private void OnSubfactionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HellForgePlanetSubfactionItem.IsChecked)) RaiseDirtyChanged();
    }

    private void OnEnemyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HellForgePlanetEnemyItem.SelectedMode)) RaiseDirtyChanged();
    }

    private void UpdateVisibility()
    {
        var faction = EffectiveFaction;
        foreach (var item in Subfactions)
            item.IsVisible = !string.IsNullOrEmpty(faction) && (item.Faction == faction || item.Faction == "any");
        foreach (var item in Enemies)
            item.IsVisible = !string.IsNullOrEmpty(faction) && item.Faction == faction;
        OnPropertyChanged(nameof(HasSubfactionChoices));
        OnPropertyChanged(nameof(HasEnemyChoices));
        OnPropertyChanged(nameof(ShowChooseFactionHint));
        OnPropertyChanged(nameof(CanEditFaction));
        OnPropertyChanged(nameof(CanEditSubfactions));
        OnPropertyChanged(nameof(CanEditEnemies));
    }

    private void RaiseDirtyChanged()
    {
        if (!_suppressDirty) DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Capitalize(string value) => char.ToUpperInvariant(value[0]) + value[1..];
}

public sealed partial class HellForgePlanetSubfactionItem : ObservableObject
{
    public HellForgePlanetSubfactionItem(string key, string name, string faction, bool isChecked)
    {
        Key = key;
        Name = name;
        Faction = faction;
        _isChecked = isChecked;
    }

    public string Key { get; }
    public string Name { get; }
    public string Faction { get; }
    public bool IsVisible { get; set; }
    [ObservableProperty] private bool _isChecked;
}

public sealed partial class HellForgePlanetEnemyItem : ObservableObject
{
    public HellForgePlanetEnemyItem(string key, string name, string faction)
    {
        Key = key;
        Name = name;
        Faction = faction;
    }

    public string Key { get; }
    public string Name { get; }
    public string Faction { get; }
    public bool IsVisible { get; set; }
    [ObservableProperty] private HellForgeChoice? _selectedMode;
}

public sealed record HellForgePlanetItem(HellForgePlanetInfo Info, string DisplayName, string DetailText);

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

/// <summary>HF 专页左侧分类；Key 稳定，Label 随语言重建。</summary>
public sealed record HellForgeCategoryItem(string Key, string Label);

/// <summary>模组分组标题（GAMEPLAY / PER PLAYER / CLIENT ONLY），与模组行混排。</summary>
public sealed record HellForgeModGroupHeader(string Title, int Count);

/// <summary>HellForge mods\ 列表的一行（CLI mods 的权威列表）；随刷新整体重建。</summary>
public sealed partial class HellForgeModItem : ObservableObject
{
    public HellForgeModItem(string id, string name, string group, bool removable, bool enabled,
        string detailText, IReadOnlyList<string> tags, string description = "", string warningText = "", string buildText = "",
        bool canToggle = true)
    {
        Id = id;
        Name = name;
        Group = group;
        Removable = removable;
        DetailText = detailText;
        Description = description;
        WarningText = warningText;
        BuildText = buildText;
        CanToggle = canToggle;
        Tags = tags.Select(tag => tag.ToUpperInvariant()).ToArray();
        _enabled = enabled;
        AppliedEnabled = enabled;
    }

    public string Id { get; }
    public string Name { get; }
    /// <summary>CLI 分组（gameplay / per-player / cosmetic）；只允许同组内调整顺序。</summary>
    public string Group { get; }
    public bool Removable { get; }
    public string DetailText { get; }
    public string Description { get; }
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public string WarningText { get; }
    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);
    public string BuildText { get; }
    public bool HasBuild => !string.IsNullOrWhiteSpace(BuildText);
    public bool CanToggle { get; }
    /// <summary>CLI tags（大写，如 BUNDLED / CODE / DATA / ZIP / PACK / LUA）。</summary>
    public IReadOnlyList<string> Tags { get; }
    public bool HasTags => Tags.Count > 0;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _canMoveUp;
    [ObservableProperty] private bool _canMoveDown;
    public ObservableCollection<HellForgePackOptionItem> Options { get; } = new();
    public bool HasOptions => Options.Count > 0;

    /// <summary>最近一次 CLI 已确认的开关值，失败或忙碌冲突时回滚用。</summary>
    public bool AppliedEnabled { get; private set; }

    public void MarkEnabledApplied() => AppliedEnabled = Enabled;
}

/// <summary>外观包的一个选项：On 开关与子选项选择；变化后经 mod-pack 落盘。</summary>
public sealed partial class HellForgePackOptionItem : ObservableObject
{
    public HellForgePackOptionItem(string modId, string name, bool on, IReadOnlyList<string> variants, string? picked)
    {
        ModId = modId;
        Name = name;
        _on = on;
        _picked = picked;
        AppliedOn = on;
        AppliedPicked = picked;
        foreach (var variant in variants) Variants.Add(variant);
    }

    public string ModId { get; }
    public string Name { get; }
    public ObservableCollection<string> Variants { get; } = new();
    public bool HasVariants => Variants.Count > 0;
    [ObservableProperty] private bool _on;
    [ObservableProperty] private string? _picked;

    /// <summary>最近一次 CLI 已确认的值，失败或忙碌冲突时回滚用。</summary>
    public bool AppliedOn { get; private set; }
    public string? AppliedPicked { get; private set; }

    public void MarkApplied()
    {
        AppliedOn = On;
        AppliedPicked = Picked;
    }
}

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