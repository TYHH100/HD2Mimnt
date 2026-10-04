using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// 模组库 ViewModel
/// 实现搜索、筛选、排序、批量操作等功能
/// </summary>
public partial class LibraryViewModel : ObservableObject
{
    private readonly ModService _modService;
    private readonly SearchService _searchService;
    private readonly ProfileService _profileService;
    private readonly NotificationService _notificationService;
    private readonly DeploymentService _deploymentService;
    private readonly GameDetectionService _gameDetectionService;
    private readonly IConfirmationService _confirmationService;

    // 数据源
    private List<ModItemViewModel> _allMods = new();
    private bool _selectionSyncInProgress;

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
    private int _filterGeneration;
    private int _searchGeneration;
    private const int SearchDebounceMs = 300;

    // 筛选
    [ObservableProperty]
    private ModFilterType _currentFilter = ModFilterType.All;

    [ObservableProperty]
    private ModCategory? _selectedCategory;

    [ObservableProperty]
    private ObservableCollection<ModCategory> _availableCategories = new();

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
    private string _deploymentMessage = string.Empty;

    private CancellationTokenSource? _deploymentCts;

    public IEnumerable<DeploymentMethod> DeploymentMethods => Enum.GetValues<DeploymentMethod>();
    public IEnumerable<ModSortMode> SortModes => Enum.GetValues<ModSortMode>();
    public string ModItemPadding => ListDensity == "Compact" ? "16,6" : "16,12";
    public bool IsLibraryEmptyStateVisible => !IsLoading && !HasError && IsEmpty;
    public bool IsSearchEmptyStateVisible => !IsLoading && !HasError && HasNoResults && !string.IsNullOrWhiteSpace(SearchText);
    public bool IsFilterEmptyStateVisible => !IsLoading && !HasError && HasNoResults && string.IsNullOrWhiteSpace(SearchText) && (CurrentFilter != ModFilterType.All || SelectedCategory.HasValue);
    public bool IsResultsVisible => !IsLoading && !HasError && HasFilteredResults;
    public bool IsLoadingStateVisible => IsLoading;
    public bool IsErrorStateVisible => HasError && !IsLoading;

    partial void OnListDensityChanged(string value) => OnPropertyChanged(nameof(ModItemPadding));

    public void ApplyListDensity(string value)
    {
        ListDensity = value is "Compact" or "Comfortable" ? value : "Comfortable";
    }

    public void ApplyLibraryDefaults(ModSortMode sortMode, ModFilterType filterType)
    {
        CurrentSortMode = sortMode;
        CurrentFilter = filterType;
    }

    public LibraryViewModel(
        ModService modService,
        SearchService searchService,
        ProfileService profileService,
        NotificationService notificationService,
        DeploymentService deploymentService,
        GameDetectionService gameDetectionService,
        IConfirmationService confirmationService)
    {
        _modService = modService;
        _searchService = searchService;
        _profileService = profileService;
        _notificationService = notificationService;
        _deploymentService = deploymentService;
        _gameDetectionService = gameDetectionService;
        _confirmationService = confirmationService;

        // 监听选择变化
        SelectedMods.CollectionChanged += OnSelectedModsChanged;

        // 初始化可用分类
        AvailableCategories = new ObservableCollection<ModCategory>(
            Enum.GetValues<ModCategory>());
    }

    public async Task InitializeAsync()
    {
        await LoadModsAsync();
        await _searchService.BuildIndexAsync();
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
            _allMods = await Task.Run(() => modsList.Select(m => new ModItemViewModel(m)).ToList());
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
        // 只加载可见的模组图标
        foreach (var mod in FilteredMods.Take(50))
        {
            await mod.LoadIconAsync();
        }
    }

    /// <summary>
    /// 搜索文本变化时触发
    /// </summary>
    partial void OnSearchTextChanged(string value)
    {
        // 取消之前的搜索
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();

        // 防抖执行搜索
        var token = _searchCts.Token;
        _ = DebounceSearchAsync(token);
    }

    private async Task DebounceSearchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SearchDebounceMs, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                await SearchAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 新的输入会取消旧搜索。
        }
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
            var results = await _searchService.SearchAsync(searchText, cancellationToken);
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
    private async Task ClearSearchAsync()
    {
        SearchText = string.Empty;
        await ApplyFiltersAsync();
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
    private async Task FilterByCategoryAsync(ModCategory? category)
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
        var filtered = await Task.Run(() =>
        {
            var filtered = _allMods.AsEnumerable();

            // 应用搜索（如果有）
            if (!string.IsNullOrWhiteSpace(searchText))
            {
                var query = searchText.ToLowerInvariant();
                filtered = filtered.Where(m =>
                    m.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    m.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    m.Author.Contains(query, StringComparison.OrdinalIgnoreCase));
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
        ModCategory? category)
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
        if (category.HasValue && category.Value != ModCategory.Unknown)
        {
            mods = mods.Where(m => m.Category == category.Value);
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
            ModSortMode.Category => mods.OrderBy(m => m.Category).ThenBy(m => m.Name),
            _ => mods
        };
    }

    /// <summary>
    /// 切换当前筛选结果的全选状态。
    /// </summary>
    [RelayCommand]
    private void SelectAll()
    {
        if (IsAllSelected)
        {
            _selectionSyncInProgress = true;
            try
            {
                foreach (var mod in FilteredMods)
                {
                    mod.IsSelected = false;
                }

                foreach (var mod in FilteredMods)
                {
                    SelectedMods.Remove(mod);
                }
            }
            finally
            {
                _selectionSyncInProgress = false;
            }

            UpdateSelectionState();
            return;
        }

        _selectionSyncInProgress = true;
        try
        {
            foreach (var mod in FilteredMods)
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

    /// <summary>
    /// 反选当前筛选结果，筛选之外的模组保持原状态。
    /// </summary>
    [RelayCommand]
    private void InvertSelection()
    {
        _selectionSyncInProgress = true;
        try
        {
            foreach (var mod in FilteredMods)
            {
                mod.IsSelected = !mod.IsSelected;
            }

            var filteredSet = FilteredMods.ToHashSet();
            foreach (var selected in SelectedMods.Where(filteredSet.Contains).ToArray())
            {
                SelectedMods.Remove(selected);
            }

            foreach (var mod in FilteredMods.Where(static mod => mod.IsSelected))
            {
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
    /// 清空选择
    /// </summary>
    [RelayCommand]
    private void ClearSelection()
    {
        _selectionSyncInProgress = true;
        try
        {
            foreach (var mod in SelectedMods.ToArray())
            {
                mod.IsSelected = false;
            }
            SelectedMods.Clear();
        }
        finally
        {
            _selectionSyncInProgress = false;
        }

        UpdateSelectionState();
    }

    /// <summary>
    /// 批量启用
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task BatchEnableAsync()
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
            await _profileService.SetModsEnabledAsync(_currentProfile.Id, selected.Select(mod => mod.Id).ToArray(), true);
            foreach (var mod in selected)
            {
                mod.IsInCurrentProfile = true;
                mod.IsEnabled = true;
            }

            await UpdateStatisticsAsync();
            _notificationService.ShowSuccess($"已启用 {SelectedCount} 个模组", "批量启用");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"批量启用失败: {ex.Message}", "操作失败");
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
                await _deploymentService.DeleteModDeploymentRecordsAsync(mod.Id);
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
            await _deploymentService.DeleteModDeploymentRecordsAsync(mod.Id);
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
    /// 导入模组
    /// </summary>
    [RelayCommand]
    private async Task ImportModAsync()
    {
        _notificationService.ShowInfo("请使用文件选择器选择模组文件", "导入模组");
    }

    public async Task ImportPathsAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        var sourcePaths = paths.Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sourcePaths.Count == 0) return;

        IsLoading = true;
        try
        {
            foreach (var path in sourcePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _modService.ImportModAsync(path, cancellationToken);
            }

            await LoadModsAsync();
            _notificationService.ShowSuccess($"已导入 {sourcePaths.Count} 个模组", "导入完成");
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
    private async Task DeployAsync()
    {
        if (IsDeploying) return;
        if (_currentProfile == null)
        {
            _notificationService.ShowWithNavigation(NotificationLevel.Warning, "请先选择配置文件", "打开配置文件", NotificationNavigationTarget.Profiles, "部署模组");
            return;
        }

        var gamePath = _gameDetectionService.CurrentGameInfo?.Path;
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            _notificationService.ShowWithNavigation(NotificationLevel.Warning, "未检测到有效的游戏目录", "打开设置", NotificationNavigationTarget.Settings, "部署模组");
            return;
        }

        if (!await _confirmationService.ConfirmAsync(
                "部署模组",
                $"将清理之前的部署并部署当前配置中的启用模组。部署方式：{CurrentDeploymentMethod}。是否继续？"))
        {
            return;
        }

        IsDeploying = true;
        _deploymentCts = new CancellationTokenSource();
        DeploymentMessage = "正在准备部署...";
        try
        {
            var progress = new Progress<DeploymentProgress>(p => DeploymentMessage = p.Message);
            var result = await _deploymentService.DeployProfileAsync(
                _currentProfile.Id, CurrentDeploymentMethod, DeploymentOrder.TopToBottom, gamePath, progress, _deploymentCts.Token);
            if (result.Status == DeploymentStatus.Completed)
            {
                _notificationService.ShowSuccess($"已部署 {result.CompletedMods} 个模组", "部署完成");
            }
            else
            {
                _notificationService.ShowWarning(result.ErrorMessage ?? result.Message ?? "部署未完全成功", "部署结果");
            }
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
            IsDeploying = false;
            DeploymentMessage = string.Empty;
            _deploymentCts.Dispose();
            _deploymentCts = null;
        }
    }

    [RelayCommand]
    private async Task CleanupAsync()
    {
        if (IsDeploying) return;
        if (!await _confirmationService.ConfirmAsync(
                "清理部署",
                "将删除最近一次部署产生的目标文件和部署记录。是否继续？"))
        {
            return;
        }

        IsDeploying = true;
        _deploymentCts = new CancellationTokenSource();
        DeploymentMessage = "正在清理部署文件...";
        try
        {
            var result = await _deploymentService.PreDeploymentCleanupAsync(
                new Progress<string>(message => DeploymentMessage = message), _deploymentCts.Token);
            _notificationService.ShowSuccess($"已清理 {result.FilesDeleted} 个文件", "清理完成");
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
            IsDeploying = false;
            DeploymentMessage = string.Empty;
            _deploymentCts.Dispose();
            _deploymentCts = null;
        }
    }

    [RelayCommand]
    private void CancelDeployment()
    {
        _deploymentCts?.Cancel();
        DeploymentMessage = "正在取消操作...";
    }

    /// <summary>
    /// 显示模组详情
    /// </summary>
    [RelayCommand]
    private void ShowModDetail(ModItemViewModel mod)
    {
        SelectedModDetail = mod;
        IsDetailPanelOpen = true;

        // 设置详情面板的命令
        mod.ToggleEnabledCommand = new RelayCommand(async () => await ToggleModEnabledAsync(mod));
        mod.RemoveFromProfileCommand = new RelayCommand(async () => await RemoveModFromProfileAsync(mod));
        mod.DeleteCommand = new RelayCommand(async () => await DeleteModDetailAsync(mod));
        mod.CloseDetailPanelCommand = new RelayCommand(CloseDetailPanel);
    }

    /// <summary>
    /// 关闭模组详情面板
    /// </summary>
    [RelayCommand]
    private void CloseDetailPanel()
    {
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
        BatchEnableCommand.NotifyCanExecuteChanged();
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

    partial void OnSelectedCategoryChanged(ModCategory? value)
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
