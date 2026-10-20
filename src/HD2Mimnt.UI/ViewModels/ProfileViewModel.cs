using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Search;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

public partial class ProfileViewModel : ObservableObject
{
    private readonly ProfileService _profileService;
    private readonly ModService _modService;
    private readonly NotificationService _notificationService;
    private readonly IConfirmationService _confirmationService;
    private readonly SettingsService _settingsService;
    private readonly ImagePreviewService? _imagePreview;

    public event EventHandler? ActiveProfileChanged;
    public event EventHandler<ProfileModViewModel>? ConflictModLocationRequested;

    [ObservableProperty]
    private bool _isConflictPickerOpen;

    [ObservableProperty]
    private ObservableCollection<ProfileModViewModel> _conflictNavigationChoices = new();

    [RelayCommand]
    private async Task OpenConflictNavigationAsync(ProfileModViewModel? source)
    {
        if (source == null || !ProfileMods.Contains(source) || !source.HasConflict) return;
        var ids = source.ConflictTargets.Select(target => target.ModId).ToHashSet();
        ConflictNavigationChoices = new(ProfileMods.Where(mod => ids.Contains(mod.ModId)));
        if (ConflictNavigationChoices.Count == 1)
            await LocateConflictModAsync(ConflictNavigationChoices[0]);
        else IsConflictPickerOpen = ConflictNavigationChoices.Count > 1;
    }

    [RelayCommand]
    private void CloseConflictPicker()
    {
        IsConflictPickerOpen = false;
        ConflictNavigationChoices = new();
    }

    [RelayCommand]
    private async Task LocateConflictModAsync(ProfileModViewModel? target)
    {
        if (target == null || !ConflictNavigationChoices.Contains(target) || !ProfileMods.Contains(target)
            || SelectedProfile is not { } profile) return;
        CloseConflictPicker();
        ModSearchText = string.Empty;
        var group = target.IsFixedGroupMember ? _luaSeparator
            : ProfileSeparators.FirstOrDefault(separator => GetSeparatorMembers(separator).Contains(target));
        if (group?.IsCollapsed == true) await ToggleSeparatorAsync(group);
        if (SelectedProfile != profile || !ProfileMods.Contains(target) || !VisibleProfileMods.Contains(target)) return;
        ClearModSelection();
        if (target.CanSelect) FocusMod(target);
        target.IsConflictLocation = true;
        ConflictModLocationRequested?.Invoke(this, target);
    }

    /// <summary>按模组 ID 回填当前扫描，不持久化临时分析结果。</summary>
    public void ApplyConflictResults(IEnumerable<ConflictGroupViewModel> groups)
        => ApplyConflictIndex(ConflictCardIndex.Create(groups));

    public void ApplyConflictIndex(ConflictCardIndex index)
    {
        CloseConflictPicker();
        foreach (var mod in ProfileMods)
            mod.ApplyConflictStatus(mod.Enabled && index.Contains(mod.ModId), index);
    }

    [ObservableProperty]
    private ObservableCollection<ProfileItemViewModel> _profiles = new();

    /// <summary>顶部搜索框：按名称/版本过滤当前 Profile 的模组卡片。</summary>
    [ObservableProperty]
    private string _modSearchText = string.Empty;

    [ObservableProperty]
    private string _newProfileName = string.Empty;

    [ObservableProperty]
    private ProfileItemViewModel? _selectedProfile;

    /// <summary>当前激活的 Profile（页头下拉选择器，选择即切换激活）。</summary>
    [ObservableProperty]
    private ProfileItemViewModel? _activeProfileSelection;

    private bool _suppressActiveProfileSelection;

    /// <summary>本页自身发起的 ProfileService 变更不需要经 ProfilesChanged 事件重载（本地状态已同步）。</summary>
    private bool _suppressProfilesChangedRefresh;

    private readonly ModSelection<ProfileModViewModel> _modSelection;
    private readonly HashSet<ProfileModViewModel> _selectionTrackedMods = new();

    [ObservableProperty]
    private ProfileModViewModel? _focusedMod;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedMods))]
    private int _selectedModCount;

    public bool HasSelectedMods => SelectedModCount > 0;

    [ObservableProperty]
    private bool _isBatchModOperationRunning;

    private bool CanRunSelectedModAction() => HasSelectedProfile && HasSelectedMods
        && !IsProfileModsLoading && !IsBatchModOperationRunning;

    partial void OnIsBatchModOperationRunningChanged(bool value) => UpdateModSelectionState();

    partial void OnProfileModsChanged(ObservableCollection<ProfileModViewModel>? oldValue,
        ObservableCollection<ProfileModViewModel> newValue)
    {
        if (oldValue != null) oldValue.CollectionChanged -= OnProfileModsCollectionChanged;
        newValue.CollectionChanged += OnProfileModsCollectionChanged;
        RefreshModSelectionSubscriptions();
    }

    private void OnProfileModsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RefreshModSelectionSubscriptions();

    private void RefreshModSelectionSubscriptions()
    {
        foreach (var mod in _selectionTrackedMods.Except(ProfileMods).ToArray())
        {
            mod.PropertyChanged -= OnModSelectionChanged;
            _selectionTrackedMods.Remove(mod);
        }
        foreach (var mod in ProfileMods.Except(_selectionTrackedMods))
        {
            mod.PropertyChanged += OnModSelectionChanged;
            _selectionTrackedMods.Add(mod);
        }
        _modSelection.RemoveMissingAnchor(ProfileMods);
        if (FocusedMod != null && !ProfileMods.Contains(FocusedMod)) FocusedMod = null;
        UpdateModSelectionState();
    }

    private void OnModSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileModViewModel.IsSelected)) UpdateModSelectionState();
    }

    private void UpdateModSelectionState()
    {
        SelectedModCount = ProfileMods.Count(mod => mod.CanSelect && mod.IsSelected);
        BatchEnableModsCommand.NotifyCanExecuteChanged();
        BatchDisableModsCommand.NotifyCanExecuteChanged();
        RemoveSelectedModsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearModSelection()
    {
        _modSelection.Clear();
        foreach (var mod in ProfileMods) mod.IsConflictLocation = false;
    }

    [RelayCommand]
    private void ToggleSelectAllMods() => _modSelection.ToggleAll();

    [RelayCommand]
    private void InvertModSelection() => _modSelection.Invert();

    [RelayCommand(CanExecute = nameof(CanRunSelectedModAction))]
    private Task BatchEnableModsAsync() => SetSelectedModsEnabledAsync(true);

    [RelayCommand(CanExecute = nameof(CanRunSelectedModAction))]
    private Task BatchDisableModsAsync() => SetSelectedModsEnabledAsync(false);

    private async Task SetSelectedModsEnabledAsync(bool enabled)
    {
        if (!CanRunSelectedModAction() || SelectedProfile is not { } profile) return;
        var selected = ProfileMods.Where(mod => mod.CanSelect && mod.IsSelected).ToArray();
        IsBatchModOperationRunning = true;
        try
        {
            await SuppressLocalChangeAsync(() => _profileService.SetModsEnabledAsync(profile.Id,
                selected.Select(mod => mod.ModId).ToArray(), enabled));
            var loaderStates = enabled && selected.Any(mod => mod.IsBslLoader)
                ? (await _profileService.GetProfileByIdAsync(profile.Id))?.Mods.ToDictionary(mod => mod.ModId, mod => mod.Enabled)
                : null;
            if (ReferenceEquals(SelectedProfile, profile))
            {
                foreach (var mod in selected) mod.Enabled = enabled;
                if (loaderStates != null)
                    foreach (var mod in ProfileMods)
                        if (loaderStates.TryGetValue(mod.ModId, out var savedEnabled)) mod.Enabled = savedEnabled;
            }
        }
        catch (Exception ex) { _notificationService.ShowError($"批量更新模组状态失败: {ex.Message}", "配置文件"); }
        finally { IsBatchModOperationRunning = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRunSelectedModAction))]
    private async Task RemoveSelectedModsAsync()
    {
        if (!CanRunSelectedModAction() || SelectedProfile is not { } profile) return;
        var selected = ProfileMods.Where(mod => mod.CanSelect && mod.IsSelected).ToArray();
        IsBatchModOperationRunning = true;
        try
        {
            await SuppressLocalChangeAsync(() => _profileService.RemoveModsFromProfileAsync(profile.Id,
                selected.Select(mod => mod.ModId).ToArray()));
            profile.ModCount = Math.Max(0, profile.ModCount - selected.Length);
            if (ReferenceEquals(SelectedProfile, profile))
            {
                AdjustSeparatorPositionsAfterRemoval(selected);
                foreach (var mod in selected) ProfileMods.Remove(mod);
                RenumberProfileMods();
                ApplyModFilter();
                IsProfileModsEmpty = ProfileMods.Count == 0 && ProfileSeparators.Count == 0;
            }
        }
        catch (Exception ex) { _notificationService.ShowError($"批量移除模组失败: {ex.Message}", "配置文件"); }
        finally { IsBatchModOperationRunning = false; }
    }

    public void SetModSelectionAnchor(ProfileModViewModel mod)
    {
        _modSelection.SetAnchor(mod);
    }

    public void FocusMod(ProfileModViewModel mod)
    {
        if (_modSelection.Focus(mod)) FocusedMod = mod;
    }

    public void ToggleModSelected(ProfileModViewModel mod)
    {
        if (_modSelection.Toggle(mod)) FocusedMod = mod;
    }

    public void SelectAllVisibleMods() => _modSelection.SelectAll(FocusedMod);

    public void SelectModRange(ProfileModViewModel mod, bool additive = false)
    {
        if (_modSelection.SelectRange(mod, additive)) FocusedMod = mod;
    }

    private static void ApplyModSelection(IEnumerable<ProfileModViewModel> added, IEnumerable<ProfileModViewModel> removed)
    {
        foreach (var mod in removed) mod.IsSelected = false;
        foreach (var mod in added) mod.IsSelected = true;
    }

    [ObservableProperty]
    private ObservableCollection<ProfileModViewModel> _profileMods = new();

    /// <summary>搜索过滤后的模组卡片列表，与 ProfileMods 共享同一组实例。</summary>
    [ObservableProperty]
    private ObservableCollection<ProfileModViewModel> _visibleProfileMods = new();

    [ObservableProperty]
    private ObservableCollection<ProfileModViewModel> _profileRows = new();

    [ObservableProperty]
    private ObservableCollection<ProfileModViewModel> _profileSeparators = new();

    [ObservableProperty]
    private string _separatorName = string.Empty;

    [ObservableProperty]
    private Color _separatorSelectedColor = Colors.CornflowerBlue;

    [ObservableProperty]
    private bool _useDefaultSeparatorColor = true;

    [ObservableProperty]
    private bool _isSeparatorNameEditable = true;

    [ObservableProperty]
    private bool _isSeparatorDialogOpen;

    [ObservableProperty]
    private string _separatorDialogTitle = string.Empty;

    [ObservableProperty]
    private string _separatorError = string.Empty;

    private Guid? _editingSeparatorId;
    private int _newSeparatorOrder;
    private Guid _separatorProfileId;

    private readonly ProfileModViewModel _luaSeparator = new()
    {
        IsSeparator = true,
        ModName = "依赖加载器的模组与 BSL / MDL",
        ModVersion = "依赖加载器的模组 → BSL / MDL（二选一）→ SmoothBoot（最高优先级）· 按部署方向排列"
    };

    private bool _bottomToTop;
    public event EventHandler? DeploymentDirectionChanged;

    [ObservableProperty]
    private bool _hasNoModResults;

    [ObservableProperty]
    private bool _isProfileModsEmpty = true;

    [ObservableProperty]
    private bool _hasProfileModsError;

    [ObservableProperty]
    private string _profileModsErrorMessage = string.Empty;

    [ObservableProperty]
    private ObservableCollection<LibraryModChoiceViewModel> _availableLibraryMods = new();

    [ObservableProperty]
    private string _addModSearchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoAddModSearchResults))]
    private ObservableCollection<LibraryModChoiceViewModel> _visibleAvailableLibraryMods = new();

    public bool HasNoAddModSearchResults => !HasNoAvailableMods && VisibleAvailableLibraryMods.Count == 0;

    partial void OnAddModSearchTextChanged(string value) => ApplyAddModFilter();

    private void ApplyAddModFilter()
    {
        var query = AddModSearchText.Trim();
        var options = SearchMatchOptions.FromSettings(_settingsService);
        VisibleAvailableLibraryMods = new(AvailableLibraryMods.Where(mod => query.Length == 0
            || SearchMatcher.MatchAny(query, options, mod.Name, mod.Author, mod.Version) != SearchMatchKind.None));
        OnPropertyChanged(nameof(HasNoAvailableMods));
    }

    [RelayCommand]
    private void ClearAddModSearch() => AddModSearchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isProfileModsLoading;

    [ObservableProperty]
    private bool _isAddModOpen;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _isCompareOpen;

    [ObservableProperty]
    private ProfileItemViewModel? _compareLeftProfile;

    [ObservableProperty]
    private ProfileItemViewModel? _compareRightProfile;

    [ObservableProperty]
    private ObservableCollection<ProfileCompareItemViewModel> _compareResults = new();

    [ObservableProperty]
    private string _compareFilter = "全部";

    private CancellationTokenSource? _compareCts;
    private CancellationTokenSource? _profileModsCts;

    public IEnumerable<string> CompareFilterOptions { get; } = new[] { "全部", "仅左侧", "仅右侧", "状态不同", "两边共有" };

    public bool HasSelectedProfile => SelectedProfile != null;
    public bool HasNoSelectedProfile => SelectedProfile == null;
    public bool IsProfileModsContentVisible => HasSelectedProfile && !IsProfileModsLoading && !HasProfileModsError && !HasNoModResults;
    public bool IsProfileModsEmptyStateVisible => HasSelectedProfile && !IsProfileModsLoading && IsProfileModsEmpty && !HasProfileModsError;
    public bool IsProfileModsLoadingStateVisible => HasSelectedProfile && IsProfileModsLoading;
    public bool IsProfileModsErrorStateVisible => HasSelectedProfile && !IsProfileModsLoading && HasProfileModsError;
    public bool HasNoAvailableMods => AvailableLibraryMods.Count == 0;

    public ProfileViewModel(ProfileService profileService, ModService modService, NotificationService notificationService, IConfirmationService confirmationService, SettingsService settingsService, ImagePreviewService? imagePreview = null, DeploymentService? deploymentService = null, ModCompatibilityViewModel? compatibility = null)
    {
        Compatibility = compatibility;
        LocalVersions = new(modService, confirmationService, async () =>
        {
            await RefreshAfterProfilesChangedAsync();
            if (ModContentChanged != null) await ModContentChanged();
        });
        _profileService = profileService;
        _modService = modService;
        _notificationService = notificationService;
        _confirmationService = confirmationService;
        _settingsService = settingsService;
        _imagePreview = imagePreview;
        _deploymentService = deploymentService;
        _modSelection = new(() => VisibleProfileMods, () => ProfileMods.Where(mod => mod.IsSelected),
            ApplyModSelection, (mod, focused) => mod.IsFocused = focused, mod => mod.CanSelect);
        _profileMods.CollectionChanged += OnProfileModsCollectionChanged;
        _settingsService.SettingChanged += OnDeploymentDirectionChanged;
    }

    private void OnDeploymentDirectionChanged(object? sender, SettingChangedEventArgs e)
    {
        if (e.Key is SettingKeys.SearchFuzzy or SettingKeys.SearchPinyin)
        {
            void ApplySearch()
            {
                ApplyModFilter();
                ApplyAddModFilter();
            }
            if (Avalonia.Application.Current == null || Dispatcher.UIThread.CheckAccess()) ApplySearch();
            else Dispatcher.UIThread.Post(ApplySearch);
            return;
        }
        if (e.Key == SettingKeys.ProfilesLockLoaders)
        {
            void Reload()
            {
                if (SelectedProfile != null) _ = LoadProfileModsAsync(SelectedProfile.Id);
                DeploymentDirectionChanged?.Invoke(this, EventArgs.Empty);
            }
            if (Avalonia.Application.Current == null || Dispatcher.UIThread.CheckAccess()) Reload();
            else Dispatcher.UIThread.Post(Reload);
            return;
        }
        if (e.Key != SettingKeys.DefaultDeploymentOrder) return;
        void Apply()
        {
            _bottomToTop = e.NewValue?.ToString() == DeploymentOrder.BottomToTop.ToString();
            _luaSeparator.ChevronAngle = _bottomToTop ? -90 : 90;
            ArrangeBslDisplay();
            RenumberProfileMods();
            ApplyModFilter();
            DeploymentDirectionChanged?.Invoke(this, EventArgs.Empty);
        }
        if (Avalonia.Application.Current == null || Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    private void ArrangeBslDisplay()
    {
        if (!_settingsService.GetSetting(SettingKeys.ProfilesLockLoaders, true))
        {
            ProfileMods = new ObservableCollection<ProfileModViewModel>(ProfileMods.OrderBy(mod => mod.Order));
            return;
        }
        var normal = ProfileMods.Where(mod => !mod.IsFixedGroupMember).ToList();
        var dependencies = ProfileMods.Where(mod => mod.BslRole == BslModRole.Dependency).OrderBy(mod => mod.Order).ToList();
        var loaders = ProfileMods.Where(mod => mod.IsBslLoader).OrderBy(mod => mod.Order).ToList();
        var smoothBoot = ProfileMods.Where(mod => mod.BslRole == BslModRole.SmoothBoot).OrderBy(mod => mod.Order).ToList();
        ProfileMods = new ObservableCollection<ProfileModViewModel>(_bottomToTop
            ? smoothBoot.AsEnumerable().Reverse().Concat(loaders.AsEnumerable().Reverse()).Concat(dependencies.AsEnumerable().Reverse()).Concat(normal)
            : normal.Concat(dependencies).Concat(loaders).Concat(smoothBoot));
    }

    [RelayCommand]
    public async Task LoadProfilesAsync()
    {
        IsLoading = true;
        HasError = false;
        ErrorMessage = string.Empty;
        try
        {
            var profiles = await _profileService.GetAllProfilesAsync();
            Profiles.Clear();
            foreach (var profile in profiles)
            {
                Profiles.Add(new ProfileItemViewModel(profile) { Owner = this });
            }

            // 选中激活的 Profile
            var activeProfile = Profiles.FirstOrDefault(p => p.IsActive);
            if (activeProfile != null)
            {
                SelectedProfile = activeProfile;
                _suppressActiveProfileSelection = true;
                ActiveProfileSelection = activeProfile;
                _suppressActiveProfileSelection = false;
                await LoadProfileModsAsync(activeProfile.Id);
            }
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"加载配置文件失败: {ex.Message}";
            _notificationService.ShowError(ErrorMessage, "配置文件");
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnModSearchTextChanged(string value) => ApplyModFilter();

    /// <summary>包一层抑制标记：服务端事件触发时不再重复重载本页。</summary>
    private async Task SuppressLocalChangeAsync(Func<Task> mutation)
    {
        _suppressProfilesChangedRefresh = true;
        try
        {
            await mutation();
        }
        finally
        {
            _suppressProfilesChangedRefresh = false;
        }
    }

    /// <summary>
    /// ProfileService.ProfilesChanged 事件联动：就地刷新配置文件列表（名称/激活态/模组数/顺序）
    /// 并重载当前查看 Profile 的模组列表，不改变用户正在查看的选中项。
    /// </summary>
    public async Task RefreshAfterProfilesChangedAsync()
    {
        if (_suppressProfilesChangedRefresh) return;

        try
        {
            var latest = (await _profileService.GetAllProfilesAsync()).ToList();

            foreach (var stale in Profiles.Where(p => latest.All(l => l.Id != p.Id)).ToArray())
            {
                Profiles.Remove(stale);
                if (ReferenceEquals(SelectedProfile, stale))
                {
                    SelectedProfile = Profiles.FirstOrDefault(item => item.IsActive) ?? Profiles.FirstOrDefault();
                }
            }

            foreach (var profile in latest)
            {
                var vm = Profiles.FirstOrDefault(p => p.Id == profile.Id);
                if (vm == null)
                {
                    Profiles.Add(new ProfileItemViewModel(profile) { Owner = this });
                }
                else
                {
                    vm.Name = profile.Name;
                    vm.Description = profile.Description;
                    vm.IsActive = profile.IsActive;
                    vm.ModCount = profile.Mods.Count;
                }
            }

            // 与服务端顺序对齐（第一位仍是默认配置）
            for (var target = 0; target < latest.Count; target++)
            {
                var vm = Profiles.FirstOrDefault(p => p.Id == latest[target].Id);
                if (vm == null) continue;
                var current = Profiles.IndexOf(vm);
                if (current != target) Profiles.Move(current, target);
            }

            // 激活态变化时同步页头选择器（不触发选中即切换）
            var active = Profiles.FirstOrDefault(p => p.IsActive);
            if (active != null && !ReferenceEquals(ActiveProfileSelection, active))
            {
                _suppressActiveProfileSelection = true;
                ActiveProfileSelection = active;
                _suppressActiveProfileSelection = false;
            }

            OnPropertyChanged(nameof(HasSelectedProfile));
            OnPropertyChanged(nameof(HasNoSelectedProfile));

            if (SelectedProfile != null)
            {
                await LoadProfileModsAsync(SelectedProfile.Id);
            }
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"同步配置文件变更失败: {ex.Message}", "配置文件");
        }
    }

    partial void OnActiveProfileSelectionChanged(ProfileItemViewModel? value)
    {
        if (_suppressActiveProfileSelection || value == null || value.IsActive) return;
        _ = SetActiveProfileAsync(value);
    }

    partial void OnIsProfileModsLoadingChanged(bool value)
    {
        RefreshProfileModStateProperties();
        UpdateModSelectionState();
    }

    partial void OnIsProfileModsEmptyChanged(bool value) => RefreshProfileModStateProperties();

    partial void OnHasProfileModsErrorChanged(bool value) => RefreshProfileModStateProperties();

    partial void OnHasNoModResultsChanged(bool value) => RefreshProfileModStateProperties();

    partial void OnAvailableLibraryModsChanged(ObservableCollection<LibraryModChoiceViewModel> value)
        => ApplyAddModFilter();

    partial void OnCompareRightProfileChanged(ProfileItemViewModel? value)
    {
        if (IsCompareOpen) _ = LoadCompareAsync();
    }

    partial void OnCompareLeftProfileChanged(ProfileItemViewModel? value)
    {
        if (IsCompareOpen) _ = LoadCompareAsync();
    }

    partial void OnCompareFilterChanged(string value) => ApplyCompareFilter();

    [RelayCommand]
    private void ClearModSearch() => ModSearchText = string.Empty;

    private void ApplyModFilter()
    {
        var query = ModSearchText.Trim();
        var options = SearchMatchOptions.FromSettings(_settingsService);
        bool Matches(ProfileModViewModel row) =>
            SearchMatcher.MatchAny(query, options, row.ModName, row.ModVersion) != SearchMatchKind.None;
        var ordinary = BuildOrdinaryRows();
        var visibleOrdinary = new List<ProfileModViewModel>();
        var group = (ProfileModViewModel?)null;
        var groupMatches = false;
        var remainingMembers = 0;
        foreach (var row in ordinary)
        {
            if (row.IsUserSeparator)
            {
                group = row;
                groupMatches = Matches(row);
                var members = GetSeparatorMembers(row);
                row.GroupModCount = members.Count;
                remainingMembers = members.Count;
                if (query.Length == 0 || groupMatches || members.Any(Matches)) visibleOrdinary.Add(row);
            }
            // 搜索时显示匹配成员，避免折叠组藏住搜索结果；不改变已保存的折叠状态。
            else
            {
                if (remainingMembers == 0) { group = null; groupMatches = false; }
                if (query.Length > 0 ? groupMatches || Matches(row) : group?.IsCollapsed != true)
                    visibleOrdinary.Add(row);
                if (remainingMembers > 0) remainingMembers--;
            }
        }
        var separator = _luaSeparator;
        separator.GroupModCount = ProfileMods.Count(mod => mod.IsFixedGroupMember);
        var fixedRows = ProfileMods.Where(mod => mod.IsFixedGroupMember
            && (query.Length > 0 ? Matches(separator) || Matches(mod) : !separator.IsCollapsed)).ToList();
        var rows = new List<ProfileModViewModel>();
        if (_bottomToTop)
        {
            rows.AddRange(fixedRows);
            if (separator.GroupModCount > 0) rows.Add(separator);
            rows.AddRange(visibleOrdinary);
        }
        else
        {
            rows.AddRange(visibleOrdinary);
            if (separator.GroupModCount > 0) rows.Add(separator);
            rows.AddRange(fixedRows);
        }
        VisibleProfileMods = new ObservableCollection<ProfileModViewModel>(rows.Where(row => !row.IsSeparator));
        ProfileRows = new ObservableCollection<ProfileModViewModel>(rows);
        HasNoModResults = query.Length > 0 && rows.Count == 0 && (ProfileMods.Count > 0 || ProfileSeparators.Count > 0);
    }

    private int GetLuaGroupBoundary(IEnumerable<ProfileModViewModel> mods)
        => mods.TakeWhile(mod => mod.IsFixedGroupMember == _bottomToTop).Count();

    [RelayCommand]
    private void OpenAddSeparator(ProfileModViewModel? target)
    {
        if (SelectedProfile == null || IsProfileModsLoading) return;
        _separatorProfileId = SelectedProfile.Id;
        _editingSeparatorId = null;
        var normal = ProfileMods.Where(mod => !mod.IsFixedGroupMember).ToList();
        _newSeparatorOrder = target is { IsUserSeparator: true } ? target.Order
            : target != null && normal.Contains(target) ? normal.IndexOf(target) : normal.Count;
        SeparatorName = string.Empty;
        IsSeparatorNameEditable = true;
        UseDefaultSeparatorColor = true;
        SeparatorSelectedColor = Colors.CornflowerBlue;
        SeparatorError = string.Empty;
        SeparatorDialogTitle = "添加分隔符分组";
        IsSeparatorDialogOpen = true;
    }

    [RelayCommand]
    private void OpenRenameSeparator(ProfileModViewModel? separator)
    {
        if (SelectedProfile == null || separator?.IsSeparator != true) return;
        _separatorProfileId = SelectedProfile.Id;
        _editingSeparatorId = separator.ModId;
        SeparatorName = separator.ModName;
        IsSeparatorNameEditable = separator.IsUserSeparator;
        UseDefaultSeparatorColor = !separator.HasCustomSeparatorColor;
        SeparatorSelectedColor = Color.TryParse(separator.SeparatorColor, out var color) ? color : Colors.CornflowerBlue;
        SeparatorError = string.Empty;
        SeparatorDialogTitle = separator.IsUserSeparator ? "编辑分组名称与颜色" : "编辑依赖加载器分组颜色";
        IsSeparatorDialogOpen = true;
    }

    [RelayCommand]
    private void CancelSeparatorDialog() => IsSeparatorDialogOpen = false;

    [RelayCommand]
    private async Task ConfirmSeparatorAsync()
    {
        if (SelectedProfile?.Id != _separatorProfileId) return;
        if (string.IsNullOrWhiteSpace(SeparatorName)) { SeparatorError = "请输入分组名称。"; return; }
        try
        {
            var color = UseDefaultSeparatorColor ? string.Empty
                : $"#{SeparatorSelectedColor.R:X2}{SeparatorSelectedColor.G:X2}{SeparatorSelectedColor.B:X2}";
            await SuppressLocalChangeAsync(async () =>
            {
                if (_editingSeparatorId is { } id)
                {
                    await _profileService.UpdateSeparatorAsync(_separatorProfileId, id, SeparatorName, color);
                    if (SelectedProfile?.Id != _separatorProfileId) return;
                    var row = id == Guid.Empty ? _luaSeparator : ProfileSeparators.First(item => item.ModId == id);
                    if (row.IsUserSeparator) row.ModName = SeparatorName.Trim();
                    row.SeparatorColor = color;
                }
                else
                {
                    var separator = await _profileService.AddSeparatorAsync(_separatorProfileId, SeparatorName, _newSeparatorOrder, color);
                    if (SelectedProfile?.Id == _separatorProfileId) ProfileSeparators.Add(CreateSeparatorRow(separator));
                }
            });
            IsSeparatorDialogOpen = false;
            ApplyModFilter();
            IsProfileModsEmpty = ProfileMods.Count == 0 && ProfileSeparators.Count == 0;
        }
        catch (Exception ex) { SeparatorError = $"保存分组失败: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ToggleSeparatorAsync(ProfileModViewModel? separator)
    {
        if (separator?.IsSeparator != true || SelectedProfile is not { } profile) return;
        try
        {
            var collapsed = !separator.IsCollapsed;
            var memberCount = GetSeparatorMembers(separator).Count;
            await SuppressLocalChangeAsync(() => _profileService.SetSeparatorCollapsedAsync(profile.Id, separator.ModId, collapsed));
            if (SelectedProfile != profile) return;
            if (separator.IsUserSeparator) separator.MemberCount ??= memberCount;
            separator.IsCollapsed = collapsed;
            foreach (var member in GetSeparatorMembers(separator)) member.IsSelected = false;
            ApplyModFilter();
            _modSelection.RemoveMissingAnchor(VisibleProfileMods);
            if (FocusedMod != null && !VisibleProfileMods.Contains(FocusedMod)) FocusedMod = null;
        }
        catch (Exception ex) { _notificationService.ShowError($"保存分隔符状态失败: {ex.Message}", "配置文件"); }
    }

    [RelayCommand]
    private async Task RemoveSeparatorAsync(ProfileModViewModel? separator)
    {
        if (separator?.IsUserSeparator != true || SelectedProfile is not { } profile) return;
        try
        {
            await SuppressLocalChangeAsync(() => _profileService.RemoveSeparatorAsync(profile.Id, separator.ModId));
            if (SelectedProfile != profile) return;
            ProfileSeparators.Remove(separator);
            ApplyModFilter();
            IsProfileModsEmpty = ProfileMods.Count == 0 && ProfileSeparators.Count == 0;
        }
        catch (Exception ex) { _notificationService.ShowError($"删除分隔符失败: {ex.Message}", "配置文件"); }
    }

    private static ProfileModViewModel CreateSeparatorRow(ProfileSeparator separator) => new()
    {
        ModId = separator.Id, ProfileId = separator.ProfileId, IsSeparator = true, IsUserSeparator = true,
        ModName = separator.Name, Order = separator.Order, IsCollapsed = separator.IsCollapsed,
        MemberCount = separator.MemberCount,
        SeparatorColor = separator.Color
    };

    private List<ProfileModViewModel> BuildOrdinaryRows()
    {
        var normal = ProfileMods.Where(mod => !mod.IsFixedGroupMember).ToList();
        var rows = new List<ProfileModViewModel>();
        var separators = ProfileSeparators.OrderBy(row => row.Order).ToArray();
        var next = 0;
        for (var index = 0; index <= normal.Count; index++)
        {
            while (next < separators.Length && Math.Clamp(separators[next].Order, 0, normal.Count) <= index)
                rows.Add(separators[next++]);
            if (index < normal.Count) rows.Add(normal[index]);
        }
        return rows;
    }

    public List<ProfileModViewModel> GetSeparatorMembers(ProfileModViewModel separator)
        => separator.IsUserSeparator
            ? BuildOrdinaryRows().SkipWhile(row => row != separator).Skip(1).TakeWhile(row => !row.IsUserSeparator)
                .Take(separator.MemberCount ?? int.MaxValue).ToList()
            : ProfileMods.Where(row => row.IsFixedGroupMember).ToList();

    private void AdjustSeparatorPositionsAfterRemoval(IReadOnlyCollection<ProfileModViewModel> removed)
    {
        var normal = ProfileMods.Where(mod => !mod.IsFixedGroupMember).ToList();
        foreach (var separator in ProfileSeparators.Where(row => row.MemberCount != null || row.IsCollapsed))
        {
            var members = GetSeparatorMembers(separator);
            separator.MemberCount = members.Count - members.Count(removed.Contains);
        }
        foreach (var separator in ProfileSeparators)
            separator.Order -= normal.Take(separator.Order).Count(removed.Contains);
    }

    partial void OnSelectedProfileChanged(ProfileItemViewModel? value)
    {
        CloseDetailPanel();
        ClearModSelection();
        IsAddModOpen = false;
        CloseConflictPicker();
        IsSeparatorDialogOpen = false;
        ProfileSeparators.Clear();
        _luaSeparator.IsCollapsed = false;
        _luaSeparator.SeparatorColor = string.Empty;
        AvailableLibraryMods.Clear();
        AddModSearchText = string.Empty;
        ApplyAddModFilter();
        if (value == null && IsCompareOpen)
        {
            IsCompareOpen = false;
            CompareResults.Clear();
        }
        HasProfileModsError = false;
        ProfileModsErrorMessage = string.Empty;
        IsProfileModsEmpty = value == null;
        OnPropertyChanged(nameof(HasSelectedProfile));
        OnPropertyChanged(nameof(HasNoSelectedProfile));
        RefreshProfileModStateProperties();
        if (value != null)
        {
            // 设置开启时，点击列表即切换为当前配置（默认关闭，见设置页“配置文件”）
            if (!value.IsActive)
            {
                _ = ActivateOnSelectIfEnabledAsync(value);
            }
            _ = LoadProfileModsAsync(value.Id);
        }
    }

    /// <summary>读取“点击列表自动设为当前”设置后切换；仍处于选中态才生效，避免过期读取误切换。</summary>
    private async Task ActivateOnSelectIfEnabledAsync(ProfileItemViewModel profile)
    {
        try
        {
            var enabled = await _settingsService.GetSettingAsync(SettingKeys.ProfilesAutoSwitchOnSelect, true);
            if (enabled && !profile.IsActive && ReferenceEquals(SelectedProfile, profile))
            {
                await SetActiveProfileAsync(profile);
            }
        }
        catch
        {
            // 设置读取失败时保持仅选中不切换
        }
    }

    private async Task LoadProfileModsAsync(Guid profileId)
    {
        CloseDetailPanel();
        _profileModsCts?.Cancel();
        _profileModsCts?.Dispose();
        _profileModsCts = new CancellationTokenSource();
        var cancellationToken = _profileModsCts.Token;

        IsProfileModsLoading = true;
        IsProfileModsEmpty = false;
        HasProfileModsError = false;
        ProfileModsErrorMessage = string.Empty;
        try
        {
            _bottomToTop = await _settingsService.GetSettingAsync(SettingKeys.DefaultDeploymentOrder, DeploymentOrder.TopToBottom.ToString())
                == DeploymentOrder.BottomToTop.ToString();
            var lockLoaders = await _settingsService.GetSettingAsync(SettingKeys.ProfilesLockLoaders, true);
            var profile = await _profileService.GetProfileByIdAsync(profileId, cancellationToken);
            if (profile == null)
            {
                HasProfileModsError = true;
                ProfileModsErrorMessage = "找不到所选 Profile。";
                return;
            }

            var modsById = (await _modService.GetModsByIdsAsync(
                profile.Mods.Select(profileMod => profileMod.ModId), cancellationToken))
                .ToDictionary(mod => mod.Id);
            var profileMods = new List<ProfileModViewModel>();
            foreach (var profileMod in profile.Mods.OrderBy(m => m.Order))
            {
                if (modsById.TryGetValue(profileMod.ModId, out var mod))
                {
                    var cardVm = new ProfileModViewModel
                    {
                        ProfileId = profileMod.ProfileId,
                        ModId = profileMod.ModId,
                        ModName = mod.Name,
                        ModVersion = mod.PlatformLink?.InstalledVersion ?? mod.Version,
                        SourceInfo = new ModItemViewModel(mod) { CompatibilityBadge = Compatibility?.GetBadge(mod.Id) },
                        Enabled = profileMod.Enabled,
                        Order = profileMod.Order,
                        BslRole = BslProfilePolicy.Role(mod),
                        LoaderLockEnabled = lockLoaders,
                        HasLuaResources = mod.ResourceTypeIds?.Contains(0xa14e8dfa2cd117e2UL) == true,
                        ModPath = mod.Path,
                        ThumbnailPath = mod.ThumbnailPath
                    };
                    // 清单与已保存的选项选择随卡片保存，点开选项覆盖层时使用
                    cardVm.InitializeOptions(
                        mod.Manifest,
                        ProfileModOptionResolver.ResolveEffective(mod.Manifest, profileMod.OptionsJson));
                    profileMods.Add(cardVm);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            ProfileMods = new ObservableCollection<ProfileModViewModel>(profileMods);
            _luaSeparator.ProfileId = profile.Id;
            _luaSeparator.IsCollapsed = profile.IsLuaGroupCollapsed;
            _luaSeparator.SeparatorColor = profile.LuaSeparatorColor;
            _luaSeparator.ChevronAngle = _bottomToTop ? -90 : 90;
            ProfileSeparators = new ObservableCollection<ProfileModViewModel>(profile.Separators
                .OrderBy(separator => separator.Order)
                .Select(CreateSeparatorRow));
            ArrangeBslDisplay();
            RenumberProfileMods();
            ApplyModFilter();
            IsProfileModsEmpty = ProfileMods.Count == 0 && ProfileSeparators.Count == 0;
            _ = LoadProfileModIconsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (_profileModsCts?.Token == cancellationToken)
            {
                ProfileMods = new ObservableCollection<ProfileModViewModel>();
                IsProfileModsEmpty = false;
                HasProfileModsError = true;
                ProfileModsErrorMessage = $"加载 Profile Mod 失败: {ex.Message}";
                _notificationService.ShowError(ProfileModsErrorMessage, "配置文件");
            }
        }
        finally
        {
            if (_profileModsCts?.Token == cancellationToken)
            {
                IsProfileModsLoading = false;
            }
        }
    }

    private void RefreshProfileModStateProperties()
    {
        OnPropertyChanged(nameof(IsProfileModsContentVisible));
        OnPropertyChanged(nameof(IsProfileModsEmptyStateVisible));
        OnPropertyChanged(nameof(IsProfileModsLoadingStateVisible));
        OnPropertyChanged(nameof(IsProfileModsErrorStateVisible));
    }

    /// <summary>按库页同样的模式后台加载卡片缩略图；已加载或无缩略图的项自动跳过。</summary>
    private async Task LoadProfileModIconsAsync(CancellationToken cancellationToken)
    {
        foreach (var mod in ProfileMods.ToArray())
        {
            if (cancellationToken.IsCancellationRequested) return;
            await mod.LoadIconAsync(cancellationToken);
        }
    }

    [RelayCommand]
    private void OpenModLocation(ProfileModViewModel? profileMod)
    {
        if (profileMod == null || string.IsNullOrWhiteSpace(profileMod.ModPath)) return;
        try
        {
            if (Directory.Exists(profileMod.ModPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = profileMod.ModPath,
                    UseShellExecute = true
                });
            }
            else
            {
                _notificationService.ShowWarning("模组目录不存在", "打开模组目录");
            }
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"打开模组目录失败: {ex.Message}", "打开模组目录");
        }
    }

    [RelayCommand]
    private Task RetryProfileModsAsync() => SelectedProfile == null
        ? Task.CompletedTask
        : LoadProfileModsAsync(SelectedProfile.Id);

    [RelayCommand]
    private async Task OpenAddModAsync()
    {
        if (SelectedProfile == null) return;

        try
        {
            var profile = await _profileService.GetProfileByIdAsync(SelectedProfile.Id);
            if (profile == null) return;

            var usedModIds = profile.Mods.Select(mod => mod.ModId).ToHashSet();
            var available = (await _modService.GetAllModsAsync())
                .Where(mod => !usedModIds.Contains(mod.Id))
                .OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
                .Select(mod => new LibraryModChoiceViewModel
                {
                    Id = mod.Id,
                    Name = mod.Name,
                    Version = mod.PlatformLink?.InstalledVersion ?? mod.Version,
                    Author = mod.PlatformLink?.Author ?? mod.Author
                })
                .ToList();

            AddModSearchText = string.Empty;
            AvailableLibraryMods = new ObservableCollection<LibraryModChoiceViewModel>(available);
            IsAddModOpen = true;
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"读取模组库失败: {ex.Message}", "添加模组");
        }
    }

    [RelayCommand]
    private void CloseAddMod() => IsAddModOpen = false;

    [RelayCommand]
    private async Task AddModToProfileAsync(LibraryModChoiceViewModel? mod)
    {
        if (mod == null || SelectedProfile == null) return;

        try
        {
            await SuppressLocalChangeAsync(() =>
                _profileService.AddModToProfileAsync(SelectedProfile.Id, mod.Id));
            SelectedProfile.ModCount++;
            AvailableLibraryMods.Remove(mod);
            ApplyAddModFilter();
            await LoadProfileModsAsync(SelectedProfile.Id);
            _notificationService.ShowSuccess($"已将“{mod.Name}”添加到当前查看的 Profile", "添加模组");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"添加模组失败: {ex.Message}", "添加模组");
        }
    }

    [RelayCommand]
    private async Task OpenCompareAsync(ProfileItemViewModel? leftProfile)
    {
        if (leftProfile == null) return;

        CompareLeftProfile = leftProfile;
        CompareRightProfile = Profiles.FirstOrDefault(profile => profile.Id != leftProfile.Id);
        IsCompareOpen = true;
        await LoadCompareAsync();
    }

    [RelayCommand]
    private void CloseCompare()
    {
        _compareCts?.Cancel();
        IsCompareOpen = false;
        CompareResults.Clear();
    }

    private async Task LoadCompareAsync()
    {
        if (CompareLeftProfile == null || CompareRightProfile == null) return;

        _compareCts?.Cancel();
        _compareCts?.Dispose();
        _compareCts = new CancellationTokenSource();
        var cancellationToken = _compareCts.Token;

        try
        {
            var profilesTask = Task.WhenAll(
                _profileService.GetProfileByIdAsync(CompareLeftProfile.Id, cancellationToken),
                _profileService.GetProfileByIdAsync(CompareRightProfile.Id, cancellationToken));
            await profilesTask;

            var left = profilesTask.Result[0];
            var right = profilesTask.Result[1];
            if (left == null || right == null) return;

            var leftMods = left.Mods.ToDictionary(mod => mod.ModId);
            var rightMods = right.Mods.ToDictionary(mod => mod.ModId);
            var names = (await _modService.GetModsByIdsAsync(
                leftMods.Keys.Concat(rightMods.Keys), cancellationToken))
                .ToDictionary(mod => mod.Id, mod => mod.Name);
            var results = leftMods.Keys.Concat(rightMods.Keys).Distinct()
                .Select(id => CreateCompareItem(id, leftMods, rightMods, names))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            cancellationToken.ThrowIfCancellationRequested();
            CompareResults = new ObservableCollection<ProfileCompareItemViewModel>(results);
            ApplyCompareFilter();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"配置文件对比失败: {ex.Message}", "配置文件对比");
        }
    }

    private void ApplyCompareFilter()
    {
        foreach (var item in CompareResults)
        {
            item.IsVisible = CompareFilter switch
            {
                "仅左侧" => item.Status == ProfileCompareStatus.OnlyLeft,
                "仅右侧" => item.Status == ProfileCompareStatus.OnlyRight,
                "状态不同" => item.Status == ProfileCompareStatus.EnabledStateDifferent,
                "两边共有" => item.Status == ProfileCompareStatus.Common,
                _ => true
            };
        }
    }

    private static ProfileCompareItemViewModel CreateCompareItem(
        Guid modId,
        IReadOnlyDictionary<Guid, ProfileMod> left,
        IReadOnlyDictionary<Guid, ProfileMod> right,
        IReadOnlyDictionary<Guid, string> names)
    {
        var hasLeft = left.TryGetValue(modId, out var leftMod);
        var hasRight = right.TryGetValue(modId, out var rightMod);
        var status = !hasLeft ? ProfileCompareStatus.OnlyRight
            : !hasRight ? ProfileCompareStatus.OnlyLeft
            : leftMod!.Enabled == rightMod!.Enabled ? ProfileCompareStatus.Common
            : ProfileCompareStatus.EnabledStateDifferent;

        return new ProfileCompareItemViewModel
        {
            ModId = modId,
            Name = names.TryGetValue(modId, out var name) ? name : modId.ToString(),
            Status = status,
            StatusText = status switch
            {
                ProfileCompareStatus.OnlyLeft => "仅左侧",
                ProfileCompareStatus.OnlyRight => "仅右侧",
                ProfileCompareStatus.EnabledStateDifferent => "启用状态不同",
                _ => "两边共有"
            },
            LeftState = !hasLeft ? "-" : leftMod!.Enabled ? "已启用" : "已禁用",
            RightState = !hasRight ? "-" : rightMod!.Enabled ? "已启用" : "已禁用"
        };
    }

    [RelayCommand]
    private async Task CreateProfileAsync()
    {
        var name = NewProfileName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            _notificationService.ShowWarning("请输入配置文件名称", "新建配置文件");
            return;
        }

        try
        {
            await SuppressLocalChangeAsync(() =>
                _profileService.CreateProfileAsync(name));
            NewProfileName = string.Empty;
            await LoadProfilesAsync();
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"创建失败: {ex.Message}", "新建配置文件");
        }
    }

    public async Task DeleteProfileAsync(ProfileItemViewModel? profile)
    {
        if (profile == null) return;

        if (profile.IsDefault)
        {
            _notificationService.ShowWarning("默认配置文件不能删除", "删除配置文件");
            return;
        }

        if (profile.IsActive)
        {
            _notificationService.ShowWarning("请先切换到其他配置文件，再删除当前配置", "删除配置文件");
            return;
        }

        if (!await _confirmationService.ConfirmAsync("删除配置文件", $"确定删除配置文件“{profile.Name}”吗？模组库中的模组不会被删除。"))
        {
            return;
        }

        try
        {
            await SuppressLocalChangeAsync(() =>
                _profileService.DeleteProfileAsync(profile.Id));
            Profiles.Remove(profile);
            if (ReferenceEquals(SelectedProfile, profile))
            {
                SelectedProfile = Profiles.FirstOrDefault(item => item.IsActive) ?? Profiles.FirstOrDefault();
            }
            _notificationService.ShowSuccess($"已删除配置文件“{profile.Name}”", "删除配置文件");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"删除失败: {ex.Message}", "删除配置文件");
        }
    }

    public async Task RenameProfileAsync(ProfileItemViewModel? profile)
    {
        if (profile == null) return;

        // 页面无独立输入对话框：新名称复用左侧面板顶部的名称输入框
        var name = NewProfileName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            _notificationService.ShowWarning("请先在上方输入框填写新名称，再点击重命名", "重命名配置文件");
            return;
        }
        try
        {
            await SuppressLocalChangeAsync(() =>
                _profileService.RenameProfileAsync(profile.Id, name));
            NewProfileName = string.Empty;
            await LoadProfilesAsync();
            _notificationService.ShowSuccess("配置文件名称已更新", "重命名配置文件");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"重命名失败: {ex.Message}", "重命名配置文件");
        }
    }

    public async Task DuplicateProfileAsync(ProfileItemViewModel? profile)
    {
        if (profile == null) return;

        var newName = $"{profile.Name} (Copy)";
        try
        {
            await SuppressLocalChangeAsync(() =>
                _profileService.DuplicateProfileAsync(profile.Id, newName));
            await LoadProfilesAsync();
            _notificationService.ShowSuccess($"已复制配置文件为“{newName}”", "复制配置文件");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"复制失败: {ex.Message}", "复制配置文件");
        }
    }

    /// <summary>设为当前配置（右键菜单 / 选中自动切换共用）。</summary>
    public async Task SetActiveProfileAsync(ProfileItemViewModel? profile)
    {
        if (profile == null) return;

        try
        {
            await SuppressLocalChangeAsync(() =>
                _profileService.SetActiveProfileAsync(profile.Id));

            // 更新所有 Profile 的激活状态
            foreach (var p in Profiles)
            {
                p.IsActive = p.Id == profile.Id;
            }
            SelectedProfile = profile;
            _suppressActiveProfileSelection = true;
            ActiveProfileSelection = profile;
            _suppressActiveProfileSelection = false;
            await LoadProfileModsAsync(profile.Id);
            ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"切换失败: {ex.Message}", "切换配置文件");
        }
    }

    [RelayCommand]
    private async Task ToggleModEnabledAsync(ProfileModViewModel? profileMod)
    {
        if (profileMod == null || SelectedProfile == null) return;

        var enabled = !profileMod.Enabled;
        try
        {
            await SuppressLocalChangeAsync(() =>
                _profileService.SetModEnabledAsync(SelectedProfile.Id, profileMod.ModId, enabled));
            profileMod.Enabled = enabled;
            if (profileMod.IsBslLoader && enabled)
            {
                var saved = await _profileService.GetProfileByIdAsync(SelectedProfile.Id);
                if (saved != null)
                {
                    var states = saved.Mods.ToDictionary(item => item.ModId, item => item.Enabled);
                    foreach (var card in ProfileMods)
                        if (states.TryGetValue(card.ModId, out var value)) card.Enabled = value;
                }
            }
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"更新模组状态失败: {ex.Message}", "配置文件");
        }
    }

    // ── 模组选项覆盖层（应用内模态弹窗，非独立页面） ──

    [ObservableProperty]
    private bool _isModOptionsDialogOpen;

    [ObservableProperty]
    private ProfileModOptionsDialogViewModel? _modOptionsDialog;

    /// <summary>右键“移动到指定位置”输入面板。</summary>
    [ObservableProperty]
    private bool _isMoveToPositionDialogOpen;

    [ObservableProperty]
    private string _moveToPositionText = string.Empty;

    [ObservableProperty]
    private string _moveToPositionError = string.Empty;

    public bool HasMoveToPositionError => !string.IsNullOrWhiteSpace(MoveToPositionError);

    partial void OnMoveToPositionErrorChanged(string value) => OnPropertyChanged(nameof(HasMoveToPositionError));

    [RelayCommand]
    private void OpenMoveToPosition(ProfileModViewModel? target)
    {
        if (target == null || !target.CanDrag || ProfileMods.Count == 0) return;
        if (!target.IsSelected)
        {
            foreach (var mod in ProfileMods) mod.IsSelected = false;
            target.IsSelected = true;
        }

        SetModSelectionAnchor(target);
        MoveToPositionText = target.Position.ToString(CultureInfo.InvariantCulture);
        MoveToPositionError = string.Empty;
        IsMoveToPositionDialogOpen = true;
    }

    [RelayCommand]
    private void CancelMoveToPosition()
    {
        IsMoveToPositionDialogOpen = false;
        MoveToPositionError = string.Empty;
    }

    [RelayCommand]
    private async Task ConfirmMoveToPositionAsync()
    {
        MoveToPositionError = string.Empty;
        if (!int.TryParse(MoveToPositionText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetPosition))
        {
            MoveToPositionError = "请输入有效的整数位置。";
            return;
        }

        if (targetPosition < 1 || targetPosition > ProfileMods.Count)
        {
            MoveToPositionError = $"位置必须在 1 到 {ProfileMods.Count} 之间。";
            return;
        }

        var moving = ProfileMods.Where(mod => mod.IsSelected).ToList();
        if (moving.Count == 0 || moving.Any(mod => !mod.CanDrag))
        {
            MoveToPositionError = "当前选择中包含不能调整位置的模组。";
            return;
        }

        var movingIds = moving.Select(mod => mod.ModId).ToList();
        var others = ProfileMods.Where(mod => !movingIds.Contains(mod.ModId)).ToList();
        var insertIndex = Math.Clamp(targetPosition - 1, 0, others.Count);
        Guid? anchor = insertIndex < others.Count ? others[insertIndex].ModId : null;
        // 分隔符前一组的末尾使用分隔符锚点，不能借用另一组首项的 ID。
        if (_settingsService.GetSetting(SettingKeys.ProfilesLockLoaders, true)
            && moving.All(mod => mod.IsFixedGroupMember == _bottomToTop)
            && insertIndex == GetLuaGroupBoundary(others))
            anchor = Guid.Empty;

        if (!CanMoveMods(movingIds, anchor))
        {
            MoveToPositionError = "该位置会破坏 Lua 模组或加载器的固定顺序。";
            return;
        }

        await MoveModsAsync(movingIds, anchor);
        CancelMoveToPosition();
    }

    [RelayCommand]
    private void OpenModOptions(ProfileModViewModel? profileMod)
    {
        if (profileMod == null || !profileMod.HasOptions) return;

        // 重新打开前先释放上一个弹窗解码的位图，避免连续开关累积原生内存。
        ReleaseModOptionsDialog();
        ModOptionsDialog = new ProfileModOptionsDialogViewModel(this, profileMod);
        IsModOptionsDialogOpen = true;
        _ = ModOptionsDialog.LoadImagesAsync();
    }

    [RelayCommand]
    public void CloseModOptions()
    {
        IsModOptionsDialogOpen = false;
        ReleaseModOptionsDialog();
        ModOptionsDialog = null;
    }

    /// <summary>关闭弹窗时释放位图；预览覆盖层若正显示弹窗图片，先关闭预览。</summary>
    private void ReleaseModOptionsDialog()
    {
        var dialog = ModOptionsDialog;
        if (dialog == null) return;
        if (_imagePreview is { IsOpen: true } preview && dialog.OwnsImage(preview.Image)) preview.Close();
        dialog.Dispose();
    }

    /// <summary>点击图片（模组图标/选项图/子选项图）：在主窗口顶层的覆盖层预览原图。</summary>
    public void ShowImagePreview(Bitmap? image, string? title) => _imagePreview?.Show(image, title);

    /// <summary>关闭共享预览覆盖层（选项弹窗释放自己按需解码的原图时使用）。</summary>
    internal void CloseImagePreview() => _imagePreview?.Close();

    /// <summary>预览覆盖层当前是否正显示指定位图。</summary>
    internal bool IsImagePreviewShowing(Bitmap? image)
        => _imagePreview is { IsOpen: true } preview && ReferenceEquals(preview.Image, image);

    /// <summary>
    /// 把模组卡片的当前选项选择持久化到当前 Profile。
    /// 本地状态已同步，抑制 ProfilesChanged 重载，避免重建卡片列表丢失覆盖层状态。
    /// </summary>
    public async Task PersistModOptionsAsync(ProfileModViewModel profileMod)
    {
        if (SelectedProfile == null) return;

        try
        {
            await SuppressLocalChangeAsync(() =>
                _profileService.SetModOptionsAsync(SelectedProfile.Id, profileMod.ModId, profileMod.StoredOptions));
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"保存模组选项失败: {ex.Message}", "配置文件");
        }
    }

    [RelayCommand]
    private async Task RemoveModFromProfileAsync(ProfileModViewModel? profileMod)
    {
        if (profileMod == null || SelectedProfile == null) return;

        try
        {
            await SuppressLocalChangeAsync(() =>
                _profileService.RemoveModFromProfileAsync(SelectedProfile.Id, profileMod.ModId));
            AdjustSeparatorPositionsAfterRemoval(new[] { profileMod });
            ProfileMods.Remove(profileMod);
            SelectedProfile.ModCount = Math.Max(0, SelectedProfile.ModCount - 1);
            RenumberProfileMods();
            ApplyModFilter();
            IsProfileModsEmpty = ProfileMods.Count == 0 && ProfileSeparators.Count == 0;
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"移除模组失败: {ex.Message}", "配置文件");
        }
    }

    /// <summary>
    /// 拖拽排序落点处理：左侧配置文件列表第一位固定为最早创建的默认配置，其余可自由移动。
    /// </summary>
    public async Task MoveProfileAsync(Guid profileId, int targetIndex)
    {
        if (Profiles.Count == 0) return;
        var profile = Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile == null) return;

        // 默认配置（列表第一位）不可移动
        if (ReferenceEquals(profile, Profiles[0])) return;

        var currentIndex = Profiles.IndexOf(profile);
        var target = Math.Clamp(targetIndex, 1, Profiles.Count - 1);
        if (target == currentIndex) return;

        Profiles.Move(currentIndex, target);
        try
        {
            await SuppressLocalChangeAsync(() =>
                _profileService.ReorderProfilesAsync(Profiles.Select(p => p.Id).ToList()));
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"保存配置文件顺序失败: {ex.Message}", "配置文件");
            await LoadProfilesAsync();
        }
    }

    /// <summary>
    /// 拖拽排序落点处理：模组卡片拖到 anchorModId 之前（为空表示末尾）；
    /// movingModIds 可包含多个（多选拖动），保持其相对顺序整体移动。
    /// </summary>
    public async Task MoveModsAsync(IReadOnlyList<Guid> movingModIds, Guid? anchorModId)
    {
        if (SelectedProfile == null || IsProfileModsLoading) return;
        if (!CanMoveMods(movingModIds, anchorModId)) return;

        var fullRows = GetSortableRows();
        var movingSet = ExpandMovingRows(movingModIds).ToHashSet();
        if (movingSet.Count == 0 || movingSet.Count == fullRows.Count || movingSet.Contains(anchorModId ?? Guid.Empty)) return;

        var moving = fullRows.Where(mod => movingSet.Contains(mod.ModId)).ToList();
        var others = fullRows.Where(mod => !movingSet.Contains(mod.ModId)).ToList();
        // 收起分组带走的成员范围有明确终点，不能因组后插入或整组移动而吸收其它模组。
        var boundedMembers = ProfileSeparators.Where(row => row.MemberCount != null || row.IsCollapsed)
            .ToDictionary(row => row, row => GetSeparatorMembers(row).Select(member => member.ModId).ToHashSet());
        var originalInsertIndex = anchorModId == Guid.Empty ? GetLuaGroupBoundary(fullRows)
            : anchorModId is { } targetId ? fullRows.FindIndex(row => row.ModId == targetId) : fullRows.Count;
        foreach (var (separator, members) in boundedMembers)
        {
            var start = fullRows.IndexOf(separator);
            // 展开后仍允许向可见成员范围内插入模组。
            if (!separator.IsCollapsed && !movingSet.Contains(separator.ModId)
                && originalInsertIndex > start && originalInsertIndex <= start + 1 + members.Count)
                members.UnionWith(moving.Where(row => !row.IsSeparator && !row.IsFixedGroupMember).Select(row => row.ModId));
        }

        // 落点换算为"anchor 之前的非拖动项数量"，anchor 本身也在拖动集中时视为放到末尾
        var insertIndex = others.Count;
        if (anchorModId == Guid.Empty)
            insertIndex = GetLuaGroupBoundary(others);
        if (anchorModId is { } anchorId)
        {
            var anchorPos = -1;
            for (var i = 0; i < fullRows.Count; i++)
            {
                if (fullRows[i].ModId == anchorId)
                {
                    anchorPos = i;
                    break;
                }
            }
            if (anchorPos >= 0)
            {
                insertIndex = fullRows.Take(anchorPos).Count(mod => !movingSet.Contains(mod.ModId));
            }
        }

        var newOrder = new List<ProfileModViewModel>(fullRows.Count);
        newOrder.AddRange(others.Take(insertIndex));
        newOrder.AddRange(moving);
        newOrder.AddRange(others.Skip(insertIndex));

        if (newOrder.Select(mod => mod.ModId).SequenceEqual(fullRows.Select(mod => mod.ModId))) return;

        foreach (var (separator, members) in boundedMembers)
            separator.MemberCount = newOrder.SkipWhile(row => row != separator).Skip(1)
                .TakeWhile(row => !row.IsSeparator && !row.IsFixedGroupMember && members.Contains(row.ModId)).Count();
        ProfileMods = new ObservableCollection<ProfileModViewModel>(newOrder.Where(row => !row.IsSeparator));
        var ordinaryCount = 0;
        foreach (var row in newOrder.Where(row => !row.IsFixedGroupMember))
        {
            if (row.IsUserSeparator) row.Order = ordinaryCount;
            else ordinaryCount++;
        }
        ProfileSeparators = new ObservableCollection<ProfileModViewModel>(newOrder.Where(row => row.IsUserSeparator));
        RenumberProfileMods();
        ApplyModFilter();

        try
        {
            await SuppressLocalChangeAsync(() =>
                _profileService.ReorderProfileRowsAsync(
                    SelectedProfile.Id, CanonicalModIds(), BuildOrdinaryRows().Select(row => row.ModId).ToArray(),
                    ProfileSeparators.Where(row => row.MemberCount != null).ToDictionary(row => row.ModId, row => row.MemberCount!.Value)));
            // 更新卡片的持久化位次，切换方向时仍保持依赖模组之间的实际覆盖顺序。
            var canonicalIds = CanonicalModIds();
            for (var i = 0; i < canonicalIds.Count; i++) ProfileMods.First(mod => mod.ModId == canonicalIds[i]).Order = i;
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"保存部署顺序失败: {ex.Message}", "配置文件");
            await LoadProfileModsAsync(SelectedProfile.Id);
        }
    }

    private List<Guid> CanonicalModIds()
    {
        if (!_settingsService.GetSetting(SettingKeys.ProfilesLockLoaders, true))
            return ProfileMods.Select(mod => mod.ModId).ToList();
        var dependencies = ProfileMods.Where(mod => mod.BslRole == BslModRole.Dependency);
        if (_bottomToTop) dependencies = dependencies.Reverse();
        return ProfileMods.Where(mod => !mod.IsFixedGroupMember).Concat(dependencies)
            .Concat(ProfileMods.Where(mod => mod.IsBslLoader).OrderBy(mod => mod.Order))
            .Concat(ProfileMods.Where(mod => mod.BslRole == BslModRole.SmoothBoot).OrderBy(mod => mod.Order))
            .Select(mod => mod.ModId).ToList();
    }

    public bool CanMoveMods(IReadOnlyList<Guid> ids, Guid? anchor)
    {
        var rows = GetSortableRows();
        if (ids.Any(id => rows.All(row => row.ModId != id))) return false;
        var expandedIds = ExpandMovingRows(ids);
        if (anchor is { } target && expandedIds.Contains(target)) return false;
        var moving = rows.Where(mod => expandedIds.Contains(mod.ModId)).ToList();
        if (!_settingsService.GetSetting(SettingKeys.ProfilesLockLoaders, true))
            return moving.Count > 0 && moving.All(mod => mod.CanDrag)
                && (anchor == null || rows.Any(row => row.ModId == anchor));
        if (moving.Count == 0 || moving.Any(mod => !mod.CanDrag) || moving.Select(mod => mod.IsFixedGroupMember).Distinct().Count() != 1)
            return false;
        var beforeSeparator = moving[0].IsFixedGroupMember == _bottomToTop;
        if (beforeSeparator && anchor == null && rows.Any(mod => mod.IsFixedGroupMember)
            || !beforeSeparator && anchor == Guid.Empty) return false;
        // 两种部署方向都以分隔符为边界，不能借另一组的卡片越过分隔符。
        if (beforeSeparator && anchor is { } id
            && rows.Any(mod => mod.ModId == id && mod.IsFixedGroupMember != moving[0].IsFixedGroupMember)) return false;
        if (anchor is { } validAnchor && validAnchor != Guid.Empty && rows.All(row => row.ModId != validAnchor)) return false;
        var others = rows.Where(mod => !expandedIds.Contains(mod.ModId)).ToList();
        var index = anchor == Guid.Empty ? GetLuaGroupBoundary(others)
            : anchor is { } anchorId ? others.FindIndex(mod => mod.ModId == anchorId) : others.Count;
        if (index < 0) index = others.Count;
        others.InsertRange(index, moving);
        var ranks = others.Select(mod => BslProfilePolicy.PriorityRank(mod.BslRole)).ToList();
        return ranks.SequenceEqual(_bottomToTop ? ranks.OrderDescending() : ranks.Order());
    }

    private List<ProfileModViewModel> GetSortableRows()
    {
        var ordinary = BuildOrdinaryRows();
        var fixedRows = ProfileMods.Where(mod => mod.IsFixedGroupMember);
        return (_bottomToTop ? fixedRows.Concat(ordinary) : ordinary.Concat(fixedRows)).ToList();
    }

    public IReadOnlyList<Guid> ExpandMovingRows(IReadOnlyList<Guid> ids)
    {
        var expanded = ids.ToHashSet();
        foreach (var group in ProfileSeparators.Where(row => ids.Contains(row.ModId)))
            foreach (var member in GetSeparatorMembers(group)) expanded.Add(member.ModId);
        return GetSortableRows().Where(row => expanded.Contains(row.ModId)).Select(row => row.ModId).ToArray();
    }

    private void RenumberProfileMods()
    {
        for (var index = 0; index < ProfileMods.Count; index++)
        {
            ProfileMods[index].Position = index + 1;
        }
    }
}

public partial class ProfileItemViewModel : ObservableObject
{
    private readonly Profile _profile;

    public Guid Id => _profile.Id;
    public bool IsDefault => _profile.IsDefault;
    public DateTime ModifiedAt => _profile.ModifiedAt;
    public DateTime CreatedAt => _profile.CreatedAt;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _description;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private int _modCount;

    public ProfileItemViewModel(Profile profile)
    {
        _profile = profile;
        _name = profile.Name;
        _description = profile.Description;
        _isActive = profile.IsActive;
        _modCount = profile.Mods.Count;
    }

    /// <summary>所属页面 VM，右键菜单命令经此转发。</summary>
    internal ProfileViewModel? Owner { get; set; }

    [RelayCommand]
    private Task ActivateAsync() => Owner == null ? Task.CompletedTask : Owner.SetActiveProfileAsync(this);

    [RelayCommand]
    private Task DuplicateAsync() => Owner == null ? Task.CompletedTask : Owner.DuplicateProfileAsync(this);

    [RelayCommand]
    private Task RenameAsync() => Owner == null ? Task.CompletedTask : Owner.RenameProfileAsync(this);

    [RelayCommand]
    private Task DeleteAsync() => Owner == null ? Task.CompletedTask : Owner.DeleteProfileAsync(this);

    partial void OnNameChanged(string value) => _profile.Name = value;
}

public partial class ProfileModViewModel : ObservableObject
{
    public ModItemViewModel? SourceInfo { get; init; }
    [ObservableProperty]
    private bool _isConflictLocation;

    public IEnumerable<ConflictModTarget> ConflictTargets { get; private set; } = [];

    [ObservableProperty]
    private bool _hasConflict;

    public string ConflictTooltip
    {
        get
        {
            if (!HasConflict) return string.Empty;
            var preview = ConflictTargets.Take(9).ToArray();
            return "与以下模组存在覆盖冲突：\n"
                + string.Join("\n", preview.Take(8).Select(target => target.ModName))
                + (preview.Length > 8 ? "\n…点击感叹号查看全部关联模组。" : "")
                + "\n\n详细信息请查看“冲突”页。";
        }
    }

    internal void ApplyConflictStatus(bool hasConflict, ConflictCardIndex index)
    {
        HasConflict = hasConflict;
        ConflictTargets = hasConflict ? index.GetTargets(ModId) : [];
        OnPropertyChanged(nameof(ConflictTooltip));
    }

    public bool IsSeparator { get; init; }
    public bool IsUserSeparator { get; init; }
    public int? MemberCount { get; set; }
    [ObservableProperty]
    private bool _isCollapsed;

    [ObservableProperty]
    private int _groupModCount;

    /// <summary>展开时的箭头方向：常规部署向下，从下到上部署向上。</summary>
    [ObservableProperty]
    private double _chevronAngle = 90;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomSeparatorColor))]
    [NotifyPropertyChangedFor(nameof(SeparatorColorBrush))]
    private string _separatorColor = string.Empty;
    public bool HasCustomSeparatorColor => Color.TryParse(SeparatorColor, out _);
    public IBrush? SeparatorColorBrush => Color.TryParse(SeparatorColor, out var color) ? new SolidColorBrush(color) : null;
    public bool IsExpanded => !IsCollapsed;
    public BslModRole BslRole { get; init; }
    public bool IsBslMember => BslRole != BslModRole.None;
    public bool LoaderLockEnabled { get; init; } = true;
    public bool IsFixedGroupMember => LoaderLockEnabled && IsBslMember;
    public bool IsBslLoader => BslProfilePolicy.IsLoader(BslRole);
    public bool IsSmoothBoot => BslRole == BslModRole.SmoothBoot;
    public bool IsLuaDependency => BslRole == BslModRole.Dependency;
    public bool HasLuaResources { get; init; }
    public bool IsLuaMod => HasLuaResources || IsBslMember;
    public string PriorityLabel => IsSmoothBoot ? "SmoothBoot · 最高优先级" : IsBslLoader ? "BSL / MDL · 固定在依赖之后" : "依赖加载器";
    public bool CanSelect => !IsSeparator && (!LoaderLockEnabled || !IsBslLoader && !IsSmoothBoot);
    public bool CanDrag => CanSelect || IsUserSeparator;

    [ObservableProperty]
    private Guid _profileId;

    [ObservableProperty]
    private Guid _modId;

    [ObservableProperty]
    private string _modName = string.Empty;

    [ObservableProperty]
    private string _modVersion = string.Empty;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private int _order;

    /// <summary>列表中的展示位次（1 起），与部署顺序一致。</summary>
    [ObservableProperty]
    private int _position;

    /// <summary>多选拖动：Ctrl+点击标记，随拖动整体移动。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHighlighted))]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHighlighted))]
    private bool _isFocused;

    public bool IsHighlighted => IsSelected || IsFocused;

    /// <summary>模组库目录，供“打开模组目录”使用。</summary>
    [ObservableProperty]
    private string _modPath = string.Empty;

    [ObservableProperty]
    private string _thumbnailPath = string.Empty;

    [ObservableProperty]
    private Bitmap? _icon;

    partial void OnIconChanged(Bitmap? value)
    {
        if (SourceInfo != null) SourceInfo.Icon = value;
    }

    private bool _isLoadingIcon;

    public string PositionText => Position.ToString("00");

    partial void OnPositionChanged(int value) => OnPropertyChanged(nameof(PositionText));

    partial void OnIsCollapsedChanged(bool value) => OnPropertyChanged(nameof(IsExpanded));

    // ── 模组选项（卡片只保留入口；选项/子选项在覆盖层里编辑） ──

    /// <summary>模组是否带选项定义（v0 互斥下拉 / v1 独立开关+子选项）。</summary>
    [ObservableProperty]
    private bool _hasOptions;

    /// <summary>模组清单（含选项定义），供选项覆盖层构建。</summary>
    internal ModManifest? ManifestRef { get; set; }

    /// <summary>已保存/正在编辑的选项选择状态。</summary>
    internal ProfileModOptionsState StoredOptions { get; set; } = new();

    internal void InitializeOptions(ModManifest? manifest, ProfileModOptionsState state)
    {
        ManifestRef = manifest;
        StoredOptions = state;
        HasOptions = ProfileModOptionResolver.HasOptions(manifest);
    }

    /// <summary>与库页共享缩略图解码缓存；失败保持 null 以显示序号占位。</summary>
    public async Task LoadIconAsync(CancellationToken cancellationToken = default)
    {
        if (Icon != null || _isLoadingIcon) return;
        if (string.IsNullOrEmpty(ThumbnailPath)) return;

        _isLoadingIcon = true;
        try
        {
            Icon = await ModImageCache.DecodeAsync(ThumbnailPath, 256, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // 图标加载失败，保持为 null
        }
        finally
        {
            _isLoadingIcon = false;
        }
    }
}

public sealed record ConflictModTarget(Guid ModId, string ModName);

/// <summary>
/// 模组选项覆盖层（应用内模态弹窗）：展示 v1 选项卡片（独立开关 + 带图片的子选项多选一）
/// 或 v0 互斥选择列表。任何改动立即经 ProfileViewModel 持久化到当前 Profile。
/// </summary>
public partial class ProfileModOptionsDialogViewModel : ObservableObject, IModOptionsSelection
{
    System.Windows.Input.ICommand IModOptionsSelection.ToggleOptionCommand => ToggleOptionCommand;
    System.Windows.Input.ICommand IModOptionsSelection.OpenGroupCommand => OpenGroupCommand;
    System.Windows.Input.ICommand IModOptionsSelection.ToggleGroupCommand => ToggleGroupCommand;
    System.Windows.Input.ICommand IModOptionsSelection.ReturnCommand => ReturnToGroupsCommand;
    private readonly ProfileViewModel _owner;

    public ProfileModViewModel TargetMod { get; }

    /// <summary>v0 模组为 true：展示互斥选择列表而不是独立开关卡片。</summary>
    public bool IsLegacyMode { get; }

    public IReadOnlyList<ProfileModLegacyChoiceViewModel> LegacyChoices { get; }

    public IReadOnlyList<ProfileModOptionCardViewModel> OptionCards { get; }

    /// <summary>一级组件分组（仅 HD2IAS 角色包导入生成）；空表示沿用单级选项列表。</summary>
    public IReadOnlyList<ProfileModOptionGroupViewModel> GroupCards { get; private set; } =
        Array.Empty<ProfileModOptionGroupViewModel>();

    private readonly Dictionary<ProfileModOptionCardViewModel, ProfileModOptionGroupViewModel> _cardGroups = new();
    private CancellationTokenSource? _imageLoad;

    /// <summary>点击卡片图片时才按需解码原图；弹窗关闭时释放，避免 4K 原图常驻。</summary>
    private CancellationTokenSource? _originalPreviewLoad;
    private Bitmap? _originalPreview;
    private string? _originalPreviewHash;
    private bool _disposed;

    [ObservableProperty]
    private ProfileModOptionGroupViewModel? _activeGroup;

    public bool HasGroups => GroupCards.Count > 0;
    public bool ShowGroupList => HasGroups && !IsGroupPage;
    public bool ShowOptionList => !IsLegacyMode && !ShowGroupList;
    public bool IsGroupPage => ActiveGroup != null;
    public IReadOnlyList<ProfileModOptionCardViewModel> VisibleOptionCards =>
        ActiveGroup?.Options ?? OptionCards;

    partial void OnActiveGroupChanged(ProfileModOptionGroupViewModel? value)
    {
        OnPropertyChanged(nameof(IsGroupPage));
        OnPropertyChanged(nameof(ShowGroupList));
        OnPropertyChanged(nameof(ShowOptionList));
        OnPropertyChanged(nameof(VisibleOptionCards));
        OnPropertyChanged(nameof(Title));
    }

    public string Title => IsGroupPage ? $"{TargetMod.ModName} · {ActiveGroup!.Name}" : TargetMod.ModName;

    public ProfileModOptionsDialogViewModel(ProfileViewModel owner, ProfileModViewModel targetMod)
    {
        _owner = owner;
        TargetMod = targetMod;

        var manifest = targetMod.ManifestRef;
        var state = targetMod.StoredOptions;
        var modPath = targetMod.ModPath;

        if (manifest?.ManifestVersion == ManifestVersion.V1 && manifest.Options is { Count: > 0 })
        {
            IsLegacyMode = false;
            var cards = new List<ProfileModOptionCardViewModel>(manifest.Options.Count);
            for (var i = 0; i < manifest.Options.Count; i++)
            {
                var option = manifest.Options[i];

                var subChoices = new List<ProfileModSubOptionChoiceViewModel>();
                if (option.SubOptions is { Count: > 0 })
                {
                    var selectedIndex = i < state.OptionSelections?.Count
                        ? Math.Clamp(state.OptionSelections[i], 0, option.SubOptions.Count - 1)
                        : 0;
                    for (var s = 0; s < option.SubOptions.Count; s++)
                    {
                        var sub = option.SubOptions[s];
                        subChoices.Add(new ProfileModSubOptionChoiceViewModel
                        {
                            Name = sub.Name,
                            Description = sub.Description,
                            ImagePath = ResolveImagePath(modPath, sub.Image),
                            IsSelected = s == selectedIndex
                        });
                    }
                }

                cards.Add(new ProfileModOptionCardViewModel
                {
                    Name = option.Name,
                    Description = option.Description,
                    IsEnabled = i < state.OptionToggles?.Count
                        ? state.OptionToggles[i]
                        : option.DefaultEnabled ?? true,
                    ImagePath = ResolveImagePath(modPath, option.Image),
                    GroupId = option.GroupId,
                    SubOptions = subChoices
                });
            }

            OptionCards = cards;
            GroupCards = BuildOptionGroups(manifest, cards, modPath);
            LegacyChoices = Array.Empty<ProfileModLegacyChoiceViewModel>();
        }
        else if (manifest?.LegacyOptions is { Count: > 0 })
        {
            IsLegacyMode = true;
            var selectedIndex = Math.Clamp(state.LegacySelectedIndex ?? 0, 0, manifest.LegacyOptions.Count - 1);
            LegacyChoices = manifest.LegacyOptions
                .Select((name, index) => new ProfileModLegacyChoiceViewModel
                {
                    Name = name,
                    IsSelected = index == selectedIndex
                })
                .ToList();
            OptionCards = Array.Empty<ProfileModOptionCardViewModel>();
        }
        else
        {
            IsLegacyMode = false;
            OptionCards = Array.Empty<ProfileModOptionCardViewModel>();
            LegacyChoices = Array.Empty<ProfileModLegacyChoiceViewModel>();
        }
    }

    [RelayCommand]
    public void ToggleOption(ProfileModOptionCardViewModel? card)
    {
        if (card == null) return;
        card.IsEnabled = !card.IsEnabled;
        SyncGroup(card);
        SaveState();
    }

    /// <summary>一级组件开关：同步组件内所有选项，并保存到当前配置。</summary>
    [RelayCommand]
    public void ToggleGroup(ProfileModOptionGroupViewModel? group)
    {
        if (group == null) return;
        group.IsEnabled = !group.IsEnabled;
        foreach (var card in group.Options) card.IsEnabled = group.IsEnabled;
        SaveState();
    }

    /// <summary>进入二级组件选项页继续配置。</summary>
    [RelayCommand]
    public void OpenGroup(ProfileModOptionGroupViewModel? group)
    {
        if (group is not { CanOpen: true }) return;
        ActiveGroup = group;
    }

    /// <summary>保存二级页改动并回到一级组件列表。</summary>
    [RelayCommand]
    public void ReturnToGroups()
    {
        SaveState();
        ActiveGroup = null;
    }

    private void SyncGroup(ProfileModOptionCardViewModel card)
    {
        // 一级组件开关表示“该组件仍参与部署”，只要组件内还有选项启用就保持勾选；
        // 关闭某个子选项只排除该选项的文件，不能把整个组件显示成未启用。
        if (_cardGroups.TryGetValue(card, out var group))
            group.IsEnabled = group.Options.Any(option => option.IsEnabled);
    }

    private ProfileModOptionGroupViewModel[] BuildOptionGroups(ModManifest manifest,
        IReadOnlyList<ProfileModOptionCardViewModel> cards, string modPath)
    {
        // 只有 HD2IAS 导入生成的分组才切换成两级配置页；普通模组保持单级列表。
        if (manifest.OptionGroups is not { Count: > 0 } declaredGroups)
            return Array.Empty<ProfileModOptionGroupViewModel>();
        var groups = new List<ProfileModOptionGroupViewModel>();
        foreach (var declared in declaredGroups)
        {
            var options = cards.Where(card => card.GroupId != null
                && card.GroupId.Equals(declared.Id, StringComparison.Ordinal)).ToList();
            if (options.Count == 0) continue;
            groups.Add(CreateGroup(declared.Id, declared.Name, declared.Description, declared.Image, options, modPath));
        }
        // 未分组的选项补一个合成分组，避免存在分组时被一级列表隐藏。
        foreach (var card in cards.Where(card => !_cardGroups.ContainsKey(card)))
            groups.Add(CreateGroup($"option-{groups.Count}", card.Name, card.Description, null, [card], modPath));
        return groups.ToArray();
    }

    private ProfileModOptionGroupViewModel CreateGroup(string id, string name, string description, string? image,
        IReadOnlyList<ProfileModOptionCardViewModel> options, string modPath)
    {
        var group = new ProfileModOptionGroupViewModel
        {
            Id = id, Name = name, Description = description,
            // 初始勾选：组件内存在启用的选项即视为组件启用，与子选项逐项开关语义一致。
            IsEnabled = options.Any(option => option.IsEnabled),
            ImagePath = ResolveImagePath(modPath, image),
            Options = options
        };
        foreach (var option in options) _cardGroups[option] = group;
        return group;
    }

    /// <summary>子选项多选一：同卡片内互斥，选中项高亮。</summary>
    public void SelectSubOption(ProfileModSubOptionChoiceViewModel? choice)
    {
        if (choice == null) return;
        foreach (var card in OptionCards)
        {
            if (card.SubOptions.Contains(choice))
            {
                foreach (var sub in card.SubOptions)
                {
                    sub.IsSelected = ReferenceEquals(sub, choice);
                }
                break;
            }
        }
        SaveState();
    }

    /// <summary>v0 互斥下拉：列表内单选。</summary>
    public void SelectLegacyChoice(ProfileModLegacyChoiceViewModel? choice)
    {
        if (choice == null) return;
        foreach (var item in LegacyChoices)
        {
            item.IsSelected = ReferenceEquals(item, choice);
        }
        SaveState();
    }

    public void Close() => _owner.CloseModOptions();

    /// <summary>
    /// 卡片图片点击：仅在需要时按原图尺寸解码，关闭预览或弹窗时释放；
    /// 没有原图路径时回退到已解码的小图。这样打开带 4K 预览图的 HD2IAS 选项不会一次性占用数百 MiB。
    /// </summary>
    public void ShowImagePreview(Bitmap? image, string? title)
    {
        if (image == null) return;
        if (FindImageHolder(image) is { } holder && !string.IsNullOrEmpty(holder.ImagePath))
        {
            _ = ShowOriginalPreviewAsync(holder, title);
            return;
        }

        _owner.ShowImagePreview(image, title);
    }

    private async Task ShowOriginalPreviewAsync(ProfileModImageHolderViewModel holder, string? title)
    {
        CancelOriginalPreviewLoad();
        var cancellation = new CancellationTokenSource();
        _originalPreviewLoad = cancellation;
        try
        {
            var hash = holder.ImagePath is { Length: > 0 } path
                ? await ModImageCache.GetContentHashAsync(path, cancellation.Token)
                : null;

            // 内容相同的另一份副本（例如 HD2IAS 组件目录里的同一张预览图）直接复用已解码原图。
            if (hash != null && _originalPreview is { } shared
                && string.Equals(hash, _originalPreviewHash, StringComparison.Ordinal))
            {
                if (!_disposed && !cancellation.IsCancellationRequested) _owner.ShowImagePreview(shared, title);
                return;
            }

            var original = await holder.LoadOriginalImageAsync(cancellation.Token);
            if (original == null)
            {
                if (!_disposed) _owner.ShowImagePreview(holder.Image, title);
                return;
            }

            // 解码期间弹窗可能已关闭；此时立即释放，不把原图挂到已销毁的弹窗上。
            if (_disposed || holder.IsDisposed || cancellation.IsCancellationRequested)
            {
                original.Dispose();
                return;
            }

            ReleaseOriginalPreview();
            _originalPreview = original;
            _originalPreviewHash = hash;
            _owner.ShowImagePreview(original, title);
        }
        catch (OperationCanceledException)
        {
            // 弹窗关闭或切换到另一张图片时放弃本次加载。
        }
    }

    private ProfileModImageHolderViewModel? FindImageHolder(Bitmap image)
        => GroupCards.Cast<ProfileModImageHolderViewModel>()
            .Concat(OptionCards)
            .Concat(OptionCards.SelectMany(card => card.SubOptions))
            .FirstOrDefault(holder => ReferenceEquals(holder.Image, image));

    /// <summary>释放覆盖层中的原图预览；覆盖层正显示它时先关闭覆盖层。</summary>
    private void ReleaseOriginalPreview()
    {
        var original = _originalPreview;
        _originalPreview = null;
        _originalPreviewHash = null;
        if (original == null) return;
        if (_owner.IsImagePreviewShowing(original)) _owner.CloseImagePreview();
        original.Dispose();
    }

    private void CancelOriginalPreviewLoad()
    {
        var cancellation = _originalPreviewLoad;
        _originalPreviewLoad = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    /// <summary>把覆盖层当前的选择收集为可持久化状态。</summary>
    internal ProfileModOptionsState BuildOptionsState()
    {
        var state = new ProfileModOptionsState();
        if (IsLegacyMode)
        {
            for (var i = 0; i < LegacyChoices.Count; i++)
            {
                if (LegacyChoices[i].IsSelected)
                {
                    state.LegacySelectedIndex = i;
                    break;
                }
            }

            if (state.LegacySelectedIndex is null && LegacyChoices.Count > 0)
            {
                state.LegacySelectedIndex = 0;
            }
        }
        else
        {
            state.OptionToggles = OptionCards.Select(card => card.IsEnabled).ToList();
            state.OptionSelections = OptionCards.Select(card =>
            {
                for (var s = 0; s < card.SubOptions.Count; s++)
                {
                    if (card.SubOptions[s].IsSelected)
                    {
                        return s;
                    }
                }

                return 0;
            }).ToList();
        }

        return state;
    }

    private void SaveState()
    {
        TargetMod.StoredOptions = BuildOptionsState();
        _ = _owner.PersistModOptionsAsync(TargetMod);
    }

    /// <summary>后台加载组件/选项/子选项预览图；关闭弹窗时取消并释放已解码位图。</summary>
    public async Task LoadImagesAsync()
    {
        _imageLoad?.Cancel();
        _imageLoad?.Dispose();
        var cancellation = new CancellationTokenSource();
        _imageLoad = cancellation;
        var token = cancellation.Token;
        try
        {
            foreach (var group in GroupCards)
            {
                await group.LoadImageAsync(token);
            }
            foreach (var card in OptionCards)
            {
                await card.LoadImageAsync(token);
                foreach (var sub in card.SubOptions)
                {
                    await sub.LoadImageAsync(token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>关闭弹窗后释放本弹窗解码的预览图（含按需解码的原图），避免反复打开累积原生位图内存。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelOriginalPreviewLoad();
        ReleaseOriginalPreview();

        // 内容相同的卡片共享同一份位图：按实例去重后统一释放，不能逐个卡片重复 Dispose。
        var images = new HashSet<Bitmap>(ReferenceEqualityComparer.Instance);
        foreach (var card in OptionCards)
        {
            if (card.Image is { } image) images.Add(image);
            foreach (var sub in card.SubOptions)
                if (sub.Image is { } subImage) images.Add(subImage);
        }
        foreach (var group in GroupCards)
            if (group.Image is { } groupImage) images.Add(groupImage);

        var cancellation = _imageLoad;
        _imageLoad = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
        foreach (var card in OptionCards) card.Dispose();
        foreach (var group in GroupCards) group.Dispose();
        foreach (var image in images)
        {
            ModImageCache.Forget(image);
            image.Dispose();
        }
    }

    /// <summary>预览覆盖层当前展示的是否是本弹窗持有的位图。</summary>
    internal bool OwnsImage(Bitmap? image)
        => image != null && (OptionCards.Any(card => ReferenceEquals(card.Image, image)
            || card.SubOptions.Any(sub => ReferenceEquals(sub.Image, image)))
            || GroupCards.Any(group => ReferenceEquals(group.Image, image)));

    /// <summary>把清单里的相对图片路径解析为模组目录内的绝对路径（越出模组目录或文件缺失返回 null）。</summary>
    internal static string? ResolveImagePath(string modPath, string? relativeImage)
    {
        if (string.IsNullOrWhiteSpace(relativeImage) || string.IsNullOrEmpty(modPath))
        {
            return null;
        }

        try
        {
            var fullRoot = Path.GetFullPath(modPath);
            var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativeImage));
            if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return File.Exists(fullPath) ? fullPath : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// 选项/组件卡片共用的预览图持有者：后台解码、关闭弹窗时取消并释放位图，
/// 防止反复打开选项弹窗后原生位图内存持续累积。
/// </summary>
public abstract partial class ProfileModImageHolderViewModel : ObservableObject, IDisposable
{
    [ObservableProperty]
    private Bitmap? _image;

    internal string? ImagePath { get; set; }

    /// <summary>
    /// 卡片实际显示尺寸只有 40～64 像素；按原图尺寸解码时，一张 4K 预览图就要占约 32 MiB 原生内存，
    /// 打开带预览图的 HD2IAS 选项弹窗会瞬间抬升数百 MiB。统一按 128 像素宽解码（2x 屏足够清晰）。
    /// </summary>
    internal const int CardImageDecodeWidth = 128;

    private bool _disposed;

    public bool IsDisposed => _disposed;

    public bool HasImage => Image != null;

    partial void OnImageChanged(Bitmap? value) => OnPropertyChanged(nameof(HasImage));

    public async Task LoadImageAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || Image != null || string.IsNullOrEmpty(ImagePath)) return;
        Bitmap? bitmap;
        try
        {
            // 同一路径或内容哈希相同的副本复用同一张解码位图，避免反复解码同一张图。
            bitmap = await ModImageCache.DecodeSharedAsync(ImagePath, CardImageDecodeWidth, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            // 预览图缺失或损坏时保持无图占位
            return;
        }
        // 解码期间弹窗可能已关闭；共享位图由弹窗统一释放，这里只丢弃引用。
        if (_disposed || cancellationToken.IsCancellationRequested) return;
        Image = bitmap;
    }

    /// <summary>按需解码原图供覆盖层预览；调用方负责在预览关闭后释放。</summary>
    public async Task<Bitmap?> LoadOriginalImageAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrEmpty(ImagePath)) return null;
        var path = ImagePath;
        try
        {
            return await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return new Bitmap(stream);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 原图不可读时由调用方回退到已加载的小图。
            return null;
        }
    }

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 相同内容的卡片共享同一张位图，不能各自 Dispose；由弹窗按实例去重后统一释放。
        Image = null;
    }
}

/// <summary>v1 选项卡片：独立开关 + 可选的子选项（多选一）。</summary>
public partial class ProfileModOptionCardViewModel : ProfileModImageHolderViewModel
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private bool _isEnabled;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public IReadOnlyList<ProfileModSubOptionChoiceViewModel> SubOptions { get; init; } =
        Array.Empty<ProfileModSubOptionChoiceViewModel>();

    public bool HasSubOptions => SubOptions.Count > 0;

    /// <summary>所属组件分组（HD2IAS 导入生成）；null 表示单级选项。</summary>
    public string? GroupId { get; init; }

    public override void Dispose()
    {
        if (IsDisposed) return;
        foreach (var sub in SubOptions) sub.Dispose();
        base.Dispose();
    }
}

/// <summary>一级组件卡片（HD2IAS 角色包）：进入二级页配置该组件的选项与子选项。</summary>
public partial class ProfileModOptionGroupViewModel : ProfileModImageHolderViewModel
{
    public string Id { get; init; } = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private bool _isEnabled;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public IReadOnlyList<ProfileModOptionCardViewModel> Options { get; init; } =
        Array.Empty<ProfileModOptionCardViewModel>();

    public bool HasSubOptions => Options.Any(option => option.SubOptions.Count > 0);

    /// <summary>只有一个无变体选项时直接在一级开关，无需进入二级页。</summary>
    public bool CanOpen => Options.Count > 1 || HasSubOptions;
}

/// <summary>v1 选项内的子选项（多选一变体），带预览图。</summary>
public partial class ProfileModSubOptionChoiceViewModel : ProfileModImageHolderViewModel
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private bool _isSelected;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}

/// <summary>v0 模组的互斥选项（单选列表项）。</summary>
public partial class ProfileModLegacyChoiceViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isSelected;
}

public partial class LibraryModChoiceViewModel : ObservableObject
{
    [ObservableProperty] private Guid _id;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _version = string.Empty;
    [ObservableProperty] private string _author = string.Empty;
}

public enum ProfileCompareStatus
{
    Common,
    OnlyLeft,
    OnlyRight,
    EnabledStateDifferent
}

public partial class ProfileCompareItemViewModel : ObservableObject
{
    [ObservableProperty] private Guid _modId;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _leftState = "-";
    [ObservableProperty] private string _rightState = "-";
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private ProfileCompareStatus _status;
    [ObservableProperty] private bool _isVisible = true;
}
