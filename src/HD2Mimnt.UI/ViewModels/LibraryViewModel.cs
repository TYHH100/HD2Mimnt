using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Search;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Resources;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;
using TaskStatus = HD2Mimnt.Core.Entities.TaskStatus;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// 模组库 ViewModel
/// 实现搜索、筛选、排序、批量操作等功能
/// </summary>
public partial class LibraryViewModel : ObservableObject
{
    [RelayCommand]
    private async Task LaunchGameAsync()
    {
        try
        {
            var launch = await _gameDetectionService.LaunchGameAsync();
            if (launch == GameLaunchKind.HellForgeSession)
                _notificationService.ShowInfo("HellForge 会话已启动", "启动游戏");
            else if (launch == GameLaunchKind.HellForgeLauncher)
                _notificationService.ShowInfo("当前 HellForge 版本不支持命令直接开始会话，已打开启动器，请点击 PLAY。", "启动游戏");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"无法启动游戏：{ex.Message}", "启动失败");
        }
    }

    private readonly ModService _modService;
    public ModVersionsViewModel LocalVersions { get; }
    private readonly SearchService _searchService;
    private readonly ProfileService _profileService;
    private readonly NotificationService _notificationService;
    private readonly DeploymentService _deploymentService;
    public event Action<IReadOnlySet<Guid>>? DeploymentStateChanged;
    private readonly GameDetectionService _gameDetectionService;
    private readonly IConfirmationService _confirmationService;
    private readonly ImagePreviewService? _imagePreview;
    private readonly SettingsService? _settingsService;
    private readonly LuaExtractionService? _luaExtraction;
    private readonly IPluginService? _pluginService;
    private readonly TaskService? _taskService;
    private CancellationTokenSource? _luaExtractionCts;

    /// <summary>插件贡献到模组右键菜单的命令（已启用插件、按授权状态标注）。</summary>
    private List<PluginCommandContribution> _modPluginCommands = new();

    /// <summary>正在进行的插件命令刷新任务，用于合并同一时刻的重复请求。</summary>
    private Task? _pluginCommandRefresh;

    /// <summary>插件贡献到模组库工具栏的入口（命令或浮窗）。</summary>
    public ObservableCollection<LibraryPluginAction> LibraryPluginActions { get; } = new();

    /// <summary>插件请求打开浮窗；视图据此弹出声明式窗口。</summary>
    public event EventHandler<PluginUiSurfaceViewModel>? PluginDialogReady;

    /// <summary>插件命令执行完毕后由视图弹出结果窗口（AI 摘要等文本可能较长）。</summary>
    public event EventHandler<PluginCommandResultEventArgs>? PluginCommandResultReady;

    /// <summary>
    /// 导入批次结束后通知外部模组集合已变化。导入在 HD2Mimnt.Worker 独立进程内写库，
    /// 不会触发主进程 <c>ModService.ModsChanged</c>，配置页等订阅方必须靠这里显式联动刷新；
    /// 同时覆盖「导入后自动加入当前配置」在 Worker 内完成的情况。
    /// </summary>
    public event EventHandler? ImportedModsChanged;

    // 数据源
    private List<ModItemViewModel> _allMods = new();
    private bool _selectionSyncInProgress;
    private readonly ModSelection<ModItemViewModel> _modSelection;
    private CancellationTokenSource? _detailHashCts;

    [ObservableProperty]
    private ObservableCollection<ModItemViewModel> _filteredMods = new();

    [ObservableProperty]
    private ObservableCollection<ModItemViewModel> _selectedMods = new();

    // 搜索
    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    private CancellationTokenSource? _searchCts;
    private DispatcherTimer? _searchDebounceTimer;
    private int _filterGeneration;
    private int _searchGeneration;

    // 筛选
    [ObservableProperty]
    private ModFilterType _currentFilter = ModFilterType.All;

    [ObservableProperty]
    private ulong? _selectedCategory;

    // 加入配置文件弹窗：目标 Mod 为空表示对整个选中集合操作
    [ObservableProperty]
    private bool _isAddToProfileDialogOpen;

    [ObservableProperty]
    private ModItemViewModel? _addToProfileTarget;

    private List<Profile>? _profilesForAdd;

    public ObservableCollection<ProfileChoiceViewModel> AddToProfileChoices { get; } = new();

    /// <summary>“加入配置文件”弹窗中的可选目标分组；仅在选择单个配置文件且设置开启时使用。</summary>
    public ObservableCollection<ProfileGroupChoiceViewModel> AddToProfileGroups { get; } = new();

    [ObservableProperty]
    private bool _showAddToProfileGroupPicker;

    [ObservableProperty]
    private bool _chooseAddToProfileGroup;

    [ObservableProperty]
    private ProfileGroupChoiceViewModel? _selectedAddToProfileGroup;

    public bool HasSingleAddToProfileChoice => AddToProfileChoices.Count(choice => choice.IsSelected) == 1;
    public bool AddToProfileGroupHintVisible => ShowAddToProfileGroupPicker && !HasSingleAddToProfileChoice;
    public bool AddToProfileGroupEmptyHintVisible => ChooseAddToProfileGroup && HasSingleAddToProfileChoice && !HasAddToProfileGroups;
    public bool HasAddToProfileGroups => AddToProfileGroups.Count > 0;

    partial void OnChooseAddToProfileGroupChanged(bool value)
    {
        OnPropertyChanged(nameof(AddToProfileGroupEmptyHintVisible));
        if (!value || !HasSingleAddToProfileChoice) return;
        RefreshAddToProfileGroups();
    }

    public string AddToProfileDialogTitle => AddToProfileTarget != null
        ? $"将“{AddToProfileTarget.Name}”加入配置文件"
        : $"将选中的 {SelectedCount} 个 Mod 加入配置文件";

    // 排序
    [ObservableProperty]
    private ModSortMode _currentSortMode = ModSortMode.NameAscending;

    // 统计
    [ObservableProperty]
    private int _totalModCount;

    [ObservableProperty]
    private int _filteredModCount;

    [ObservableProperty]
    private int _enabledCount;

    [ObservableProperty]
    private int _currentProfileCount;

    [ObservableProperty]
    private int _disabledCount;

    [ObservableProperty]
    private int _deployedCount;

    [ObservableProperty]
    private int _notDeployedCount;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private bool _isAllSelected;

    [ObservableProperty]
    private string _listDensity = "Comfortable";

    // 状态
    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private bool _hasFilteredResults;

    [ObservableProperty]
    private bool _hasNoResults;

    [ObservableProperty]
    private string _currentProfileName = "无当前 Profile";

    [ObservableProperty]
    private ModItemViewModel? _focusedMod;

    // 当前配置文件
    private Profile? _currentProfile;

    // 模组详情面板
    [ObservableProperty]
    private ModItemViewModel? _selectedModDetail;

    [ObservableProperty]
    private bool _isDetailPanelOpen;

    [ObservableProperty]
    private DeploymentMethod _currentDeploymentMethod = DeploymentMethod.Copy;

    [ObservableProperty]
    private bool _isDeploying;

    [ObservableProperty]
    private bool _isDeploymentProgressOpen;

    [ObservableProperty]
    private string _deploymentTitle = "部署模组";

    [RelayCommand]
    private void OpenDeploymentProgress()
    {
        if (IsDeploying) IsDeploymentProgressOpen = true;
    }

    [RelayCommand]
    private void CloseDeploymentProgress() => IsDeploymentProgressOpen = false;

    [ObservableProperty]
    private string _deploymentMessage = string.Empty;

    [ObservableProperty]
    private double _deploymentProgress;

    [ObservableProperty]
    private bool _isDeploymentIndeterminate = true;

    [ObservableProperty]
    private string _deploymentFileCount = string.Empty;

    private CancellationTokenSource? _deploymentCts;

    public ModImportViewModel Import { get; }
    public ModLinkViewModel? ModLink { get; }
    public ModOnlineViewModel? ModOnline { get; }

    [RelayCommand]
    private Task OpenModOnlineAsync(ModItemViewModel mod) => ModOnline?.OpenAsync(mod) ?? Task.CompletedTask;

    [RelayCommand]
    private void OpenModLink(ModItemViewModel mod) => ModLink?.Open(mod);
    public Task ImportCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// 导入批次结束后的后台收尾（导入后兼容性检查、Lua 自动提取）。不并入 <see cref="ImportCompletion"/>，
    /// 但保留可等待句柄：退出流程与测试收尾要确认它们不会在服务释放后继续访问存储。
    /// </summary>
    public Task ImportPostProcessing { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private void OpenImport() => Import.Open();

    public IEnumerable<DeploymentMethod> DeploymentMethods => Enum.GetValues<DeploymentMethod>();

    /// <summary>排序下拉的本地化选项（直接绑定枚举会显示英文名）。</summary>
    public IReadOnlyList<SortModeOption> SortModes { get; } = new[]
    {
        new SortModeOption("名称升序", ModSortMode.NameAscending),
        new SortModeOption("名称降序", ModSortMode.NameDescending),
        new SortModeOption("最新导入", ModSortMode.DateAddedNewest),
        new SortModeOption("最早导入", ModSortMode.DateAddedOldest),
        new SortModeOption("按分类", ModSortMode.Category),
    };

    /// <summary>分类下拉的本地化选项。</summary>
    public IReadOnlyList<CategoryOption> CategoryOptions { get; private set; } = CreateCategoryOptions(Array.Empty<ulong>());

    private static IReadOnlyList<CategoryOption> CreateCategoryOptions(IEnumerable<ulong> detectedIds)
        => new[] { new CategoryOption("全部分类", null) }
            .Concat(ResourceTypeCatalog.All.Select(type => new CategoryOption(type.Label, type.Id)))
            .Concat(detectedIds.Distinct().Where(id => !ResourceTypeCatalog.IsKnown(id)).Order()
                .Select(id => new CategoryOption(ResourceTypeCatalog.GetDisplayName(id), id)))
            .ToArray();

    public bool IsCompactList => ListDensity == "Compact";
    public string FilterSummaryText => $"显示 {FilteredModCount:N0} / {TotalModCount:N0} 个 Mod";
    public bool IsLibraryEmptyStateVisible => !IsLoading && !HasError && IsEmpty;
    public bool IsSearchEmptyStateVisible => !IsLoading && !HasError && HasNoResults && !string.IsNullOrWhiteSpace(SearchText);
    public bool IsFilterEmptyStateVisible => !IsLoading && !HasError && HasNoResults && string.IsNullOrWhiteSpace(SearchText) && (CurrentFilter != ModFilterType.All || SelectedCategory.HasValue);
    public bool IsResultsVisible => !IsLoading && !HasError && HasFilteredResults;
    public bool IsLoadingStateVisible => IsLoading;
    public bool IsErrorStateVisible => HasError && !IsLoading;

    partial void OnListDensityChanged(string value) => OnPropertyChanged(nameof(IsCompactList));

    partial void OnFilteredModCountChanged(int value) => OnPropertyChanged(nameof(FilterSummaryText));

    partial void OnTotalModCountChanged(int value) => OnPropertyChanged(nameof(FilterSummaryText));

    public void ApplyListDensity(string value)
    {
        ListDensity = value is "Compact" or "Comfortable" ? value : "Comfortable";
    }

    public LibraryViewModel(
        ModService modService,
        SearchService searchService,
        ProfileService profileService,
        NotificationService notificationService,
        DeploymentService deploymentService,
        GameDetectionService gameDetectionService,
        IConfirmationService confirmationService,
        ImagePreviewService? imagePreview = null,
        SettingsService? settingsService = null,
        ModLinkService? modLinkService = null,
        ModOnlineService? modOnlineService = null,
        IWebsiteDownloadLauncher? websiteLauncher = null,
        BrowserDownloadHandoffService? browserDownloads = null, ModCompatibilityViewModel? compatibility = null,
        PlatformDownloadTaskRunner? downloadTasks = null, ModelPreviewViewModel? modelPreview = null, AudioPreviewViewModel? audioPreview = null,
        LuaExtractionService? luaExtraction = null, IPluginService? pluginService = null,
        TaskService? taskService = null)
    {
        _taskService = taskService;
        _pluginService = pluginService;
        ModelPreview = modelPreview;
        AudioPreview = audioPreview;
        Compatibility = compatibility;
        _luaExtraction = luaExtraction;
        _modService = modService;
        _modService.NoticeDismissed += OnModNoticeDismissed;
        LocalVersions = new(modService, confirmationService, async () =>
        {
            await LoadModsAsync();
            await searchService.BuildIndexAsync();
        });
        Import = new ModImportViewModel(modService, settingsService, taskService);
        ModLink = modLinkService == null ? null : new ModLinkViewModel(modLinkService);
        ModOnline = modOnlineService == null ? null : new ModOnlineViewModel(modOnlineService, async () =>
        {
            await LoadModsAsync();
            if (SelectedModDetail is { } detail && await _modService.GetModByIdAsync(detail.Id) is { } saved)
                detail.ApplyContent(saved);
            await searchService.BuildIndexAsync();
        }, websiteLauncher, browserDownloads, downloadTasks);
        _searchService = searchService;
        _profileService = profileService;
        _notificationService = notificationService;
        _deploymentService = deploymentService;
        _gameDetectionService = gameDetectionService;
        _confirmationService = confirmationService;
        _imagePreview = imagePreview;
        _settingsService = settingsService;
        if (_settingsService != null) _settingsService.SettingChanged += OnLibrarySettingsChanged;
        _modSelection = new(() => FilteredMods, () => SelectedMods, SyncListSelection,
            (mod, focused) => mod.IsFocused = focused);

        // 监听选择变化
        SelectedMods.CollectionChanged += OnSelectedModsChanged;
    }

    public async Task InitializeAsync()
    {
        await LoadModsAsync();
        await _searchService.BuildIndexAsync();
        await RefreshPluginCommandsAsync();
    }

    /// <summary>
    /// 刷新插件贡献到模组右键菜单的命令。插件未启用或权限未授予时也会返回，
    /// 由菜单以禁用状态展示，避免用户以为“插件没生效”。
    /// </summary>
    public Task RefreshPluginCommandsAsync()
    {
        // 右键菜单每次弹出都会调用，启动阶段也可能同时触发。
        // 若两次并发执行，“清空 → 追加”会交错，同一入口被追加两遍，
        // 因此合并同一时刻的重复请求，只跑一次。
        if (_pluginCommandRefresh is { IsCompleted: false } running) return running;

        _pluginCommandRefresh = RefreshPluginCommandsCoreAsync();
        return _pluginCommandRefresh;
    }

    private async Task RefreshPluginCommandsCoreAsync()
    {
        if (_pluginService is null) return;

        try
        {
            _modPluginCommands = await _pluginService.GetContributedCommandsAsync(
                PluginSDK.PluginCommandPlacements.ModContextMenu);

            // 先在局部集合里拼完整，再一次性替换。
            // 直接往 ObservableCollection 里 Clear + 逐个 Add，中间一旦有其他刷新插入就会重复。
            var actions = new List<LibraryPluginAction>();

            // 旧式命令贡献（commands[].placement = libraryToolbar）继续支持
            var toolbarCommands = await _pluginService.GetContributedCommandsAsync(
                PluginSDK.PluginCommandPlacements.LibraryToolbar);
            foreach (var command in toolbarCommands)
            {
                actions.Add(new LibraryPluginAction
                {
                    PluginId = command.PluginId,
                    PluginName = command.PluginName,
                    DisplayName = command.DisplayName,
                    Description = command.Description,
                    Command = command.Name,
                    RequiredPermission = command.RequiredPermission,
                    IsPermissionGranted = command.IsPermissionGranted
                });
            }

            // 声明式菜单贡献：可以只指向一个浮窗，不需要命令实现
            var bundles = await _pluginService.GetUiContributionsAsync() ?? new List<PluginUiBundle>();
            foreach (var bundle in bundles)
            {
                foreach (var menu in bundle.Ui.Menus)
                {
                    if (!PluginSDK.PluginCommandPlacements.IsLibraryToolbar(menu.Placement)) continue;
                    if (string.IsNullOrWhiteSpace(menu.Command) && string.IsNullOrWhiteSpace(menu.Dialog)) continue;

                    actions.Add(new LibraryPluginAction
                    {
                        PluginId = bundle.PluginId,
                        PluginName = bundle.PluginName,
                        DisplayName = string.IsNullOrWhiteSpace(menu.Label) ? menu.Id : menu.Label,
                        Description = menu.Description,
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

            LibraryPluginActions.Clear();
            foreach (var action in actions)
            {
                LibraryPluginActions.Add(action);
            }
        }
        catch (Exception ex)
        {
            _modPluginCommands = new List<PluginCommandContribution>();
            LibraryPluginActions.Clear();
            System.Diagnostics.Debug.WriteLine($"刷新插件命令失败：{ex.Message}");
        }
    }

    /// <summary>当前可用于模组右键菜单的插件命令快照，供视图构建菜单。</summary>
    public IReadOnlyList<PluginCommandContribution> GetModPluginCommands() => _modPluginCommands;

    /// <summary>
    /// 执行插件贡献到模组菜单的命令：把目标模组的元数据作为参数传给插件进程，
    /// 插件只能拿到名称/作者/说明这类展示信息，拿不到文件路径与密钥。
    /// </summary>
    [RelayCommand]
    private async Task RunModPluginCommandAsync(ModPluginInvocation? invocation)
    {
        if (invocation is null) return;
        if (_pluginService is null)
        {
            _notificationService.ShowError("插件服务不可用，无法执行该命令。", invocation.Command.DisplayName);
            return;
        }

        var (mod, command) = invocation;
        if (!command.IsPermissionGranted)
        {
            _notificationService.ShowWarning(
                $"该命令需要权限“{command.RequiredPermission}”，请在设置 → 插件中授权后再试。",
                command.DisplayName);
            return;
        }

        try
        {
            var payload = BuildModCommandArguments(mod);
            var result = await _pluginService.ExecutePluginCommandAsync(command.PluginId, command.Name, payload);

            if (result.Success)
            {
                PluginCommandResultReady?.Invoke(this, new PluginCommandResultEventArgs
                {
                    Title = command.DisplayName,
                    PluginName = command.PluginName,
                    Content = string.IsNullOrWhiteSpace(result.Message) ? "命令已执行完成。" : result.Message
                });
            }
            else
            {
                _notificationService.ShowError(
                    result.Error ?? result.Message ?? "插件命令执行失败。",
                    $"{command.DisplayName} 执行失败");
            }
        }
        catch (Exception ex)
        {
            _notificationService.ShowError(ex.Message, $"{command.DisplayName} 执行失败");
        }
    }

    /// <summary>
    /// 执行插件贡献到模组库工具栏的入口：可打开声明式浮窗，也可执行命令。
    /// </summary>
    [RelayCommand]
    private async Task RunLibraryPluginCommandAsync(LibraryPluginAction? action)
    {
        if (action is null) return;
        if (_pluginService is null)
        {
            _notificationService.ShowError("插件服务不可用，无法执行该命令。", action.DisplayName);
            return;
        }

        if (!action.IsPermissionGranted)
        {
            _notificationService.ShowWarning(
                $"该功能需要权限“{action.RequiredPermission}”，请在设置 → 插件中授权后再试。",
                action.DisplayName);
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(action.Dialog))
            {
                var surface = PluginUiSurfaceViewModel.CreateDialogSurface(action.Bundle, action.Dialog, _pluginService);
                if (surface is not null)
                {
                    PluginDialogReady?.Invoke(this, surface);
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(action.Command)) return;

            var result = await _pluginService.ExecutePluginCommandAsync(
                action.PluginId, action.Command, action.Arguments);

            if (result.Success)
            {
                PluginCommandResultReady?.Invoke(this, new PluginCommandResultEventArgs
                {
                    Title = action.DisplayName,
                    PluginName = action.PluginName,
                    Content = string.IsNullOrWhiteSpace(result.Message) ? "命令已执行完成。" : result.Message
                });
            }
            else
            {
                _notificationService.ShowError(
                    result.Error ?? result.Message ?? "插件命令执行失败。",
                    $"{action.DisplayName} 执行失败");
            }
        }
        catch (Exception ex)
        {
            _notificationService.ShowError(ex.Message, $"{action.DisplayName} 执行失败");
        }
    }

    private static string BuildModCommandArguments(ModItemViewModel mod)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"模组名称：{mod.Name}");
        if (!string.IsNullOrWhiteSpace(mod.Version)) builder.AppendLine($"版本：{mod.Version}");
        if (!string.IsNullOrWhiteSpace(mod.Author)) builder.AppendLine($"作者：{mod.Author}");
        if (!string.IsNullOrWhiteSpace(mod.Description)) builder.AppendLine($"说明：{mod.Description}");

        var payload = new Dictionary<string, object?>
        {
            ["mod"] = new Dictionary<string, object?>
            {
                ["id"] = mod.Id.ToString(),
                ["name"] = mod.Name,
                ["version"] = mod.Version,
                ["author"] = mod.Author,
                ["description"] = mod.Description
            },
            ["text"] = builder.ToString().TrimEnd()
        };

        return System.Text.Json.JsonSerializer.Serialize(payload);
    }

    public async Task RefreshProfileStateAsync()
    {
        _currentProfile = await _profileService.GetActiveProfileAsync();
        CurrentProfileName = _currentProfile?.Name ?? "无当前 Profile";
        var profileMods = _currentProfile?.Mods.ToDictionary(pm => pm.ModId) ?? new Dictionary<Guid, ProfileMod>();
        foreach (var mod in _allMods)
        {
            if (profileMods.TryGetValue(mod.Id, out var profileMod))
            {
                mod.IsInCurrentProfile = true;
                mod.IsEnabled = profileMod.Enabled;
                mod.Order = profileMod.Order;
            }
            else
            {
                mod.IsInCurrentProfile = false;
                mod.IsEnabled = false;
                mod.Order = 0;
            }
        }
        await ApplyFiltersAsync();
        await UpdateStatisticsAsync();
    }

    /// <summary>
    /// 加载所有模组
    /// </summary>
    [RelayCommand]
    private async Task LoadModsAsync()
    {
        IsLoading = true;
        HasError = false;
        ErrorMessage = string.Empty;

        try
        {
            // 获取当前配置文件
            _currentProfile = await _profileService.GetActiveProfileAsync();
            CurrentProfileName = _currentProfile?.Name ?? "无当前 Profile";

            // 获取所有模组
            var mods = await _modService.GetAllModsAsync();
            var modsList = mods.ToList();

            // 创建 ViewModels
            ClearSelection();
            DetachModSelectionHandlers();
            var noticeStyle = _settingsService?.GetSetting(SettingKeys.ModNoticeStyle, "Badge");
            _allMods = await Task.Run(() => modsList.Select(m =>
            {
                var item = new ModItemViewModel(m);
                item.ApplyNoticeStyle(noticeStyle);
                return item;
            }).ToList());
            foreach (var mod in _allMods) mod.CompatibilityBadge = Compatibility?.GetBadge(mod.Id);
            CategoryOptions = CreateCategoryOptions(_allMods.SelectMany(mod => mod.ResourceTypeIds));
            OnPropertyChanged(nameof(CategoryOptions));
            AttachModSelectionHandlers();

            // 如果有配置文件，设置启用状态
            if (_currentProfile != null)
            {
                var profileMods = _currentProfile.Mods.ToDictionary(pm => pm.ModId);
                foreach (var modVm in _allMods)
                {
                    if (profileMods.TryGetValue(modVm.Id, out var pm))
                    {
                        modVm.IsInCurrentProfile = true;
                        modVm.IsEnabled = pm.Enabled;
                        modVm.Order = pm.Order;
                    }
                    else
                    {
                        modVm.IsInCurrentProfile = false;
                    }
                }
            }

            var deployedModIds = (await _deploymentService.GetLatestDeploymentsAsync())
                .Where(d => Enum.TryParse<DeploymentStatus>(d.Status, out var status) &&
                            (status == DeploymentStatus.Completed || status == DeploymentStatus.PartiallyFailed))
                .Select(d => d.ModId)
                .ToHashSet();
            foreach (var modVm in _allMods)
            {
                modVm.IsDeployed = deployedModIds.Contains(modVm.Id);
            }
            DeploymentStateChanged?.Invoke(deployedModIds);

            // 应用筛选和排序
            await ApplyFiltersAsync();

            // 更新统计
            await UpdateStatisticsAsync();

            // 异步加载图标
            _ = LoadIconsAsync();

            IsEmpty = _allMods.Count == 0;
            UpdateResultState();
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"加载模组失败: {ex.Message}";
            _notificationService.ShowError(ErrorMessage, "加载失败");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 异步加载图标
    /// </summary>
    private async Task LoadIconsAsync()
    {
        // 加载完整数据源，筛选、排序和滚动后出现的卡片也需要图片。
        var mods = _allMods;
        foreach (var mod in mods.ToArray())
        {
            if (!ReferenceEquals(mods, _allMods)) return;
            await mod.LoadIconAsync();
        }
    }

    /// <summary>
    /// 执行搜索命令
    /// </summary>
    [RelayCommand]
    private async Task ExecuteSearchAsync()
    {
        // 取消之前的搜索
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();

        var token = _searchCts.Token;
        await SearchAsync(token);
    }

    /// <summary>
    /// 执行搜索
    /// </summary>
    private async Task SearchAsync(CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Increment(ref _filterGeneration);
        var searchGeneration = Interlocked.Increment(ref _searchGeneration);
        var searchText = SearchText;
        var filter = CurrentFilter;
        var category = SelectedCategory;
        var sortMode = CurrentSortMode;
        var searchOptions = SearchMatchOptions.FromSettings(_settingsService);
        IsSearching = true;

        try
        {
            if (string.IsNullOrWhiteSpace(searchText))
            {
                // 清空搜索，显示所有
                await ApplyFiltersAsync();
                if (searchGeneration == Volatile.Read(ref _searchGeneration))
                {
                    IsSearching = false;
                }
                return;
            }
            // 执行搜索
            var results = await _searchService.SearchAsync(searchText, searchOptions, cancellationToken);
            var resultIds = new HashSet<Guid>(results.Select(m => m.Id));

            // 筛选 _allMods
            var filtered = _allMods.Where(m => resultIds.Contains(m.Id));

            // 应用其他筛选条件
            filtered = ApplyFilter(filtered, filter, category);

            // 应用排序
            filtered = ApplySort(filtered, sortMode);

            if (generation != Volatile.Read(ref _filterGeneration)) return;

            FilteredMods = new ObservableCollection<ModItemViewModel>(filtered);
            FilteredModCount = FilteredMods.Count;
            IsAllSelected = FilteredMods.Count > 0 && FilteredMods.All(mod => mod.IsSelected);
            UpdateResultState();
        }
        finally
        {
            if (searchGeneration == Volatile.Read(ref _searchGeneration))
            {
                IsSearching = false;
            }
        }
    }

    /// <summary>
    /// 清空搜索
    /// </summary>
    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
    }

    /// <summary>
    /// 搜索输入防抖（沿用旧版 WPF 的做法）：停顿后自动执行搜索，回车仍可立即触发。
    /// </summary>
    partial void OnSearchTextChanged(string value)
    {
        _searchDebounceTimer?.Stop();
        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer?.Stop();
            SafeAsync.Run(ExecuteSearchAsync, "自动搜索");
        };
        _searchDebounceTimer.Start();
    }

    private void OnModNoticeDismissed(object? sender, Guid id)
    {
        void Apply() { _allMods.FirstOrDefault(mod => mod.Id == id)?.DismissNotice(); }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    /// <summary>设置页保存模组库/搜索选项后的运行时响应：搜索规则立即刷新，Lua 自动拆包开关驱动补拆或取消。</summary>
    private void OnLibrarySettingsChanged(object? sender, SettingChangedEventArgs e)
    {
        if (e.Key == SettingKeys.ModNoticeStyle)
        {
            var style = e.NewValue as string;
            void ApplyStyle() { foreach (var mod in _allMods) mod.ApplyNoticeStyle(style); }
            if (Dispatcher.UIThread.CheckAccess()) ApplyStyle();
            else Dispatcher.UIThread.Post(ApplyStyle);
            return;
        }
        if (e.Key is SettingKeys.SearchFuzzy or SettingKeys.SearchPinyin)
        {
            if (string.IsNullOrWhiteSpace(SearchText)) return;
            if (Dispatcher.UIThread.CheckAccess()) _ = ExecuteSearchAsync();
            else Dispatcher.UIThread.Post(() => _ = ExecuteSearchAsync());
            return;
        }
        if (e.Key == SettingKeys.LuaAutoExtract && _luaExtraction != null)
        {
            if (_luaExtraction.IsEnabled)
            {
                // 开启即补拆模组库全部存量模组；未处理过的模组由服务按记录跳过。
                if (Dispatcher.UIThread.CheckAccess()) _ = ExtractAllLuaAsync();
                else Dispatcher.UIThread.Post(() => _ = ExtractAllLuaAsync());
            }
            else
            {
                _luaExtractionCts?.Cancel();
            }
        }
    }

    /// <summary>开启开关后后台补拆全库 Lua；关闭开关或开启新一轮时取消在途任务。</summary>
    private async Task ExtractAllLuaAsync()
    {
        if (_luaExtraction == null) return;
        _luaExtractionCts?.Cancel();
        _luaExtractionCts?.Dispose();
        _luaExtractionCts = new CancellationTokenSource();
        var token = _luaExtractionCts.Token;
        try
        {
            var summary = await _luaExtraction.ExtractForLibraryAsync(token);
            ShowLuaExtractionSummary(summary);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 开关被关闭或新一轮补拆已取代本次任务。
        }
        catch (Exception ex)
        {
            _notificationService.ShowError(
                UiLocalization.Current.Format("Settings.Library.LuaExtractFailed", ex.Message),
                UiLocalization.Current["Settings.Library.LuaExtractTitle"]);
        }
    }

    /// <summary>导入批次完成后按自动拆包设置在后台提取新模组的 Lua；与兼容性检查同样不并入 ImportCompletion。</summary>
    private async Task ExtractImportedLuaAsync()
    {
        if (_luaExtraction == null || Import.ImportedIds.Count == 0) return;
        var importedIds = Import.ImportedIds.ToArray();
        var token = _luaExtractionCts?.Token ?? CancellationToken.None;
        try
        {
            var summary = await _luaExtraction.ExtractForModsAsync(importedIds, token);
            ShowLuaExtractionSummary(summary);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 开关被关闭；已处理模组有记录，重开后不会重复提取。
        }
        catch (Exception ex)
        {
            _notificationService.ShowError(
                UiLocalization.Current.Format("Settings.Library.LuaExtractFailed", ex.Message),
                UiLocalization.Current["Settings.Library.LuaExtractTitle"]);
        }
    }

    private void ShowLuaExtractionSummary(LuaExtractionBatchSummary summary)
    {
        if (summary.ProcessedMods == 0) return;
        _notificationService.ShowInfo(
            UiLocalization.Current.Format("Settings.Library.LuaExtractedSummary",
                summary.ProcessedMods, summary.SourceCount, summary.BytecodeCount),
            UiLocalization.Current["Settings.Library.LuaExtractTitle"]);
    }

    /// <summary>
    /// 筛选
    /// </summary>
    [RelayCommand]
    private async Task FilterAsync(ModFilterType filterType)
    {
        CurrentFilter = filterType;
        await ApplyFiltersAsync();
    }

    /// <summary>
    /// 按分类筛选
    /// </summary>
    [RelayCommand]
    private async Task FilterByCategoryAsync(ulong? category)
    {
        SelectedCategory = category;
        await ApplyFiltersAsync();
    }

    [RelayCommand]
    private async Task ClearFiltersAsync()
    {
        SelectedCategory = null;
        CurrentFilter = ModFilterType.All;
        await ApplyFiltersAsync();
    }

    /// <summary>
    /// 应用所有筛选条件
    /// </summary>
    private async Task ApplyFiltersAsync()
    {
        var generation = Interlocked.Increment(ref _filterGeneration);
        var searchText = SearchText;
        var filter = CurrentFilter;
        var category = SelectedCategory;
        var sortMode = CurrentSortMode;
        var searchOptions = SearchMatchOptions.FromSettings(_settingsService);
        var filtered = await Task.Run(() =>
        {
            var filtered = _allMods.AsEnumerable();

            // 应用搜索（如果有）
            if (!string.IsNullOrWhiteSpace(searchText))
            {
                filtered = filtered.Where(m =>
                    SearchMatcher.MatchAny(searchText, searchOptions, m.Name, m.Description, m.Author)
                    != SearchMatchKind.None
                    || m.Tags.Any(tag => SearchMatcher.Match(tag, searchText, searchOptions) != SearchMatchKind.None));
            }

            // 应用状态筛选
            filtered = ApplyFilter(filtered, filter, category);

            // 应用排序
            filtered = ApplySort(filtered, sortMode);

            return filtered.ToList();
        });

        if (generation != Volatile.Read(ref _filterGeneration)) return;

        FilteredMods = new ObservableCollection<ModItemViewModel>(filtered);
        FilteredModCount = filtered.Count;
        IsAllSelected = FilteredMods.Count > 0 && FilteredMods.All(mod => mod.IsSelected);
        UpdateResultState();
    }

    /// <summary>
    /// 应用筛选条件
    /// </summary>
    private IEnumerable<ModItemViewModel> ApplyFilter(IEnumerable<ModItemViewModel> mods)
        => ApplyFilter(mods, CurrentFilter, SelectedCategory);

    private static IEnumerable<ModItemViewModel> ApplyFilter(
        IEnumerable<ModItemViewModel> mods,
        ModFilterType filter,
        ulong? category)
    {
        // 状态筛选
        mods = filter switch
        {
            ModFilterType.Enabled => mods.Where(m => m.IsEnabled),
            ModFilterType.Disabled => mods.Where(m => m.IsInCurrentProfile && !m.IsEnabled),
            ModFilterType.CurrentProfile => mods.Where(m => m.IsInCurrentProfile),
            ModFilterType.Deployed => mods.Where(m => m.IsDeployed),
            ModFilterType.NotDeployed => mods.Where(m => !m.IsDeployed),
            _ => mods
        };

        // 分类筛选
        if (category.HasValue)
        {
            mods = mods.Where(m => m.ResourceTypeIds.Contains(category.Value));
        }

        return mods;
    }

    /// <summary>
    /// 排序
    /// </summary>
    [RelayCommand]
    private async Task SortAsync(ModSortMode sortMode)
    {
        CurrentSortMode = sortMode;
        await ApplyFiltersAsync();
    }

    /// <summary>
    /// 应用排序
    /// </summary>
    private IEnumerable<ModItemViewModel> ApplySort(IEnumerable<ModItemViewModel> mods)
        => ApplySort(mods, CurrentSortMode);

    private static IEnumerable<ModItemViewModel> ApplySort(
        IEnumerable<ModItemViewModel> mods,
        ModSortMode sortMode)
    {
        return sortMode switch
        {
            ModSortMode.NameAscending => mods.OrderBy(m => m.Name),
            ModSortMode.NameDescending => mods.OrderByDescending(m => m.Name),
            ModSortMode.DateAddedNewest => mods.OrderByDescending(m => m.ImportedAt),
            ModSortMode.DateAddedOldest => mods.OrderBy(m => m.ImportedAt),
            ModSortMode.Category => mods.OrderBy(m => m.CategoryDisplay).ThenBy(m => m.Name),
            _ => mods
        };
    }

    /// <summary>
    /// 切换当前筛选结果的全选状态。
    /// </summary>
    [RelayCommand]
    private void SelectAll() => _modSelection.ToggleAll();

    /// <summary>Ctrl+A 始终选择当前结果，重复按下不反转。</summary>
    [RelayCommand]
    private void SelectAllVisible() => _modSelection.SelectAll(FocusedMod);

    /// <summary>
    /// 反选当前筛选结果，筛选之外的模组保持原状态。
    /// </summary>
    [RelayCommand]
    private void InvertSelection() => _modSelection.Invert();

    [RelayCommand]
    private void FocusSearch()
    {
        SearchFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenFocusedMod()
    {
        var mod = FocusedMod ?? SelectedMods.FirstOrDefault();
        if (mod != null)
        {
            ShowModDetail(mod);
        }
    }

    /// <summary>
    /// 普通单击只聚焦并设置范围锚点，保持单击不进入批量选择的设计。
    /// </summary>
    public void FocusMod(ModItemViewModel mod)
    {
        if (_modSelection.Focus(mod)) FocusedMod = mod;
    }

    /// <summary>点击卡片后清除新导入/已更新提示；先更新界面，再持久化清除结果。</summary>
    public async Task DismissModNoticeAsync(ModItemViewModel mod)
    {
        if (!mod.HasNotice) return;
        mod.DismissNotice();
        await _modService.DismissModNoticeAsync(mod.Id);
    }

    /// <summary>Ctrl+点击切换多选，并把本次点击作为新的范围锚点。</summary>
    public void ToggleSelected(ModItemViewModel mod)
    {
        if (_modSelection.Toggle(mod)) FocusedMod = mod;
    }

    /// <summary>Shift 替换范围，Ctrl+Shift 追加范围；连续操作保持最初锚点。</summary>
    public void SelectRange(ModItemViewModel mod, bool additive = false)
    {
        if (_modSelection.SelectRange(mod, additive)) FocusedMod = mod;
    }

    /// <summary>
    /// 清空选择
    /// </summary>
    [RelayCommand]
    private void ClearSelection() => _modSelection.Clear();

    /// <summary>
    /// 打开"加入配置文件"弹窗：目标 Mod 为空表示对整个选中集合操作，可勾选多个配置文件。
    /// </summary>
    [RelayCommand]
    private async Task OpenAddToProfileDialogAsync(ModItemViewModel? mod)
    {
        var targetMods = mod != null ? new List<ModItemViewModel> { mod } : SelectedMods.ToList();
        if (targetMods.Count == 0)
        {
            _notificationService.ShowInfo("请先选择要加入的 Mod", "加入配置文件");
            return;
        }

        var profiles = (await _profileService.GetAllProfilesAsync()).ToList();
        if (profiles.Count == 0)
        {
            _notificationService.ShowWithNavigation(NotificationLevel.Warning, "请先创建一个配置文件", "打开配置文件", NotificationNavigationTarget.Profiles, "加入配置文件");
            return;
        }

        _profilesForAdd = profiles;
        AddToProfileTarget = mod;
        ShowAddToProfileGroupPicker = _settingsService?.GetSetting(SettingKeys.ProfilesChooseGroupWhenAdding, false) == true;
        ClearAddToProfileChoices();
        foreach (var profile in profiles)
        {
            var choice = new ProfileChoiceViewModel(
                profile.Id, profile.Name, profile.Mods.Count,
                profile.Id == _currentProfile?.Id);
            choice.PropertyChanged += OnAddToProfileChoiceChanged;
            AddToProfileChoices.Add(choice);
        }
        RefreshAddToProfileGroups();
        OnPropertyChanged(nameof(AddToProfileDialogTitle));
        IsAddToProfileDialogOpen = true;
    }

    private void OnAddToProfileChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProfileChoiceViewModel.IsSelected)) return;
        RefreshAddToProfileGroups();
        OnPropertyChanged(nameof(HasSingleAddToProfileChoice));
        OnPropertyChanged(nameof(AddToProfileGroupHintVisible));
        OnPropertyChanged(nameof(AddToProfileGroupEmptyHintVisible));
    }

    /// <summary>目标分组来自当前唯一勾选的配置文件；多选时保持为空，确认时走原追加逻辑。</summary>
    private void RefreshAddToProfileGroups()
    {
        var selected = AddToProfileChoices.Where(choice => choice.IsSelected).ToList();
        var profile = selected.Count == 1
            ? _profilesForAdd?.FirstOrDefault(item => item.Id == selected[0].Id)
            : null;
        AddToProfileGroups.Clear();
        if (profile != null)
            foreach (var separator in profile.Separators.OrderBy(item => item.Order))
                AddToProfileGroups.Add(new ProfileGroupChoiceViewModel(separator.Id, separator.Name));
        SelectedAddToProfileGroup = AddToProfileGroups.FirstOrDefault();
        OnPropertyChanged(nameof(HasAddToProfileGroups));
        OnPropertyChanged(nameof(AddToProfileGroupHintVisible));
        OnPropertyChanged(nameof(AddToProfileGroupEmptyHintVisible));
    }

    private void ClearAddToProfileChoices()
    {
        foreach (var choice in AddToProfileChoices) choice.PropertyChanged -= OnAddToProfileChoiceChanged;
        AddToProfileChoices.Clear();
        AddToProfileGroups.Clear();
        SelectedAddToProfileGroup = null;
        ChooseAddToProfileGroup = false;
        OnPropertyChanged(nameof(HasAddToProfileGroups));
        OnPropertyChanged(nameof(HasSingleAddToProfileChoice));
        OnPropertyChanged(nameof(AddToProfileGroupHintVisible));
        OnPropertyChanged(nameof(AddToProfileGroupEmptyHintVisible));
    }

    [RelayCommand]
    private void CancelAddToProfile()
    {
        IsAddToProfileDialogOpen = false;
        AddToProfileTarget = null;
        _profilesForAdd = null;
        ShowAddToProfileGroupPicker = false;
        ClearAddToProfileChoices();
    }

    public async Task OpenAddToOtherProfilesAsync(ModItemViewModel mod, Guid sourceProfileId)
    {
        await OpenAddToProfileDialogAsync(mod);
        foreach (var choice in AddToProfileChoices.ToArray())
        {
            if (choice.Id == sourceProfileId) AddToProfileChoices.Remove(choice);
            else choice.IsSelected = false;
        }
        RefreshAddToProfileGroups();
        if (AddToProfileChoices.Count == 0)
        {
            CancelAddToProfile();
            _notificationService.ShowInfo("请先创建一个配置文件", "加入配置文件");
        }
    }

    /// <summary>
    /// 确认加入：每个勾选的配置文件调用一次批量接口（服务端一次读取一次更新，
    /// 未在 ProfileMods 中的模组补录并分配稳定顺序，加入即启用）。
    /// </summary>
    [RelayCommand]
    private async Task ConfirmAddToProfileAsync()
    {
        var targets = AddToProfileChoices.Where(choice => choice.IsSelected).ToList();
        if (targets.Count == 0)
        {
            _notificationService.ShowInfo("请至少勾选一个配置文件", "加入配置文件");
            return;
        }

        var targetMods = AddToProfileTarget != null
            ? new List<ModItemViewModel> { AddToProfileTarget }
            : SelectedMods.ToList();
        var modIds = targetMods.Select(m => m.Id).ToArray();

        IsLoading = true;
        try
        {
            var group = ChooseAddToProfileGroup && targets.Count == 1 ? SelectedAddToProfileGroup : null;
            if (group != null)
            {
                await _profileService.AddModsToProfileGroupAsync(targets[0].Id, modIds, group.Id);
            }
            else
            {
                foreach (var choice in targets)
                {
                    await _profileService.SetModsEnabledAsync(choice.Id, modIds, true);
                }
            }

            var activeProfileId = _currentProfile?.Id;
            if (targets.Any(choice => choice.Id == activeProfileId))
            {
                foreach (var mod in targetMods)
                {
                    mod.IsInCurrentProfile = true;
                    mod.IsEnabled = true;
                }
            }

            await UpdateStatisticsAsync();
            CancelAddToProfile();
            _notificationService.ShowSuccess($"已将 {targetMods.Count} 个 Mod 加入 {targets.Count} 个配置文件", "加入配置文件");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"加入配置文件失败: {ex.Message}", "加入配置文件");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 批量禁用
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task BatchDisableAsync()
    {
        if (_currentProfile == null)
        {
            _notificationService.ShowWithNavigation(NotificationLevel.Warning, "请先选择一个配置文件", "打开配置文件", NotificationNavigationTarget.Profiles, "无配置文件");
            return;
        }

        IsLoading = true;
        try
        {
            var selected = SelectedMods.ToList();
            await _profileService.SetModsEnabledAsync(_currentProfile.Id, selected.Select(mod => mod.Id).ToArray(), false);
            foreach (var mod in selected) mod.IsEnabled = false;

            await UpdateStatisticsAsync();
            _notificationService.ShowSuccess($"已禁用 {SelectedCount} 个模组", "批量禁用");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"批量禁用失败: {ex.Message}", "操作失败");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 从当前 Profile 移除选中的 Mod，但保留 Library 资产。
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveSelectedFromProfileAsync()
    {
        if (_currentProfile == null)
        {
            _notificationService.ShowWarning("当前没有可操作的 Profile", "从 Profile 移除");
            return;
        }

        var selected = SelectedMods.Where(mod => mod.IsInCurrentProfile).ToList();
        if (selected.Count == 0)
        {
            _notificationService.ShowInfo("所选 Mod 均未加入当前 Profile", "从 Profile 移除");
            return;
        }

        if (!await _confirmationService.ConfirmAsync(
                "从当前 Profile 移除",
                $"将从“{_currentProfile.Name}”移除 {selected.Count} 个 Mod，Library 中的 Mod 文件不会删除。是否继续？"))
        {
            return;
        }

        IsLoading = true;
        try
        {
            foreach (var mod in selected)
            {
                await _profileService.RemoveModFromProfileAsync(_currentProfile.Id, mod.Id);
                mod.IsInCurrentProfile = false;
                mod.IsEnabled = false;
                mod.Order = 0;
            }

            await ApplyFiltersAsync();
            await UpdateStatisticsAsync();
            _notificationService.ShowSuccess($"已从当前 Profile 移除 {selected.Count} 个 Mod，Library 资产已保留", "从 Profile 移除");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"从 Profile 移除失败: {ex.Message}", "操作失败");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 批量删除
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task BatchDeleteAsync()
    {
        var selectedCount = SelectedMods.Count;
        if (selectedCount == 0) return;
        if (!await _confirmationService.ConfirmAsync("删除模组", $"确定删除选中的 {selectedCount} 个模组吗？此操作会删除模组文件。"))
        {
            return;
        }
        IsLoading = true;
        try
        {
            var modsToDelete = SelectedMods.ToList();
            foreach (var mod in modsToDelete)
            {
                await _modService.DeleteModAsync(mod.Id);
                SyncListSelection(Array.Empty<ModItemViewModel>(), new[] { mod });
                mod.PropertyChanged -= OnModPropertyChanged;
                _allMods.Remove(mod);
                FilteredMods.Remove(mod);

                // 从搜索索引中移除
                _searchService.RemoveModFromIndex(mod.Id);
            }

            ClearSelection();
            await UpdateStatisticsAsync();
            _notificationService.ShowSuccess($"已删除 {modsToDelete.Count} 个模组", "批量删除");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"批量删除失败: {ex.Message}", "操作失败");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 单个模组启用/禁用
    /// </summary>
    [RelayCommand]
    private async Task ToggleModEnabledAsync(ModItemViewModel mod)
    {
        if (_currentProfile == null) return;

        try
        {
            var newState = !mod.IsEnabled;
            await _profileService.SetModEnabledAsync(_currentProfile.Id, mod.Id, newState);
            mod.IsInCurrentProfile = true;
            mod.IsEnabled = newState;
            await UpdateStatisticsAsync();
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"操作失败: {ex.Message}", "错误");
        }
    }

    /// <summary>
    /// 删除单个模组
    /// </summary>
    [RelayCommand]
    private async Task DeleteModAsync(ModItemViewModel mod)
    {
        if (!await _confirmationService.ConfirmAsync("删除模组", $"确定删除“{mod.Name}”吗？此操作会删除模组文件。"))
        {
            return;
        }
        IsLoading = true;
        try
        {
            await _modService.DeleteModAsync(mod.Id);
            SyncListSelection(Array.Empty<ModItemViewModel>(), new[] { mod });
            mod.PropertyChanged -= OnModPropertyChanged;
            _allMods.Remove(mod);
            FilteredMods.Remove(mod);
            _searchService.RemoveModFromIndex(mod.Id);

            await UpdateStatisticsAsync();
            _notificationService.ShowSuccess($"已删除 {mod.Name}", "删除模组");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"删除失败: {ex.Message}", "错误");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 将单个模组加入当前 Profile（服务端批量接口会补录 ProfileMods 并分配稳定顺序，加入即启用）
    /// </summary>
    [RelayCommand]
    private async Task AddModToProfileAsync(ModItemViewModel mod)
    {
        if (_currentProfile == null)
        {
            _notificationService.ShowWithNavigation(NotificationLevel.Warning, "请先选择一个配置文件", "打开配置文件", NotificationNavigationTarget.Profiles, "加入 Profile");
            return;
        }
        if (mod.IsInCurrentProfile) return;

        try
        {
            await _profileService.SetModEnabledAsync(_currentProfile.Id, mod.Id, true);
            mod.IsInCurrentProfile = true;
            mod.IsEnabled = true;
            await UpdateStatisticsAsync();
            _notificationService.ShowSuccess($"已将“{mod.Name}”加入当前 Profile", "加入 Profile");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"加入 Profile 失败: {ex.Message}", "加入 Profile");
        }
    }

    /// <summary>
    /// 导入模组
    /// </summary>
    public Task ImportPathsAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default, ModPlatformLink? sourceLink = null)
    {
        if (!ImportCompletion.IsCompleted) return Task.CompletedTask;
        ImportCompletion = ImportAndRefreshAsync(paths, cancellationToken, sourceLink);
        return ImportCompletion;
    }

    private async Task ImportAndRefreshAsync(IEnumerable<string> paths, CancellationToken cancellationToken, ModPlatformLink? sourceLink)
    {
        var sourcePaths = paths.Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sourcePaths.Count == 0) return;

        IsLoading = true;
        try
        {
            await Import.ImportAsync(sourcePaths, cancellationToken, sourceLink, PrepareImportGroupSelectionAsync);
            await LoadModsAsync();
            if (Import.ImportedIds.Count == 0) Import.DismissImportGroupSelection();
            if (Import.FailedCount > 0 || Import.RemainingCount > 0)
                _notificationService.ShowWarning($"{Import.ResultText}，未完成 {Import.RemainingCount} 个", Import.Step);
            else
                _notificationService.ShowSuccess($"已导入 {Import.SuccessCount} 个模组", "导入完成");
        }
        catch (OperationCanceledException)
        {
            _notificationService.ShowWarning("模组导入已取消", "导入模组");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"导入失败: {ex.Message}", "导入模组");
        }
        finally
        {
            IsLoading = false;
        }
        // Worker 进程写库不产生主进程事件，这里补发一次：成功、部分成功与取消（已导入的部分保留）都要联动刷新。
        if (Import.ImportedIds.Count > 0) ImportedModsChanged?.Invoke(this, EventArgs.Empty);
        // 批次结束（含取消）后统一检查本次导入的模组；不并入 ImportCompletion，下载交接无需等待检查。
        // 保留可等待句柄，退出流程与测试收尾需要确认这些后台工作不再触碰已释放的服务。
        ImportPostProcessing = Task.WhenAll(CheckImportedCompatibilityAsync(), ExtractImportedLuaAsync());
    }

    /// <summary>导入前为自动加入当前配置准备可选分组；任一开关关闭或没有分组时保持原追加逻辑。</summary>
    private async Task PrepareImportGroupSelectionAsync()
    {
        if (_settingsService?.GetSetting(SettingKeys.ProfilesAutoAddImportedMods, false) != true
            || _settingsService.GetSetting(SettingKeys.ProfilesChooseGroupWhenAdding, false) != true) return;
        try
        {
            var profile = await _profileService.GetActiveProfileAsync();
            if (profile != null) Import.ShowImportGroupSelection(profile.Id, profile.Separators);
        }
        catch
        {
            // 读取失败时保持原追加逻辑。
        }
    }

    [RelayCommand]
    private async Task ApplyImportGroupAsync()
    {
        var group = Import.SelectedImportGroup;
        if (group == null || Import.ImportGroupProfileId == Guid.Empty || Import.ImportedIds.Count == 0) return;
        IsLoading = true;
        try
        {
            var moved = await _profileService.MoveModsToGroupAsync(
                Import.ImportGroupProfileId, group.Id, Import.ImportedIds.ToArray());
            Import.DismissImportGroupSelection();
            if (moved == 0)
                _notificationService.ShowInfo("本次导入的模组都位于固定加载器区域，不能加入用户分组。", "导入完成");
            else
                _notificationService.ShowSuccess($"已将 {moved} 个模组加入分组“{group.Name}”", "导入完成");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"加入分组失败: {ex.Message}", "导入完成");
        }
        finally { IsLoading = false; }
    }

    /// <summary>整批导入完成后按导入自动检查设置在后台检查新模组；一次批次只触发一次。</summary>
    private async Task CheckImportedCompatibilityAsync()
    {
        try
        {
            if (Compatibility == null || Import.ImportedIds.Count == 0) return;
            var importedIds = Import.ImportedIds.ToArray();
            var imported = _allMods.Where(mod => importedIds.Contains(mod.Id)).ToArray();
            if (imported.Length > 0) await Compatibility.CheckAfterImportAsync(imported, _notificationService);
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"导入后兼容性检查失败: {ex.Message}", "兼容性检查");
        }
    }

    /// <summary>
    /// 刷新
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadModsAsync();
    }

    [RelayCommand]
    private async Task DeployAsync(Guid? profileId)
    {
        if (IsDeploying) return;
        IsDeploying = true;
        ResetDeploymentProgress();
        DeploymentTitle = "部署模组";
        IsDeploymentProgressOpen = true;
        _deploymentCts = new CancellationTokenSource();
        var deploymentCts = _deploymentCts;
        try
        {
            var profile = profileId.HasValue
                ? await _profileService.GetProfileByIdAsync(profileId.Value, deploymentCts.Token)
                : await _profileService.GetActiveProfileAsync(deploymentCts.Token);
            if (profile == null)
            {
                _notificationService.ShowWithNavigation(NotificationLevel.Warning, "请先选择配置文件", "打开配置文件", NotificationNavigationTarget.Profiles, "部署模组");
                return;
            }
            if (IsHellForgeMode)
            {
                DeploymentTitle = "同步到 HellForge";
                var hfOrder = await ReadDeploymentOrderAsync();
                // 同步按设置中的部署方式写入 mods\（不调 mod-add）：确认文案须让用户看到实际写入方式。
                var hfMethod = _settingsService == null ? DeploymentMethod.Copy.ToString()
                    : await _settingsService.GetSettingAsync(SettingKeys.DefaultDeploymentMethod, DeploymentMethod.Copy.ToString());
                var hfMethodName = Enum.TryParse<DeploymentMethod>(hfMethod, true, out var parsedMethod)
                    ? parsedMethod switch
                    {
                        DeploymentMethod.HardLink => "硬链接",
                        DeploymentMethod.SymbolicLink => "符号链接",
                        _ => "文件复制"
                    }
                    : "文件复制";
                await RunHellForgeSyncTaskAsync("同步到 HellForge", "同步到 HellForge",
                    $"配置：{profile.Name}\n启用模组：{profile.Mods.Count(mod => mod.Enabled)} 个\n写入方式：{hfMethodName}（按设置中的部署方式，不可用时自动回退为文件复制）\n将把启用的普通模组导入 HellForge 的 mods 目录（Lua 加载器与 HD2IAS 模组跳过），由 HellForge 会话负责暂存与清理，不写入游戏副本。是否继续？",
                    new Dictionary<string, string>
                    {
                        ["Mode"] = "Sync",
                        ["ProfileId"] = profile.Id.ToString(),
                        ["Order"] = hfOrder.ToString()
                    }, deploymentCts);
                return;
            }
            var gamePath = _gameDetectionService.CurrentGameInfo?.Path;
            if (string.IsNullOrWhiteSpace(gamePath))
            {
                _notificationService.ShowWithNavigation(NotificationLevel.Warning, "未检测到有效的游戏目录", "打开设置", NotificationNavigationTarget.Settings, "部署模组");
                return;
            }
            var savedMethod = _settingsService == null ? DeploymentMethod.Copy.ToString()
                : await _settingsService.GetSettingAsync(SettingKeys.DefaultDeploymentMethod, DeploymentMethod.Copy.ToString());
            CurrentDeploymentMethod = Enum.TryParse<DeploymentMethod>(savedMethod, out var method) && Enum.IsDefined(method)
                ? method : DeploymentMethod.Copy;
            var methodName = CurrentDeploymentMethod switch
            {
                DeploymentMethod.HardLink => "硬链接", DeploymentMethod.SymbolicLink => "符号链接", _ => "文件复制"
            };
            DeploymentMessage = "等待确认部署...";
            if (!await _confirmationService.ConfirmAsync("部署模组",
                    $"配置：{profile.Name}\n启用模组：{profile.Mods.Count(mod => mod.Enabled)} 个\n部署方式：{methodName}\n部署位置：{Path.Combine(gamePath, "data")}\n将清理之前的部署并按配置顺序部署，是否继续？"))
                return;

            DeploymentMessage = "正在准备部署...";
            var progress = new Progress<TaskProgress>(p =>
            {
                if (ReferenceEquals(_deploymentCts, deploymentCts) && !deploymentCts.IsCancellationRequested
                    && !string.IsNullOrWhiteSpace(p.Message))
                    DeploymentMessage = p.Message;
            });
            var savedOrder = _settingsService == null ? DeploymentOrder.TopToBottom.ToString()
                : await _settingsService.GetSettingAsync(SettingKeys.DefaultDeploymentOrder, DeploymentOrder.TopToBottom.ToString());
            var deploymentOrder = savedOrder == DeploymentOrder.BottomToTop.ToString()
                ? DeploymentOrder.BottomToTop : DeploymentOrder.TopToBottom;
            // 部署在独立 Worker 进程执行：清理/复制大量文件不会卡住 UI，进程崩溃也只影响本任务。
            var outcome = await RequireTaskService().RunProcessTaskAsync("部署模组", "Deployment",
                new Dictionary<string, string>
                {
                    ["ProfileId"] = profile.Id.ToString(),
                    ["Method"] = CurrentDeploymentMethod.ToString(),
                    ["Order"] = deploymentOrder.ToString(),
                    ["GamePath"] = gamePath
                }, progress, cancellationToken: deploymentCts.Token);
            ShowDeploymentOutcome(outcome.Task);
            await LoadModsAsync();
        }
        catch (OperationCanceledException)
        {
            _notificationService.ShowWarning("部署已取消", "部署模组");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"部署失败: {ex.Message}", "部署模组");
        }
        finally
        {
            _deploymentCts = null;
            deploymentCts.Dispose();
            IsDeploying = false;
            IsDeploymentProgressOpen = false;
            DeploymentMessage = string.Empty;
        }
    }
    [RelayCommand]
    private async Task CleanupAsync()
    {
        if (IsDeploying) return;
        if (IsHellForgeMode)
        {
            IsDeploying = true;
            ResetDeploymentProgress();
            DeploymentTitle = "清理 HellForge 模组";
            IsDeploymentProgressOpen = true;
            _deploymentCts = new CancellationTokenSource();
            var hfCts = _deploymentCts;
            try
            {
                await RunHellForgeSyncTaskAsync("清理 HellForge 模组", "清理 HellForge 模组",
                    "将从 HellForge 的 mods 目录移除本管理器同步的全部模组（不影响游戏副本与他人模组）。是否继续？",
                    new Dictionary<string, string> { ["Mode"] = "Remove" }, hfCts);
            }
            catch (OperationCanceledException)
            {
                _notificationService.ShowWarning("同步清理已取消", "清理 HellForge 模组");
            }
            catch (Exception ex)
            {
                _notificationService.ShowError($"同步清理失败: {ex.Message}", "清理 HellForge 模组");
            }
            finally
            {
                _deploymentCts = null;
                hfCts.Dispose();
                IsDeploying = false;
                IsDeploymentProgressOpen = false;
                DeploymentMessage = string.Empty;
                ResetDeploymentProgress();
            }
            return;
        }
        if (!await _confirmationService.ConfirmAsync(
                "清理部署",
                "将删除最近一次部署产生的目标文件和部署记录。是否继续？"))
        {
            return;
        }
        if (IsDeploying) return;

        IsDeploying = true;
        ResetDeploymentProgress();
        DeploymentTitle = "清理部署";
        IsDeploymentProgressOpen = true;
        _deploymentCts = new CancellationTokenSource();
        var deploymentCts = _deploymentCts;
        DeploymentMessage = "正在清理部署文件...";
        try
        {
            var progress = new Progress<TaskProgress>(p =>
            {
                if (!ReferenceEquals(_deploymentCts, deploymentCts) || deploymentCts.IsCancellationRequested) return;
                if (!string.IsNullOrWhiteSpace(p.Message)) DeploymentMessage = p.Message;
                if (!string.IsNullOrWhiteSpace(p.CurrentItem))
                {
                    // 文件进度一开始就是 "0 / N"，此时也应结束不确定态。
                    DeploymentFileCount = p.CurrentItem;
                    IsDeploymentIndeterminate = false;
                    DeploymentProgress = Math.Clamp(p.Progress, 0, 100);
                }
            });
            var outcome = await RequireTaskService().RunProcessTaskAsync("清理部署", "Cleanup",
                progressObserver: progress, cancellationToken: deploymentCts.Token);
            switch (outcome.Task?.Status)
            {
                case TaskStatus.Completed:
                    _notificationService.ShowSuccess(outcome.Task.Result ?? "清理完成", "清理完成");
                    break;
                case TaskStatus.Cancelled:
                    _notificationService.ShowWarning("清理部署已取消", "清理部署");
                    break;
                case TaskStatus.ForceTerminated:
                    _notificationService.ShowWarning("清理部署已强制终止", "清理部署");
                    break;
                default:
                    _notificationService.ShowError(outcome.Task?.Message ?? "清理失败", "清理部署");
                    break;
            }
            await LoadModsAsync();
        }
        catch (OperationCanceledException)
        {
            _notificationService.ShowWarning("清理部署已取消", "清理部署");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"清理失败: {ex.Message}", "清理部署");
        }
        finally
        {
            _deploymentCts = null;
            deploymentCts.Dispose();
            IsDeploying = false;
            IsDeploymentProgressOpen = false;
            DeploymentMessage = string.Empty;
            ResetDeploymentProgress();
        }
    }

    private TaskService RequireTaskService()
        => _taskService ?? throw new InvalidOperationException("部署与清理需要在任务服务可用时运行。");

    private void ShowDeploymentOutcome(ModTask? task)
    {
        switch (task?.Status)
        {
            case TaskStatus.Completed:
                _notificationService.ShowSuccess(string.IsNullOrWhiteSpace(task.Result) ? "部署完成" : task.Result, "部署完成");
                break;
            case TaskStatus.PartiallyFailed:
                _notificationService.ShowWarning(task.Result ?? "部署未完全成功", "部署结果");
                break;
            case TaskStatus.Cancelled:
                _notificationService.ShowWarning("部署已取消", "部署模组");
                break;
            case TaskStatus.ForceTerminated:
                _notificationService.ShowWarning("部署已强制终止", "部署模组");
                break;
            default:
                _notificationService.ShowError(task?.Message ?? "部署失败", "部署模组");
                break;
        }
    }

    private bool IsHellForgeMode =>
        _settingsService?.GetGameInstallationMode() == GameInstallationMode.HellForge;

    private async Task<DeploymentOrder> ReadDeploymentOrderAsync()
    {
        var savedOrder = _settingsService == null ? DeploymentOrder.TopToBottom.ToString()
            : await _settingsService.GetSettingAsync(SettingKeys.DefaultDeploymentOrder, DeploymentOrder.TopToBottom.ToString());
        return savedOrder == DeploymentOrder.BottomToTop.ToString() ? DeploymentOrder.BottomToTop : DeploymentOrder.TopToBottom;
    }

    /// <summary>HellForge 模式：部署=同步模组进 mods\，清理=移除本管理器同步的模组；经独立 Worker 进程执行。</summary>
    private async Task RunHellForgeSyncTaskAsync(string taskTitle, string confirmTitle, string confirmMessage,
        Dictionary<string, string> args, CancellationTokenSource cts)
    {
        DeploymentMessage = "等待确认...";
        if (!await _confirmationService.ConfirmAsync(confirmTitle, confirmMessage)) return;
        DeploymentMessage = "正在同步到 HellForge...";
        var progress = new Progress<TaskProgress>(p =>
        {
            if (ReferenceEquals(_deploymentCts, cts) && !cts.IsCancellationRequested && !string.IsNullOrWhiteSpace(p.Message))
                DeploymentMessage = p.Message;
        });
        var outcome = await RequireTaskService().RunProcessTaskAsync(taskTitle, "HellForgeSync", args, progress,
            cancellationToken: cts.Token);
        ShowHellForgeSyncOutcome(outcome.Task, taskTitle);
        await LoadModsAsync();
    }

    private void ShowHellForgeSyncOutcome(ModTask? task, string title)
    {
        switch (task?.Status)
        {
            case TaskStatus.Completed:
                _notificationService.ShowSuccess(string.IsNullOrWhiteSpace(task.Result) ? "同步完成" : task.Result, title);
                break;
            case TaskStatus.PartiallyFailed:
                _notificationService.ShowWarning(task.Result ?? "同步未完全成功", title);
                break;
            case TaskStatus.Cancelled:
                _notificationService.ShowWarning("同步已取消", title);
                break;
            case TaskStatus.ForceTerminated:
                _notificationService.ShowWarning("同步已强制终止", title);
                break;
            default:
                _notificationService.ShowError(task?.Message ?? "同步失败", title);
                break;
        }
    }

    private void ResetDeploymentProgress()
    {
        DeploymentProgress = 0;
        IsDeploymentIndeterminate = true;
        DeploymentFileCount = string.Empty;
    }

    public async Task CancelDeploymentAndWaitAsync()
    {
        _deploymentCts?.Cancel();
        if (DeployCommand.ExecutionTask is { } deployment) await deployment;
        if (CleanupCommand.ExecutionTask is { } cleanup) await cleanup;
    }

    [RelayCommand]
    private void CancelDeployment()
    {
        _deploymentCts?.Cancel();
        DeploymentMessage = "正在取消操作...";
    }

    /// <summary>点击图片（模组卡片图标/详情面板图标）：在主窗口顶层的覆盖层预览原图。</summary>
    public void ShowImagePreview(Bitmap? image, string? title) => _imagePreview?.Show(image, title);

    public Task ShowModImagePreviewAsync(Guid modId, string? title)
        => _imagePreview?.ShowOriginalAsync(() => _modService.ReadOriginalPreviewAsync(modId), title) ?? Task.CompletedTask;

    /// <summary>详情面板内联编辑名称/描述：就地更新列表项和搜索索引，不关闭详情。</summary>
    private async Task<bool> SaveModMetadataAsync(ModItemViewModel mod, string name, string description)
    {
        if (!_allMods.Contains(mod)) return false;
        if (string.IsNullOrWhiteSpace(name))
        {
            _notificationService.ShowWarning("模组名称不能为空", "编辑模组");
            return false;
        }
        try
        {
            var saved = await _modService.UpdateModMetadataAsync(mod.Id, name: name, description: description);
            mod.ApplyMetadata(saved);
            _searchService.UpdateModIndex(saved);
            await ApplyFiltersAsync();
            _notificationService.ShowSuccess("模组信息已更新", "编辑模组");
            return true;
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"保存失败: {ex.Message}", "编辑模组");
            return false;
        }
    }

    public async Task ChangeModImageAsync(ModItemViewModel mod, string imagePath)
    {
        if (mod.IsChangingImage || !_allMods.Contains(mod)) return;
        mod.IsChangingImage = true;
        try
        {
            var thumbnailPath = await _modService.SetCustomThumbnailAsync(mod.Id, imagePath);
            if (thumbnailPath == null)
            {
                _notificationService.ShowError("无法使用所选图片，请选择有效的 PNG、JPG 或 BMP 图片。", "更换图片");
                return;
            }
            await mod.ApplyThumbnailAsync(thumbnailPath);
            _notificationService.ShowSuccess("已更换模组图片", "更换图片");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"更换图片失败: {ex.Message}", "更换图片");
        }
        finally { mod.IsChangingImage = false; }
    }

    public async Task RestoreModImageAsync(ModItemViewModel mod)
    {
        if (mod.IsChangingImage || !_allMods.Contains(mod)) return;
        mod.IsChangingImage = true;
        try
        {
            var thumbnailPath = await _modService.GenerateThumbnailAsync(mod.Id);
            if (thumbnailPath == null)
            {
                _notificationService.ShowWarning("模组目录中没有可用图片，无法恢复默认图片。", "恢复默认图片");
                return;
            }
            await mod.ApplyThumbnailAsync(thumbnailPath);
            _notificationService.ShowSuccess("已恢复模组默认图片", "恢复默认图片");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"恢复默认图片失败: {ex.Message}", "恢复默认图片");
        }
        finally { mod.IsChangingImage = false; }
    }

    /// <summary>
    /// 显示模组详情
    /// </summary>
    [RelayCommand]
    private void ShowModDetail(ModItemViewModel mod)
    {
        if (LocalVersions.IsBusy) return;
        mod.CheckCompatibilityCommand = Compatibility == null ? null : new AsyncRelayCommand(() => Compatibility.OpenAsync([mod], mod.Name));
        mod.LocalVersions = LocalVersions;
        LocalVersions.Open(mod);
        _detailHashCts?.Cancel();
        if (SelectedModDetail != null) SelectedModDetail.IsCalculatingHash = false;
        SelectedModDetail = mod;
        IsDetailPanelOpen = true;

        // 设置详情面板的命令
        mod.ToggleEnabledCommand = new RelayCommand(async () => await ToggleModEnabledAsync(mod));
        mod.RemoveFromProfileCommand = new RelayCommand(async () => await RemoveModFromProfileAsync(mod));
        mod.DeleteCommand = new RelayCommand(async () => await DeleteModDetailAsync(mod));
        mod.CloseDetailPanelCommand = new RelayCommand(CloseDetailPanel);
        mod.CheckUpdatesCommand = ModOnline == null ? null : new AsyncRelayCommand(() => ModOnline.OpenAsync(mod));
        mod.SaveMetadataHandler = SaveModMetadataAsync;
        mod.IsEditingMetadata = false;
        if (string.IsNullOrWhiteSpace(mod.Hash))
        {
            var cancellation = new CancellationTokenSource();
            _detailHashCts = cancellation;
            _ = LoadDetailHashAsync(mod, cancellation);
        }
    }

    private async Task LoadDetailHashAsync(ModItemViewModel mod, CancellationTokenSource cancellation)
    {
        mod.HashError = null;
        mod.IsCalculatingHash = true;
        try
        {
            var hash = await _modService.EnsureModHashAsync(mod.Id, cancellation.Token);
            if (!cancellation.IsCancellationRequested) mod.SetHash(hash);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested) mod.HashError = $"计算失败：{ex.Message}";
        }
        finally
        {
            // 再次打开同一模组时，旧任务不能覆盖新任务的加载状态。
            if (ReferenceEquals(_detailHashCts, cancellation))
            {
                mod.IsCalculatingHash = false;
                _detailHashCts = null;
            }
            cancellation.Dispose();
        }
    }

    /// <summary>
    /// 关闭模组详情面板
    /// </summary>
    [RelayCommand]
    private void CloseDetailPanel()
    {
        LocalVersions.Cancel();
        _detailHashCts?.Cancel();
        IsDetailPanelOpen = false;
        SelectedModDetail = null;
    }

    /// <summary>
    /// 从详情面板删除模组
    /// </summary>
    private async Task DeleteModDetailAsync(ModItemViewModel mod)
    {
        await DeleteModAsync(mod);
        CloseDetailPanel();
    }

    [RelayCommand]
    private async Task RemoveModFromProfileAsync(ModItemViewModel mod)
    {
        if (_currentProfile == null || !mod.IsInCurrentProfile)
        {
            return;
        }

        if (!await _confirmationService.ConfirmAsync(
                "从当前 Profile 移除",
                $"将从“{_currentProfile.Name}”移除“{mod.Name}”，Library 中的 Mod 文件不会删除。是否继续？"))
        {
            return;
        }

        try
        {
            await _profileService.RemoveModFromProfileAsync(_currentProfile.Id, mod.Id);
            mod.IsInCurrentProfile = false;
            mod.IsEnabled = false;
            mod.Order = 0;
            await ApplyFiltersAsync();
            await UpdateStatisticsAsync();
            CloseDetailPanel();
            _notificationService.ShowSuccess($"已从当前 Profile 移除 {mod.Name}，Library 资产已保留", "从 Profile 移除");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"从 Profile 移除失败: {ex.Message}", "操作失败");
        }
    }

    /// <summary>
    /// 更新统计
    /// </summary>
    private async Task UpdateStatisticsAsync()
    {
        var statistics = await Task.Run(() =>
        {
            var total = _allMods.Count;
            var currentProfile = _allMods.Count(m => m.IsInCurrentProfile);
            var enabled = _allMods.Count(m => m.IsProfileEnabled);
            var disabled = _allMods.Count(m => m.IsProfileMemberDisabled);
            var deployed = _allMods.Count(m => m.IsDeployed);
            return (total, currentProfile, enabled, disabled, deployed, total - deployed);
        });

        TotalModCount = statistics.total;
        CurrentProfileCount = statistics.currentProfile;
        EnabledCount = statistics.enabled;
        DisabledCount = statistics.Item4;
        DeployedCount = statistics.deployed;
        NotDeployedCount = statistics.Item6;
    }

    /// <summary>
    /// 选择变化事件
    /// </summary>
    private void OnSelectedModsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_selectionSyncInProgress)
        {
            _selectionSyncInProgress = true;
            try
            {
                if (e.OldItems != null)
                {
                    foreach (ModItemViewModel mod in e.OldItems)
                    {
                        mod.IsSelected = false;
                    }
                }

                if (e.NewItems != null)
                {
                    foreach (ModItemViewModel mod in e.NewItems)
                    {
                        mod.IsSelected = true;
                    }
                }
            }
            finally
            {
                _selectionSyncInProgress = false;
            }
        }

        UpdateSelectionState();
    }

    public void SyncListSelection(
        IEnumerable<ModItemViewModel> addedItems,
        IEnumerable<ModItemViewModel> removedItems)
    {
        _selectionSyncInProgress = true;
        try
        {
            foreach (var mod in removedItems)
            {
                mod.IsSelected = false;
                SelectedMods.Remove(mod);
            }

            foreach (var mod in addedItems)
            {
                mod.IsSelected = true;
                if (!SelectedMods.Contains(mod))
                {
                    SelectedMods.Add(mod);
                }
            }
        }
        finally
        {
            _selectionSyncInProgress = false;
        }

        UpdateSelectionState();
    }

    private void AttachModSelectionHandlers()
    {
        foreach (var mod in _allMods)
        {
            mod.PropertyChanged += OnModPropertyChanged;
        }
    }

    private void DetachModSelectionHandlers()
    {
        foreach (var mod in _allMods)
        {
            mod.PropertyChanged -= OnModPropertyChanged;
        }
    }

    private void OnModPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_selectionSyncInProgress || e.PropertyName != nameof(ModItemViewModel.IsSelected) || sender is not ModItemViewModel mod)
        {
            return;
        }

        _selectionSyncInProgress = true;
        try
        {
            if (mod.IsSelected)
            {
                if (!SelectedMods.Contains(mod))
                {
                    SelectedMods.Add(mod);
                }
            }
            else
            {
                SelectedMods.Remove(mod);
            }
        }
        finally
        {
            _selectionSyncInProgress = false;
        }

        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        SelectedCount = SelectedMods.Count;
        IsAllSelected = FilteredMods.Count > 0 && FilteredMods.All(mod => mod.IsSelected);
        BatchDisableCommand.NotifyCanExecuteChanged();
        RemoveSelectedFromProfileCommand.NotifyCanExecuteChanged();
        BatchDeleteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 是否有选中项
    /// </summary>
    private bool HasSelection() => SelectedCount > 0;

    public event EventHandler? SearchFocusRequested;

    private void UpdateResultState()
    {
        HasFilteredResults = FilteredModCount > 0;
        HasNoResults = !_allMods.Any() || HasError || IsLoading
            ? false
            : FilteredModCount == 0;
        RefreshStateVisibilityProperties();
    }

    private void RefreshStateVisibilityProperties()
    {
        OnPropertyChanged(nameof(IsLibraryEmptyStateVisible));
        OnPropertyChanged(nameof(IsSearchEmptyStateVisible));
        OnPropertyChanged(nameof(IsFilterEmptyStateVisible));
        OnPropertyChanged(nameof(IsResultsVisible));
        OnPropertyChanged(nameof(IsLoadingStateVisible));
        OnPropertyChanged(nameof(IsErrorStateVisible));
    }

    partial void OnIsLoadingChanged(bool value) => RefreshStateVisibilityProperties();
    partial void OnHasErrorChanged(bool value) => RefreshStateVisibilityProperties();
    partial void OnIsEmptyChanged(bool value) => RefreshStateVisibilityProperties();
    partial void OnHasNoResultsChanged(bool value) => RefreshStateVisibilityProperties();
    partial void OnCurrentFilterChanged(ModFilterType value) => RefreshStateVisibilityProperties();

    partial void OnSelectedCategoryChanged(ulong? value)
    {
        RefreshStateVisibilityProperties();
        _ = ApplyFiltersAsync();
    }

    partial void OnCurrentSortModeChanged(ModSortMode value)
    {
        _ = ApplyFiltersAsync();
    }
}

/// <summary>
/// 模组筛选类型
/// </summary>
public enum ModFilterType
{
    All,
    CurrentProfile,
    Enabled,
    Disabled,
    Deployed,
    NotDeployed
}

/// <summary>
/// 模组排序模式
/// </summary>
public enum ModSortMode
{
    NameAscending,
    NameDescending,
    DateAddedNewest,
    DateAddedOldest,
    Category
}

/// <summary>排序/分类下拉的本地化选项（直接绑定枚举会显示英文名）。</summary>
public sealed record SortModeOption(string Display, ModSortMode Value);

public sealed record CategoryOption(string Display, ulong? Value);

/// <summary>加入配置文件弹窗中的配置文件选项。</summary>
public partial class ProfileChoiceViewModel : ObservableObject
{
    public Guid Id { get; }
    public string Name { get; }
    public int ModCount { get; }
    public bool IsActive { get; }

    [ObservableProperty]
    private bool _isSelected;

    public ProfileChoiceViewModel(Guid id, string name, int modCount, bool isActive)
    {
        Id = id;
        Name = name;
        ModCount = modCount;
        IsActive = isActive;
        IsSelected = isActive;
    }
}

/// <summary>模组右键菜单中一条插件命令的调用参数（目标模组 + 命令来源）。</summary>
public sealed record ModPluginInvocation(ModItemViewModel Mod, PluginCommandContribution Command);

/// <summary>插件命令执行完成后的展示内容，由视图弹出结果窗口。</summary>
public sealed class PluginCommandResultEventArgs : EventArgs
{
    public string Title { get; init; } = string.Empty;

    public string PluginName { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;
}

/// <summary>
/// 模组库工具栏上的一条插件入口：可能是命令，也可能只指向一个声明式浮窗。
/// </summary>
public sealed class LibraryPluginAction
{
    public string PluginId { get; init; } = string.Empty;

    public string PluginName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string? Icon { get; init; }

    public string? Command { get; init; }

    public string? Arguments { get; init; }

    public string? Dialog { get; init; }

    public string? RequiredPermission { get; init; }

    public bool IsPermissionGranted { get; init; } = true;

    public HD2Mimnt.Core.Interfaces.PluginUiBundle? Bundle { get; init; }

    public string IconKind => string.IsNullOrWhiteSpace(Icon) ? "Plugin" : Icon;
}
