using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.PluginSDK;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Views;
using System.Collections.ObjectModel;
using System.IO;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.Services;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace HD2Mimnt.UI.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    public ModCompatibilityViewModel? Compatibility { get; }
    public Task StartupCompatibilityTask { get; private set; } = Task.CompletedTask;
    private bool _startupCompatibilityStarted;
    private bool _startupUpdateCheckStarted;
    private bool _closingCompatibility;
    private readonly LibraryViewModel _libraryViewModel;
    private readonly ProfileViewModel _profileViewModel;
    private readonly GameDetectionService _gameDetectionService;
    private readonly NotificationService _notificationService;
    private readonly TaskService _taskService;
    private readonly TestDataService _testDataService;
    private readonly ProfileService _profileService;
    private readonly ModService _modService;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly TaskCenterViewModel _taskCenterViewModel;
    private readonly ExternalToolsViewModel _externalToolsViewModel;
    private readonly PluginManagementViewModel _pluginManagementViewModel;
    private readonly BackupViewModel _backupViewModel;
    private readonly LogViewModel _logViewModel;
    private readonly ConfirmationService _confirmationService;
    private readonly ConflictsViewModel _conflictsViewModel;
    private readonly ImagePreviewService _imagePreview;
    private readonly NexusModsTestService _nexusModsTestService;
    private readonly IPluginService? _pluginService;
    private readonly HellForgeSessionService? _hellForgeSessionService;
    private string? _pluginUiSignature;
    private int _tutorialOriginPage;
    private string? _tutorialOriginSettingsCategory;
    private string? _tutorialOriginToolsCategory;
    private Avalonia.Controls.Window? _ownerWindow;

    /// <summary>
    /// 固定页（配置 / 模组库 / 冲突 / 工具 / 设置 / 浏览 / 创作 / HellForge）在 <see cref="SelectedTabIndex"/> 中占用的槽位数，
    /// 插件页面从这里开始顺延。主窗口构建导航项时复用同一个常量。
    /// </summary>
    public const int PluginPageOffset = 8;

    /// <summary>HellForge 专页的固定槽位；仅 HellForge 游戏源下显示导航入口。</summary>
    public const int HellForgeTabIndex = 7;

    /// <summary>插件贡献的独立页面，安装/启用后作为导航项出现。</summary>
    public ObservableCollection<PluginUiSurfaceViewModel> PluginPages { get; } = new();

    /// <summary>插件贡献到导航栏“插件”菜单的入口。</summary>
    public ObservableCollection<PluginMenuEntry> PluginMenus { get; } = new();

    public string AppVersion => AppInfo.Version;

    [ObservableProperty]
    private string _title = "HD2Mimnt - Helldivers 2 Mod Manager";

    [ObservableProperty]
    private ObservableObject? _currentView;

    [ObservableProperty]
    private int _selectedTabIndex = 0;

    [ObservableProperty]
    private string _statusBarText = "Ready";

    // 游戏状态
    [ObservableProperty]
    private string _gameStatus = "Detecting...";

    [ObservableProperty]
    private bool _gameDetected = false;

    [ObservableProperty]
    private string _gamePath = string.Empty;

    // 当前配置文件
    [ObservableProperty]
    private string _currentProfileName = "No Profile";

    // 任务状态
    [ObservableProperty]
    private int _runningTaskCount = 0;

    [ObservableProperty]
    private string _taskStatusText = "Idle";

    // 通知状态
    [ObservableProperty]
    private int _unreadNotificationCount = 0;

    [ObservableProperty]
    private bool _isTaskCenterOpen;

    [ObservableProperty]
    private bool _isNotificationPanelOpen;

    // 外观：自定义背景图片与卡片透明度（设置页“外观”驱动）
    [ObservableProperty]
    private Bitmap? _backgroundImage;

    [ObservableProperty]
    private bool _hasBackgroundImage;

    [ObservableProperty]
    private double _backgroundImageOpacity = 0.25;

    [ObservableProperty]
    private double _backgroundBlurRadius;

    /// <summary>实时玻璃的模糊半径（DIP）：0 为纯玻璃，数值越大越朦胧。</summary>
    [ObservableProperty]
    private double _glassBlurRadius = LiveBackdropService.DefaultBlur;

    /// <summary>实时玻璃是否真的生效（取屏不可用时为 false，退回系统丙烯酸）。</summary>
    [ObservableProperty]
    private bool _isLiveGlassActive;

    // Expose ViewModels for binding
    public LibraryViewModel LibraryViewModel => _libraryViewModel;
    public ProfileViewModel ProfileViewModel => _profileViewModel;
    public SettingsViewModel SettingsViewModel => _settingsViewModel;
    public TaskCenterViewModel TaskCenterViewModel => _taskCenterViewModel;
    public ExternalToolsViewModel ExternalToolsViewModel => _externalToolsViewModel;
    public PluginManagementViewModel PluginManagementViewModel => _pluginManagementViewModel;
    public BackupViewModel BackupViewModel => _backupViewModel;
    public LogViewModel LogViewModel => _logViewModel;
    public NotificationPanelViewModel NotificationPanelViewModel { get; }
    public MoreViewModel MoreViewModel { get; } = new();
    public ConflictsViewModel ConflictsViewModel => _conflictsViewModel;
    public ImagePreviewService ImagePreview => _imagePreview;
    public ModelPreviewViewModel? ModelPreview => _libraryViewModel.ModelPreview;
    public AudioPreviewViewModel? AudioPreview => _libraryViewModel.AudioPreview;
    public BrowseModsViewModel? BrowseModsViewModel { get; }
    public DirectDownloadViewModel? DirectDownloadViewModel { get; }
    public ModCreationViewModel? ModCreationViewModel { get; }
    public Hd2IASManagementViewModel? Hd2IASManagementViewModel { get; }
    public SoundFixViewModel? SoundFixViewModel { get; }
    public IasSystemViewModel? IasSystemViewModel { get; }
    public TutorialViewModel Tutorial { get; } = new();
    public ManagerMigrationViewModel? ManagerMigrationViewModel { get; }
    public AppUpdateViewModel? AppUpdateViewModel { get; }
    public HellForgeViewModel? HellForgeViewModel { get; }

    public MainWindowViewModel(
        LibraryViewModel libraryViewModel,
        ProfileViewModel profileViewModel,
        ConflictsViewModel conflictsViewModel,
        GameDetectionService gameDetectionService,
        NotificationService notificationService,
        TaskService taskService,
        TestDataService testDataService,
        ProfileService profileService,
        ModService modService,
        SettingsViewModel settingsViewModel,
        TaskCenterViewModel taskCenterViewModel,
        NotificationPanelViewModel notificationPanelViewModel,
        ExternalToolsViewModel externalToolsViewModel,
        PluginManagementViewModel pluginManagementViewModel,
        BackupViewModel backupViewModel,
        LogViewModel logViewModel,
        ConfirmationService confirmationService,
        ImagePreviewService imagePreviewService,
        NexusModsTestService nexusModsTestService,
        ManagerMigrationViewModel? managerMigrationViewModel = null,
        BrowseModsViewModel? browseModsViewModel = null,
        DirectDownloadViewModel? directDownloadViewModel = null,
        ModCreationViewModel? modCreationViewModel = null,
        Hd2IASManagementViewModel? hd2IASManagementViewModel = null, ModCompatibilityViewModel? compatibility = null,
        SoundFixViewModel? soundFixViewModel = null,
        IasSystemViewModel? iasSystemViewModel = null,
        AppUpdateViewModel? appUpdateViewModel = null,
        IPluginService? pluginService = null,
        HellForgeViewModel? hellForgeViewModel = null,
        HellForgeSessionService? hellForgeSessionService = null)
    {
        _pluginService = pluginService;
        HellForgeViewModel = hellForgeViewModel;
        _hellForgeSessionService = hellForgeSessionService;
        if (hellForgeSessionService != null)
            hellForgeSessionService.SessionStarted += OnHellForgeSessionStarted;
        Compatibility = compatibility;
        if (Compatibility != null)
        {
            Compatibility.ContentChanged += async () =>
            {
                conflictsViewModel.InvalidateResults();
                await libraryViewModel.InitializeAsync();
                await profileViewModel.RefreshAfterProfilesChangedAsync();
            };
            modService.ModsChanged += (_, _) =>
            {
                // 自己提交的变更由 ContentChanged 等待刷新，避免排队回调误使新结果失效。
                if (!Compatibility.IsMutating) ApplyOnUiThread(Compatibility.Invalidate);
            };
            Compatibility.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ModCompatibilityViewModel.IsOpen))
                {
                    NavigateToProfilesCommand.NotifyCanExecuteChanged(); NavigateToLibraryCommand.NotifyCanExecuteChanged();
                    NavigateToConflictsCommand.NotifyCanExecuteChanged(); NavigateToToolsCommand.NotifyCanExecuteChanged();
                    NavigateToSettingsCommand.NotifyCanExecuteChanged(); NavigateToBrowseCommand.NotifyCanExecuteChanged();
                    NavigateToModCreationCommand.NotifyCanExecuteChanged();
                    NavigateToHellForgeCommand.NotifyCanExecuteChanged();
                }
            };
        }
        _libraryViewModel = libraryViewModel;
        _profileViewModel = profileViewModel;
        if (ModelPreview is { } preview) preview.PropertyChanged += OnModelPreviewChanged;
        if (AudioPreview is { } audioPreview) audioPreview.PropertyChanged += OnAudioPreviewChanged;
        _conflictsViewModel = conflictsViewModel;
        _gameDetectionService = gameDetectionService;
        _notificationService = notificationService;
        _taskService = taskService;
        _testDataService = testDataService;
        _profileService = profileService;
        _modService = modService;
        _settingsViewModel = settingsViewModel;
        _taskCenterViewModel = taskCenterViewModel;
        _externalToolsViewModel = externalToolsViewModel;
        _pluginManagementViewModel = pluginManagementViewModel;
        _pluginManagementViewModel.ContributionsChanged += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => SafeAsync.FireAndForget(
                RefreshPluginUiAsync(),
                "刷新插件界面贡献"));
        _backupViewModel = backupViewModel;
        _logViewModel = logViewModel;
        NotificationPanelViewModel = notificationPanelViewModel;
        _confirmationService = confirmationService;
        _imagePreview = imagePreviewService;
        _nexusModsTestService = nexusModsTestService;
        ManagerMigrationViewModel = managerMigrationViewModel;
        BrowseModsViewModel = browseModsViewModel;
        DirectDownloadViewModel = directDownloadViewModel;
        ModCreationViewModel = modCreationViewModel;
        Hd2IASManagementViewModel = hd2IASManagementViewModel;
        SoundFixViewModel = soundFixViewModel;
        IasSystemViewModel = iasSystemViewModel;
        AppUpdateViewModel = appUpdateViewModel;
        if (AppUpdateViewModel != null)
        {
            // 检测到新版本（手动或启动检查）：弹通知，点击跳到"工具"页的"关于"分类处理。
            AppUpdateViewModel.UpdateAvailableDetected += () =>
            {
                var version = AppUpdateViewModel.LatestVersion ?? string.Empty;
                _notificationService.ShowWithAction(NotificationLevel.Info,
                    UiLocalization.Current.Format("Updates.NotificationBody", version),
                    UiLocalization.Current["Updates.NotificationAction"],
                    () => Dispatcher.UIThread.Post(() =>
                    {
                        // 先切换主页面再设置分类，仅改分类在非工具页时无可见效果。
                        NavigateToToolsCommand.Execute(null);
                        MoreViewModel.SelectedCategory = "关于";
                    }),
                    UiLocalization.Current["Updates.NotificationTitle"]);
            };
            // 安装脚本已启动，立即退出应用让脚本完成文件替换并重启。
            AppUpdateViewModel.InstallLaunched += () =>
                (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        // 旧版 HD2IAS 的运行时缺失自动提醒暂时停用（上游内测期）；页面与手动刷新保留。
        // if (Hd2IASManagementViewModel != null)
        //     Hd2IASManagementViewModel.InstallationRequirementChanged += OnHd2IASRequirementChanged;
        MoreViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MoreViewModel.SelectedCategory) && MoreViewModel.IsHd2IASVisible
                && Hd2IASManagementViewModel is { } ias)
            {
                ias.Invalidate();
                if (!ias.IsBusy) _ = ias.RefreshInstallationCommand.ExecuteAsync(null);
            }
            if (args.PropertyName == nameof(MoreViewModel.SelectedCategory) && MoreViewModel.IsIasSystemVisible
                && IasSystemViewModel is { IsBusy: false } system)
                _ = system.InitializeAsync();
        };
        async Task ImportArchiveAsync(string archive, CancellationToken token, ModPlatformLink? sourceLink = null)
        {
            if (!_libraryViewModel.ImportCompletion.IsCompleted)
                throw new InvalidOperationException("已有模组正在导入，请等待完成后重试。");
            await _libraryViewModel.ImportPathsAsync(new[] { archive }, token, sourceLink);
            if (_libraryViewModel.Import.SuccessCount > 0) return;
            token.ThrowIfCancellationRequested();
            if (_libraryViewModel.Import.RemainingCount > 0) throw new OperationCanceledException();
            throw new InvalidOperationException(_libraryViewModel.Import.Errors.FirstOrDefault() ?? "模组导入失败。");
        }
        if (BrowseModsViewModel != null)
        {
            BrowseModsViewModel.ImportPlatformArchiveAsync = (archive, source, token) => ImportArchiveAsync(archive, token, source);
        }
        if (DirectDownloadViewModel != null)
            DirectDownloadViewModel.ImportDownloadedArchiveAsync = (archive, token) => ImportArchiveAsync(archive, token);
        if (ManagerMigrationViewModel != null)
            ManagerMigrationViewModel.RefreshImportedDataAsync = async () =>
            {
                await _libraryViewModel.RefreshCommand.ExecuteAsync(null);
                await _profileViewModel.RefreshAfterProfilesChangedAsync();
            };
        Tutorial.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TutorialViewModel.IsOpen))
            {
                NavigateToProfilesCommand.NotifyCanExecuteChanged();
                NavigateToLibraryCommand.NotifyCanExecuteChanged();
                NavigateToConflictsCommand.NotifyCanExecuteChanged();
                NavigateToToolsCommand.NotifyCanExecuteChanged();
                NavigateToSettingsCommand.NotifyCanExecuteChanged();
                NavigateToBrowseCommand.NotifyCanExecuteChanged();
                NavigateToModCreationCommand.NotifyCanExecuteChanged();
                NavigateToHellForgeCommand.NotifyCanExecuteChanged();
                if (Tutorial.IsOpen)
                {
                    _tutorialOriginPage = SelectedTabIndex;
                    _tutorialOriginSettingsCategory = SettingsViewModel.SelectedCategory;
                    _tutorialOriginToolsCategory = MoreViewModel.SelectedCategory;
                }
                else
                {
                    SettingsViewModel.SelectedCategory = _tutorialOriginSettingsCategory ?? "游戏";
                    MoreViewModel.SelectedCategory = _tutorialOriginToolsCategory ?? "插件";
                    NavigateToPage(_tutorialOriginPage);
                    return;
                }
            }
            if (Tutorial.IsOpen && (args.PropertyName == nameof(TutorialViewModel.CurrentStep)
                || args.PropertyName == nameof(TutorialViewModel.IsOpen)))
            {
                CloseTaskCenter();
                CloseNotifications();
                NavigateForTutorialStep();
            }
        };

        NavigateToProfiles();
        SubscribeToEvents();
        _profileViewModel.ActiveProfileChanged += OnActiveProfileChanged;
        _libraryViewModel.DeploymentStateChanged += _profileViewModel.ApplyDeploymentState;
        _profileViewModel.AddToOtherProfilesAsync = _libraryViewModel.OpenAddToOtherProfilesAsync;
        _profileViewModel.ModDeleted += () => _libraryViewModel.InitializeAsync();
        _profileViewModel.ModContentChanged += () => _libraryViewModel.InitializeAsync();
        _profileViewModel.ModUpdatesRequested += (_, mod) =>
        {
            NavigateToLibrary();
            _libraryViewModel.OpenModOnlineCommand.Execute(mod);
        };
        _profileViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ProfileViewModel.SelectedProfile))
            {
                SyncConflictProfile();
                ScanProfileConflictsCommand.NotifyCanExecuteChanged();
            }
            if (args.PropertyName == nameof(ProfileViewModel.ProfileMods)) SyncConflictBadges();
        };
        _conflictsViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ConflictsViewModel.HasScanned)) SyncConflictBadges();
            if (args.PropertyName == nameof(ConflictsViewModel.IsScanning))
                ScanProfileConflictsCommand.NotifyCanExecuteChanged();
        };
        _settingsViewModel.ThemeChanged += OnThemeChanged;
        _settingsViewModel.ListDensityChanged += OnListDensityChanged;
        _settingsViewModel.AppearanceChanged += OnAppearanceChanged;
        _settingsViewModel.GameSourceChanged += OnGameSourceChanged;
        _settingsViewModel.AutoCheckConflictsChanged += (_, enabled) =>
            ApplyOnUiThread(() => _conflictsViewModel.SetAutomaticChecking(enabled));
        _backupViewModel.RefreshAfterRestoreAsync = OnBackupRestoredAsync;
        NotificationPanelViewModel.NavigationRequested += OnNotificationNavigationRequested;
        NotificationPanelViewModel.CloseRequested += (_, _) => CloseNotifications();
        _modService.ModsChanged += OnModsChanged;
        _libraryViewModel.ImportedModsChanged += OnImportedModsChanged;
        _modService.MetadataChanged += (_, _) => QueueUiRefresh(async () =>
            await _profileViewModel.RefreshAfterProfilesChangedAsync());
        _profileService.ProfilesChanged += OnProfilesChanged;
        _profileViewModel.DeploymentDirectionChanged += (_, _) => _conflictsViewModel.InvalidateResults();
    }

    /// <summary>
    /// 重新读取所有已启用插件的声明式界面贡献。
    /// 插件安装 / 启停 / 授权后由设置页事件触发，无需重启管理器。
    /// </summary>
    public async Task RefreshPluginUiAsync()
    {
        if (_pluginService is null) return;

        try
        {
            var bundles = await _pluginService.GetUiContributionsAsync();
            if (bundles is null) return;

            // 设置页每次加载都会触发刷新；贡献没变时不重建页面，避免丢失用户输入
            var signature = string.Join("\n", bundles.Select(b =>
                $"{b.PluginId}|{b.PluginName}|{string.Join(",", b.GrantedPermissions.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))}|" +
                string.Join(",", b.Ui.Pages.Select(p => $"{p.Id}:{p.Title}:{p.Blocks.Count}")) + "|" +
                string.Join(",", b.Ui.Menus.Select(m => $"{m.Id}:{m.Label}:{m.Command}:{m.Dialog}"))));

            if (string.Equals(signature, _pluginUiSignature, StringComparison.Ordinal)) return;
            _pluginUiSignature = signature;

            // 重建会换掉全部视图模型实例，先按「插件 + 页面 id」记住用户当前所在页，
            // 序号在页面增删后会变化，用 id 才能稳定还原，否则插件停用时会跳到别的页面。
            string? previousPluginId = null;
            string? previousSurfaceId = null;
            var previousIndex = SelectedTabIndex - PluginPageOffset;
            if (previousIndex >= 0 && previousIndex < PluginPages.Count)
            {
                previousPluginId = PluginPages[previousIndex].PluginId;
                previousSurfaceId = PluginPages[previousIndex].SurfaceId;
            }

            PluginPages.Clear();
            PluginMenus.Clear();

            foreach (var bundle in bundles)
            {
                foreach (var page in bundle.Ui.Pages)
                {
                    if (string.IsNullOrWhiteSpace(page.Id)) continue;
                    PluginPages.Add(new PluginUiSurfaceViewModel(
                        bundle,
                        page.Id,
                        page.Title,
                        page.Description,
                        page.Icon,
                        page.Blocks,
                        isPage: true,
                        _pluginService));
                }

                foreach (var menu in bundle.Ui.Menus)
                {
                    if (!PluginCommandPlacements.IsMainMenu(menu.Placement)) continue;
                    if (string.IsNullOrWhiteSpace(menu.Command) && string.IsNullOrWhiteSpace(menu.Dialog)) continue;

                    PluginMenus.Add(new PluginMenuEntry
                    {
                        PluginId = bundle.PluginId,
                        PluginName = bundle.PluginName,
                        Label = string.IsNullOrWhiteSpace(menu.Label) ? menu.Id : menu.Label,
                        Icon = menu.Icon,
                        Command = menu.Command,
                        Arguments = menu.Arguments,
                        Dialog = menu.Dialog,
                        RequiredPermission = menu.RequiredPermission,
                        IsPermissionGranted = bundle.HasPermission(menu.RequiredPermission),
                        Bundle = bundle
                    });
                }
            }

            // 还原用户原来停留的插件页。仍存在就按新序号重新指向（重建换了实例），
            // 已被移除（停用 / 卸载）则退回设置页，避免停在空标签或别人的页面上。
            if (previousIndex >= 0)
            {
                var restored = -1;
                if (previousPluginId is not null && previousSurfaceId is not null)
                {
                    for (var i = 0; i < PluginPages.Count; i++)
                    {
                        if (string.Equals(PluginPages[i].PluginId, previousPluginId, StringComparison.Ordinal)
                            && string.Equals(PluginPages[i].SurfaceId, previousSurfaceId, StringComparison.Ordinal))
                        {
                            restored = i;
                            break;
                        }
                    }
                }

                if (restored >= 0)
                {
                    SelectedTabIndex = PluginPageOffset + restored;
                    CurrentView = PluginPages[restored];
                    StatusBarText = PluginPages[restored].Title;
                }
                else
                {
                    SelectedTabIndex = 4;
                    CurrentView = _settingsViewModel;
                    StatusBarText = "插件设置";
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"读取插件界面贡献失败：{ex.Message}");
        }
    }

    /// <summary>导航到第 index 个插件页面（Tab 索引位于固定页之后）。</summary>
    public void NavigateToPluginPage(int index)
    {
        if (index < 0 || index >= PluginPages.Count) return;
        SelectedTabIndex = PluginPageOffset + index;
        CurrentView = PluginPages[index];
        StatusBarText = PluginPages[index].Title;
    }

    /// <summary>为功能菜单项创建浮窗界面；该菜单项没有浮窗时返回 null。</summary>
    public PluginUiSurfaceViewModel? CreatePluginDialog(PluginMenuEntry entry)
        => string.IsNullOrWhiteSpace(entry.Dialog)
            ? null
            : PluginUiSurfaceViewModel.CreateDialogSurface(entry.Bundle, entry.Dialog, _pluginService);

    /// <summary>
    /// 执行功能菜单里的插件命令。返回 null 表示没有可展示的结果（例如返回了界面更新）。
    /// </summary>
    public async Task<PluginCommandResultEventArgs?> RunPluginMenuAsync(PluginMenuEntry entry)
    {
        if (!entry.IsPermissionGranted)
        {
            return new PluginCommandResultEventArgs
            {
                Title = entry.Label,
                PluginName = entry.PluginName,
                Content = $"该功能需要权限 **{entry.RequiredPermission}**。\n\n请在「设置 → 插件」中为「{entry.PluginName}」授权后重试。"
            };
        }

        if (_pluginService is null || string.IsNullOrWhiteSpace(entry.Command))
        {
            return null;
        }

        var result = await _pluginService.ExecutePluginCommandAsync(entry.PluginId, entry.Command, entry.Arguments);

        if (!result.Success)
        {
            return new PluginCommandResultEventArgs
            {
                Title = entry.Label,
                PluginName = entry.PluginName,
                Content = $"命令执行失败：{result.Error ?? result.Message ?? "未知错误"}"
            };
        }

        if (string.IsNullOrWhiteSpace(result.Message))
        {
            return null;
        }

        return new PluginCommandResultEventArgs
        {
            Title = entry.Label,
            PluginName = entry.PluginName,
            Content = result.Message
        };
    }

    private void NavigateForTutorialStep()
    {        if (Tutorial.CurrentStep.PageIndex == 3)
            MoreViewModel.SelectedCategory = Tutorial.CurrentStep.Category ?? "插件";
        if (Tutorial.CurrentStep.PageIndex == 4)
            SettingsViewModel.SelectedCategory = Tutorial.CurrentStep.Category ?? "游戏";
        NavigateToPage(Tutorial.CurrentStep.PageIndex);
    }

    private void NavigateToPage(int pageIndex)
    {        switch (pageIndex)
        {
            case 0: NavigateToProfiles(); break;
            case 1: NavigateToLibrary(); break;
            case 2: NavigateToConflicts(); break;
            case 3: NavigateToTools(); break;
            case 4: NavigateToSettings(); break;
            case 5: NavigateToBrowseCommand.Execute(null); break;
            case 6: NavigateToModCreation(); break;
            case 7: NavigateToHellForgeCommand.Execute(null); break;
        }
    }

    private void SubscribeToEvents()
    {
        _gameDetectionService.GameStatusChanged += OnGameStatusChanged;
        _notificationService.NotificationAdded += OnNotificationAdded;
        _taskService.TaskCompleted += OnTaskCompleted;
        _taskService.TaskStarted += OnTaskStarted;
    }

    private void OnGameStatusChanged(object? sender, GameStatusChangedEventArgs e)
    {
        QueueUiRefresh(async () =>
        {
            GameDetected = e.NewStatus == Application.Services.GameStatus.Found;
            GamePath = e.GameInfo.Path;
            GameStatus = e.NewStatus switch
            {
                Application.Services.GameStatus.Found => "Game Found",
                Application.Services.GameStatus.NotFound => "Game Not Found",
                Application.Services.GameStatus.Invalid => "Invalid Path",
                Application.Services.GameStatus.Checking => "Checking...",
                _ => "Unknown"
            };
            if (e.NewStatus != Application.Services.GameStatus.Checking && Hd2IASManagementViewModel != null)
                await Hd2IASManagementViewModel.CheckRuntimeRequirementAsync();
        });
    }

    // 旧版 HD2IAS 运行时缺失自动提醒暂时停用；恢复订阅 OnHd2IASRequirementChanged 时一并恢复。
    // private void OnHd2IASRequirementChanged(object? sender, EventArgs e)
    // {
    //     ... 原实现见 git 历史（依赖 _iasMissingNotifiedPath/_iasDataMissingNotifiedPath 字段）。
    // }

    private void OnNotificationAdded(object? sender, Notification e)
    {
        ApplyOnUiThread(() => UnreadNotificationCount = _notificationService.GetUnreadCount());
    }

    private readonly Services.AsyncOperationQueue _refreshes = new();

    private bool _disposed;

    private void QueueUiRefresh(Func<Task> refresh) => _ = QueueUiRefreshAsync(refresh);

    private Task QueueUiRefreshAsync(Func<Task> refresh)
        => _disposed ? Task.CompletedTask : _refreshes.Enqueue(async () =>
        {
            try
            {
                if (Dispatcher.UIThread.CheckAccess() || global::Avalonia.Application.Current is null) await refresh();
                else await Dispatcher.UIThread.InvokeAsync(refresh);
            }
            catch (Exception error)
            {
                ApplyOnUiThread(() => _notificationService.ShowError($"刷新界面失败：{error.Message}", "界面刷新"));
            }
        });

    public Task WaitForRefreshesAsync() => _refreshes.DrainAsync();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _modService.ModsChanged -= OnModsChanged;
        _libraryViewModel.ImportedModsChanged -= OnImportedModsChanged;
        _profileService.ProfilesChanged -= OnProfilesChanged;
        _profileViewModel.ActiveProfileChanged -= OnActiveProfileChanged;
        _taskService.TaskCompleted -= OnTaskCompleted;
        _taskService.TaskStarted -= OnTaskStarted;
        _gameDetectionService.GameStatusChanged -= OnGameStatusChanged;
        _settingsViewModel.AppearanceChanged -= OnAppearanceChanged;
        _settingsViewModel.GameSourceChanged -= OnGameSourceChanged;
        if (_hellForgeSessionService != null)
            _hellForgeSessionService.SessionStarted -= OnHellForgeSessionStarted;
        _backupViewModel.RefreshAfterRestoreAsync = null;
        if (ModelPreview is { } preview)
        {
            preview.PropertyChanged -= OnModelPreviewChanged;
            await preview.StopAsync();
        }
        if (AudioPreview is { } audioPreview)
        {
            audioPreview.PropertyChanged -= OnAudioPreviewChanged;
            await audioPreview.StopAsync();
        }
        await WaitForRefreshesAsync();
    }

    private static void ApplyOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess() || global::Avalonia.Application.Current is null)
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
    }

    private void OnTaskCompleted(object? sender, TaskCompletedEventArgs e)
    {
        // 导入/部署/清理由发起浮层提示结果，这里只刷新运行状态，避免重复通知。
        if (e.NotifyUser) QueueUiRefresh(() => HandleTaskCompletedAsync(e.Task));
        else QueueUiRefresh(UpdateRunningTasksAsync);
    }

    private void OnTaskStarted(object? sender, EventArgs e) => QueueUiRefresh(UpdateRunningTasksAsync);

    private async Task HandleTaskCompletedAsync(Core.Entities.ModTask task)
    {
        switch (task.Status)
        {
            case Core.Entities.TaskStatus.Completed:
                _notificationService.ShowSuccess($"任务“{task.Name}”已完成", "后台任务");
                break;
            case Core.Entities.TaskStatus.PartiallyFailed:
                _notificationService.ShowWarning($"任务“{task.Name}”部分失败", "后台任务");
                break;
            case Core.Entities.TaskStatus.Cancelled:
                _notificationService.ShowInfo($"任务“{task.Name}”已取消", "后台任务");
                break;
            case Core.Entities.TaskStatus.Failed:
            case Core.Entities.TaskStatus.ForceTerminated:
                _notificationService.ShowError($"任务“{task.Name}”执行失败：{task.Message}", "后台任务");
                break;
        }

        await UpdateRunningTasksAsync();
    }

    private void OnNotificationNavigationRequested(object? sender, NotificationNavigationTarget target)
    {
        IsNotificationPanelOpen = false;
        switch (target)
        {
            case NotificationNavigationTarget.Library:
                NavigateToLibrary();
                break;
            case NotificationNavigationTarget.Profiles:
                NavigateToProfiles();
                break;
            case NotificationNavigationTarget.Settings:
                NavigateToSettings();
                break;
            case NotificationNavigationTarget.More:
                NavigateToTools();
                break;
            case NotificationNavigationTarget.Tasks:
                NavigateToTasks();
                break;
        }
    }

    private Task OnActiveProfileChanged() => QueueUiRefreshAsync(async () =>
    {
        await _libraryViewModel.RefreshProfileStateAsync();
        UpdateCurrentProfile();
    });

    private void OnModsChanged(object? sender, EventArgs e)
    {
        if (Compatibility?.IsMutating == true) return;
        // 模组增删后联动刷新配置文件页（库页自行重载）
        QueueUiRefresh(async () =>
        {
            _conflictsViewModel.InvalidateResults();
            await _profileViewModel.RefreshAfterProfilesChangedAsync();
            if (Hd2IASManagementViewModel != null)
                await Hd2IASManagementViewModel.CheckRuntimeRequirementAsync();
        });
    }

    /// <summary>
    /// Worker 进程导入完成后的联动刷新：跨进程写库不会触发主进程 ModService.ModsChanged，
    /// 这里补齐配置页卡片、归属状态与冲突结果，避免新模组要重启才可见。
    /// </summary>
    private void OnImportedModsChanged(object? sender, EventArgs e) => QueueUiRefresh(async () =>
    {
        _conflictsViewModel.InvalidateResults();
        await _profileViewModel.RefreshAfterProfilesChangedAsync();
        SyncConflictProfile();
        UpdateCurrentProfile();
    });

    private void OnProfilesChanged(object? sender, ProfilesChangedEventArgs e)
    {
        // 在事件发布时捕获；排队刷新执行时，本地操作的抑制范围可能已经结束。
        var isLocalChange = _profileViewModel.IsApplyingLocalChange;
        // 配置文件数据变更（含模组成员/启用状态）联动刷新配置文件页与库页统计、归属状态
        QueueUiRefresh(async () =>
        {
            if (e.AffectsConflictResults) _conflictsViewModel.InvalidateResults();
            if (!isLocalChange) await _profileViewModel.RefreshAfterProfilesChangedAsync(skipUnchangedProfile: true);
            await _libraryViewModel.RefreshProfileStateAsync();
            SyncConflictProfile();
            UpdateCurrentProfile();
        });
    }

    private Task OnBackupRestoredAsync() => _refreshes.Enqueue(ReloadAfterBackupAsync);

    private async Task ReloadAfterBackupAsync()
    {
        await RestoreNexusCredentialsAsync();
        await _profileService.InitializeAsync();
        await _settingsViewModel.LoadAsync();
        await _libraryViewModel.InitializeAsync();
        await _profileViewModel.LoadProfilesAsync();
        await _taskCenterViewModel.LoadAsync();
        await _externalToolsViewModel.InitializeAsync();
        await _pluginManagementViewModel.InitializeAsync();
        UpdateCurrentProfile();
    }

    public async Task InitializeAsync()
    {
        await RestoreNexusCredentialsAsync();
        await _profileService.InitializeAsync();
        await _libraryViewModel.InitializeAsync();
        await _profileViewModel.LoadProfilesAsync();
        SyncConflictProfile();
        await _settingsViewModel.LoadAsync();
        AppThemeService.Apply(_settingsViewModel.Theme);
        ApplyTransparencyBrushes();
        _libraryViewModel.ApplyListDensity(_settingsViewModel.ListDensity);
        await ApplyAppearanceAsync();
        await _taskCenterViewModel.LoadAsync();
        await _externalToolsViewModel.InitializeAsync();
        await _pluginManagementViewModel.InitializeAsync();
        await _backupViewModel.InitializeAsync();
        await _logViewModel.InitializeAsync();
        await UpdateRunningTasksAsync();

        // 官方来源按设置决定是否扫描常见安装位置；HellForge 只应用已配置目录。
        if (_settingsViewModel.IsOfficialGameSource && _settingsViewModel.AutoDetectGame)
        {
            await _gameDetectionService.AutoDetectAsync();
        }
        else
        {
            await _gameDetectionService.ApplyConfiguredGameAsync();
        }

        // 更新当前配置
        UpdateCurrentProfile();

        // 显示欢迎通知
        if (Hd2IASManagementViewModel != null)
            await Hd2IASManagementViewModel.CheckRuntimeRequirementAsync();
        // _notificationService.ShowInfo("HD2Mimnt started successfully", "Welcome");

        await WaitForRefreshesAsync();
        StatusBarText = "Application initialized";
        if (!_startupCompatibilityStarted)
        {
            _startupCompatibilityStarted = true;
            StartupCompatibilityTask = RunStartupCompatibilityAsync();
        }

        // 应用自更新：默认关闭，开启设置后启动静默检查，仅发现新版本时弹通知。
        if (!_startupUpdateCheckStarted && AppUpdateViewModel != null)
        {
            _startupUpdateCheckStarted = true;
            _ = AppUpdateViewModel.CheckOnStartupAsync();
        }
    }

    private async Task RunStartupCompatibilityAsync()
    {
        await _settingsViewModel.UpdateStaleCompatibilityDataAsync();
        if (_closingCompatibility) return;
        // 无论是否开启启动扫描，都先从持久化摘要恢复上次检查的角标。
        await _libraryViewModel.RestoreCompatibilityBadgesAsync();
        if (!_closingCompatibility) await _libraryViewModel.CheckCompatibilityOnStartupAsync();
    }

    private async Task RestoreNexusCredentialsAsync()
    {
        if (_settingsViewModel.AyakaAccount != null) await _settingsViewModel.AyakaAccount.RestoreAsync();
        try { await _nexusModsTestService.RestoreApiKeyAsync(); }
        catch
        {
            _notificationService.ShowWarning("无法读取已保存的 N 网凭据，请在设置的账号页重新登录，或在开发工具重新填写个人 Key。", "N 网认证");
        }
    }

    private void UpdateCurrentProfile()
    {
        var activeProfile = _profileViewModel.Profiles.FirstOrDefault(p => p.IsActive);
        CurrentProfileName = activeProfile?.Name ?? "No Profile";
    }

    private void OnModelPreviewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ModelPreviewViewModel.IsOpen)) return;
        NavigateToProfilesCommand.NotifyCanExecuteChanged(); NavigateToLibraryCommand.NotifyCanExecuteChanged();
        NavigateToConflictsCommand.NotifyCanExecuteChanged(); NavigateToToolsCommand.NotifyCanExecuteChanged();
        NavigateToSettingsCommand.NotifyCanExecuteChanged(); NavigateToBrowseCommand.NotifyCanExecuteChanged();
        NavigateToModCreationCommand.NotifyCanExecuteChanged();
        NavigateToHellForgeCommand.NotifyCanExecuteChanged();
    }

    private void OnAudioPreviewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(AudioPreviewViewModel.IsOpen)) return;
        NavigateToProfilesCommand.NotifyCanExecuteChanged(); NavigateToLibraryCommand.NotifyCanExecuteChanged();
        NavigateToConflictsCommand.NotifyCanExecuteChanged(); NavigateToToolsCommand.NotifyCanExecuteChanged();
        NavigateToSettingsCommand.NotifyCanExecuteChanged(); NavigateToBrowseCommand.NotifyCanExecuteChanged();
        NavigateToModCreationCommand.NotifyCanExecuteChanged();
        NavigateToHellForgeCommand.NotifyCanExecuteChanged();
    }

    private bool CanNavigate() => !Tutorial.IsOpen && Compatibility?.IsOpen != true && ModelPreview?.IsOpen != true && AudioPreview?.IsOpen != true;

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void NavigateToProfiles()
    {
        SelectedTabIndex = 0;
        CurrentView = _profileViewModel;
        StatusBarText = "配置文件";
    }

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void NavigateToLibrary()
    {
        SelectedTabIndex = 1;
        CurrentView = _libraryViewModel;
        StatusBarText = "模组库";
    }

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void NavigateToModCreation()
    {
        SelectedTabIndex = 6;
        CurrentView = ModCreationViewModel;
        StatusBarText = "模组创建";
    }

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private async Task NavigateToBrowseAsync()
    {
        SelectedTabIndex = 5;
        CurrentView = BrowseModsViewModel;
        StatusBarText = "浏览模组";
        if (BrowseModsViewModel != null) await BrowseModsViewModel.EnsureLoadedAsync();
    }

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private async Task NavigateToHellForgeAsync()
    {
        SelectedTabIndex = HellForgeTabIndex;
        CurrentView = HellForgeViewModel;
        StatusBarText = "HellForge 档案";
        if (HellForgeViewModel != null) await HellForgeViewModel.InitializeAsync();
    }

    partial void OnSelectedTabIndexChanged(int oldValue, int newValue)
    {
        if (oldValue == 5 && newValue != 5 && BrowseModsViewModel != null)
            _ = BrowseModsViewModel.LeavePageAsync();
        if (oldValue == 4 && newValue != 4) _ = _settingsViewModel.StopAccountsAsync();
    }

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void NavigateToConflicts()
    {
        SyncConflictProfile();
        SelectedTabIndex = 2;
        CurrentView = _conflictsViewModel;
        StatusBarText = "冲突";
    }

    private void SyncConflictProfile() => _conflictsViewModel.SetProfile(
        _profileViewModel.SelectedProfile?.Id, _profileViewModel.SelectedProfile?.Name);

    private void SyncConflictBadges() => _profileViewModel.ApplyConflictIndex(
        _conflictsViewModel.HasScanned ? _conflictsViewModel.CardIndex : ConflictCardIndex.Empty);

    private bool CanScanProfileConflicts() => _profileViewModel.HasSelectedProfile && !_conflictsViewModel.IsScanning;

    /// <summary>直接检查正在查看的配置，保持当前页面并回填卡片提示。</summary>
    [RelayCommand(CanExecute = nameof(CanScanProfileConflicts))]
    private async Task ScanProfileConflictsAsync()
    {
        SyncConflictProfile();
        await _conflictsViewModel.ScanCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void NavigateToTasks()
    {
        IsTaskCenterOpen = true;
        _taskCenterViewModel.StartAutoRefresh();
        _ = UpdateRunningTasksAsync();
        StatusBarText = "任务中心";
    }

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void NavigateToSettings()
    {
        SelectedTabIndex = 4;
        StatusBarText = "设置";
    }

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void NavigateToTools()
    {
        SelectedTabIndex = 3;
        StatusBarText = "工具";
    }

    [RelayCommand]
    private void CloseTaskCenter()
    {
        IsTaskCenterOpen = false;
        _taskCenterViewModel.StopAutoRefresh();
    }

    [RelayCommand]
    private void OpenTaskCenter()
    {
        NavigateToTasks();
    }

    [RelayCommand]
    private void OpenNotifications()
    {
        IsNotificationPanelOpen = !IsNotificationPanelOpen;
        if (IsNotificationPanelOpen)
        {
            _notificationService.MarkAllAsRead();
            UnreadNotificationCount = 0;
        }
    }

    [RelayCommand]
    private void CloseNotifications() => IsNotificationPanelOpen = false;

    partial void OnIsNotificationPanelOpenChanged(bool value)
    {
        NotificationPanelViewModel.IsHistoryOpen = value;
        if (value) NotificationPanelViewModel.DismissAllToasts();
    }

    [RelayCommand]
    private void CloseOverlays()
    {
        if (ModelPreview?.IsOpen == true) { ModelPreview.CloseCommand.Execute(null); return; }
        if (AudioPreview?.IsOpen == true) { AudioPreview.CloseCommand.Execute(null); return; }
        if (Compatibility?.IsOpen == true) { Compatibility.CloseCommand.Execute(null); return; }
        if (_libraryViewModel.ModOnline?.IsOpen == true)
        {
            _libraryViewModel.ModOnline.CloseCommand.Execute(null);
            return;
        }
        if (Tutorial.IsOpen)
        {
            Tutorial.CloseCommand.Execute(null);
            return;
        }
        if (_libraryViewModel.Import.IsOpen)
        {
            if (SelectedTabIndex == 5 && _libraryViewModel.Import.IsImporting)
                _libraryViewModel.Import.CancelCommand.Execute(null);
            else _libraryViewModel.Import.CloseCommand.Execute(null);
            return;
        }
        if (_libraryViewModel.IsDeploymentProgressOpen)
        {
            _libraryViewModel.CloseDeploymentProgressCommand.Execute(null);
            return;
        }
        if (_imagePreview.IsOpen)
        {
            _imagePreview.Close();
            return;
        }
        if (_libraryViewModel.IsAddToProfileDialogOpen)
        {
            _libraryViewModel.CancelAddToProfileCommand.Execute(null);
            return;
        }
        if (SelectedTabIndex == 5 && BrowseModsViewModel is { } browse)
        {
            if (browse.IsLoadingImage) { browse.CancelImagePreview(); return; }
            if (browse.IsBusy) { browse.CancelCommand.Execute(null); return; }
        }
        if (SelectedTabIndex == 6 && !IsTaskCenterOpen && !IsNotificationPanelOpen)
            ModCreationViewModel?.CancelSorting();

        if (SelectedTabIndex == 0 && !IsTaskCenterOpen && !IsNotificationPanelOpen)
        {
            if (_profileViewModel.IsDetailPanelOpen)
                _profileViewModel.CloseDetailPanelCommand.Execute(null);
            else if (_profileViewModel.IsConflictPickerOpen)
                _profileViewModel.CloseConflictPickerCommand.Execute(null);
            else if (_profileViewModel.IsMoveToPositionDialogOpen)
                _profileViewModel.CancelMoveToPositionCommand.Execute(null);
            else if (_profileViewModel.IsAddToGroupDialogOpen)
                _profileViewModel.CancelAddToGroupCommand.Execute(null);
            else if (_profileViewModel.IsModOptionsDialogOpen)
                _profileViewModel.CloseModOptionsCommand.Execute(null);
            else if (_profileViewModel.IsAddModOpen)
                _profileViewModel.CloseAddModCommand.Execute(null);
            else if (!_libraryViewModel.IsDeploying)
                _profileViewModel.ClearModSelectionCommand.Execute(null);
        }
        if (SelectedTabIndex == 1 && !IsTaskCenterOpen && !IsNotificationPanelOpen)
        {
            if (_libraryViewModel.ModLink?.IsOpen == true)
                _libraryViewModel.ModLink.CloseCommand.Execute(null);
            else if (_libraryViewModel.IsAddToProfileDialogOpen)
                _libraryViewModel.CancelAddToProfileCommand.Execute(null);
            else if (_libraryViewModel.IsDetailPanelOpen)
                _libraryViewModel.CloseDetailPanelCommand.Execute(null);
            else
                _libraryViewModel.ClearSelectionCommand.Execute(null);
        }

        IsTaskCenterOpen = false;
        IsNotificationPanelOpen = false;
        _taskCenterViewModel.StopAutoRefresh();
    }

    public void AttachOwner(Avalonia.Controls.Window owner)
    {
        _ownerWindow = owner;
        _confirmationService.SetOwner(owner);
        // 实时玻璃的取屏要求窗口已显示（排除自身截图的 API 需要可见窗口），显示后再应用一次。
        owner.Opened += (_, _) => ApplyTransparencyBrushes();
        // 取屏是异步启动的：第一帧到达才知道图层可以显示，不能只看 Apply 那一刻有没有帧。
        GlassMaterialService.LiveFrameUpdated += OnLiveFrameUpdated;
        owner.Closed += (_, _) => GlassMaterialService.LiveFrameUpdated -= OnLiveFrameUpdated;
    }

    private void OnLiveFrameUpdated(object? sender, EventArgs e) =>
        IsLiveGlassActive = GlassMaterialService.LiveFrame is not null;

    public void ReceiveBrowserProtocolLink(string value)
    {
        if (value.Length == 0) return;
        if (BrowseModsViewModel is { IsWaitingForWebsite: true } browse && browse.TryReceiveProtocolLink(value, ignoreMismatch: true)) return;
        if (_libraryViewModel.ModOnline is { IsWaitingForWebsite: true } waitingOnline &&
            waitingOnline.TryReceiveProtocolLink(value, ignoreMismatch: true)) return;
        if (BrowseModsViewModel is { IsWaitingForWebsite: true } waitingBrowse)
        {
            waitingBrowse.TryReceiveProtocolLink(value);
            return;
        }
        if (_libraryViewModel.ModOnline is { IsWaitingForWebsite: true } online)
        {
            online.TryReceiveProtocolLink(value);
            return;
        }
        if (BrowseModsViewModel?.IsCancelledProtocolLink(value) == true ||
            _libraryViewModel.ModOnline?.IsCancelledProtocolLink(value) == true)
        {
            _notificationService.ShowWarning(
                "对应的网页下载任务已取消。请重新发起下载。", "N 网下载");
            return;
        }
        if (BrowseModsViewModel is not { IsBusy: false } receiver || !receiver.PlatformChange.IsCompleted ||
            _libraryViewModel.ModOnline?.IsBusy == true)
        {
            _notificationService.ShowWarning("当前下载操作尚未结束，请完成或取消后重新打开 N 网下载链接。", "N 网下载");
            return;
        }
        SelectedTabIndex = 5;
        CurrentView = receiver;
        StatusBarText = "浏览模组";
        _ = receiver.DownloadProtocolLinkAsync(value);
    }

    public async Task<bool> CanCloseAsync()
    {
        _closingCompatibility = true;
        if (ModelPreview != null) await ModelPreview.StopAsync();
        if (AudioPreview != null) await AudioPreview.StopAsync();
        await _settingsViewModel.StopCompatibilityDataAsync();
        if (Compatibility != null) await Compatibility.StopAsync();
        await StartupCompatibilityTask;
        _closingCompatibility = false;
        await _libraryViewModel.CancelDeploymentAndWaitAsync();
        if (ModCreationViewModel != null)
        {
            await ModCreationViewModel.StopAsync();
            if (!await ModCreationViewModel.ConfirmDiscardAsync()) return false;
        }
        await _profileViewModel.CloseModDetailAsync();
        await _libraryViewModel.LocalVersions.StopAsync();
        await _settingsViewModel.StopAccountsAsync();
        if (DirectDownloadViewModel != null) await DirectDownloadViewModel.CancelAndWaitAsync();
        if (SoundFixViewModel != null) await SoundFixViewModel.CancelAndWaitAsync();
        if (Hd2IASManagementViewModel != null) await Hd2IASManagementViewModel.CancelAndWaitAsync();
        if (IasSystemViewModel != null) await IasSystemViewModel.CancelAndWaitAsync();
        if (HellForgeViewModel != null) await HellForgeViewModel.CancelAndWaitAsync();
        if (BrowseModsViewModel != null) await BrowseModsViewModel.LeavePageAsync();
        if (ManagerMigrationViewModel is { IsBusy: true } migration)
        {
            await migration.CancelAndWaitAsync();
        }
        if (_libraryViewModel.ModOnline is { } online)
            await online.CloseCommand.ExecuteAsync(null);
        if (!_libraryViewModel.ImportCompletion.IsCompleted)
        {
            if (!await _confirmationService.ConfirmAsync("退出应用", "模组仍在添加中。是否取消添加并在清理完成后退出？"))
                return false;
            _libraryViewModel.Import.CancelCommand.Execute(null);
            await _libraryViewModel.ImportCompletion;
        }
        var runningTasks = await _taskService.GetRunningTasksAsync();
        if (!runningTasks.Any())
        {
            if (Hd2IASManagementViewModel != null) await Hd2IASManagementViewModel.StopAutomaticChecksAsync();
            await _conflictsViewModel.StopScanAsync();
            _taskCenterViewModel.StopAutoRefresh();
            await StopPlatformDownloadsAsync();
            await _taskService.CancelAllAndWaitAsync();
            await WaitForRefreshesAsync();
            return true;
        }

        var canClose = await _confirmationService.ConfirmAsync(
            "退出应用",
            $"当前仍有 {runningTasks.Count()} 个后台任务运行中。将取消任务并等待清理完成，确定退出吗？");
        if (canClose)
        {
            if (Hd2IASManagementViewModel != null) await Hd2IASManagementViewModel.StopAutomaticChecksAsync();
            await _conflictsViewModel.StopScanAsync();
            _taskCenterViewModel.StopAutoRefresh();
            await StopPlatformDownloadsAsync();
            await _taskService.CancelAllAndWaitAsync();
            await WaitForRefreshesAsync();
        }

        return canClose;
    }

    private async Task StopPlatformDownloadsAsync()
    {
        if (BrowseModsViewModel != null) await BrowseModsViewModel.CloseAsync();
        if (_libraryViewModel.ModOnline is { } online) await online.StopAsync();
    }

    private void OnThemeChanged(object? sender, string theme)
    {
        AppThemeService.Apply(theme);
        ApplyTransparencyBrushes();
    }

    /// <summary>设置保存后游戏源实际变化：切到 HellForge 自动进入专页；切回官方时不停留在已隐藏的页面。</summary>
    private void OnGameSourceChanged(object? sender, bool isHellForge)
    {
        ApplyOnUiThread(() =>
        {
            if (isHellForge) NavigateToHellForgeCommand.Execute(null);
            else if (SelectedTabIndex == HellForgeTabIndex) NavigateToSettings();
        });
    }

    /// <summary>会话启动（无论从哪个页面发起）后跳到 HellForge 专页，让准备进度可见。</summary>
    private void OnHellForgeSessionStarted(object? sender, EventArgs e)
    {
        ApplyOnUiThread(() =>
        {
            if (SelectedTabIndex != HellForgeTabIndex) NavigateToHellForgeCommand.Execute(null);
        });
    }

    private void OnAppearanceChanged(object? sender, EventArgs e) => QueueUiRefresh(ApplyAppearanceAsync);

    /// <summary>应用外观设置：背景图片（后台线程解码）、透明度、模糊度与卡片透明度。</summary>
    public async Task ApplyAppearanceAsync()
    {
        BackgroundImageOpacity = Math.Clamp(_settingsViewModel.BackgroundImageOpacity, 0, 100) / 100.0;
        BackgroundBlurRadius = Math.Clamp(_settingsViewModel.BackgroundBlur, 0, 40);

        var path = _settingsViewModel.BackgroundImagePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            BackgroundImage = null;
            HasBackgroundImage = false;
        }
        else
        {
            try
            {
                var bitmap = await Task.Run(() => new Bitmap(path));
                BackgroundImage = bitmap;
                HasBackgroundImage = true;
            }
            catch
            {
                BackgroundImage = null;
                HasBackgroundImage = false;
                _notificationService.ShowWarning("背景图片加载失败，已回退为纯色背景", "外观");
            }
        }

        ApplyTransparencyBrushes();
    }

    /// <summary>只更新表面透明度，颜色由 FluentAvalonia 动态资源随主题更新。
    /// 浅色主题下毛玻璃强度大于 0 时由 <see cref="GlassMaterialService"/> 统一发布窗口着色层，
    /// 否则维持原有观感：有背景图时顶栏/底栏跟随卡片透明度、内容区全透明露出背景图；无背景图时全部不透明。</summary>
    private void ApplyTransparencyBrushes()
    {
        var app = global::Avalonia.Application.Current;
        if (app == null) return;

        GlassBlurRadius = Math.Clamp(_settingsViewModel.GlassBlur, LiveBackdropService.MinBlur, LiveBackdropService.MaxBlur);

        var active = GlassMaterialService.Apply(
            _ownerWindow,
            app.ActualThemeVariant,
            _settingsViewModel.GlassIntensity,
            GlassBlurRadius,
            HasBackgroundImage,
            _settingsViewModel.CardOpacity,
            // 只有用户主动开启自绘实时玻璃时才牺牲“窗口能被截图”，默认走系统丙烯酸。
            screenshotFriendly: !_settingsViewModel.GlassLiveBackdrop);

        // 实时玻璃的图层是否可见只看取屏是否真的在跑：丙烯酸兜底时该层保持隐藏。
        IsLiveGlassActive = active && GlassMaterialService.LiveFrame is not null;
    }

    private void OnListDensityChanged(object? sender, string density) => _libraryViewModel.ApplyListDensity(density);


    [RelayCommand]
    private async Task RefreshGameStatusAsync()
    {
        GameStatus = "Checking...";
        await _gameDetectionService.AutoDetectAsync();
    }

    [RelayCommand]
    private async Task UpdateRunningTasksAsync()
    {
        var runningTasks = await _taskService.GetRunningTasksAsync();
        RunningTaskCount = runningTasks.Count();
        TaskStatusText = RunningTaskCount > 0 ? $"{RunningTaskCount} Running" : "Idle";
    }

    [RelayCommand]
    private void OpenDevTools()
    {
        var devToolsViewModel = new DevToolsViewModel(_testDataService, _profileService, _nexusModsTestService)
        { AyakaAccount = _settingsViewModel.AyakaAccount, BrowseMods = BrowseModsViewModel };
        var devToolsWindow = new DevToolsWindow
        {
            DataContext = devToolsViewModel
        };
        devToolsWindow.Show();
    }
}
