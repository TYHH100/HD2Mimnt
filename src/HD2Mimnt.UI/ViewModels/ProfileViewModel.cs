using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Avalonia.Media.Imaging;
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

    [ObservableProperty]
    private ObservableCollection<ProfileModViewModel> _profileMods = new();

    /// <summary>搜索过滤后的模组卡片列表，与 ProfileMods 共享同一组实例。</summary>
    [ObservableProperty]
    private ObservableCollection<ProfileModViewModel> _visibleProfileMods = new();

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
    public bool IsProfileModsContentVisible => HasSelectedProfile && !IsProfileModsLoading && !IsProfileModsEmpty && !HasProfileModsError && !HasNoModResults;
    public bool IsProfileModsEmptyStateVisible => HasSelectedProfile && !IsProfileModsLoading && IsProfileModsEmpty && !HasProfileModsError;
    public bool IsProfileModsLoadingStateVisible => HasSelectedProfile && IsProfileModsLoading;
    public bool IsProfileModsErrorStateVisible => HasSelectedProfile && !IsProfileModsLoading && HasProfileModsError;
    public bool HasNoAvailableMods => AvailableLibraryMods.Count == 0;

    public ProfileViewModel(ProfileService profileService, ModService modService, NotificationService notificationService, IConfirmationService confirmationService, SettingsService settingsService, ImagePreviewService? imagePreview = null)
    {
        _profileService = profileService;
        _modService = modService;
        _notificationService = notificationService;
        _confirmationService = confirmationService;
        _settingsService = settingsService;
        _imagePreview = imagePreview;
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

    partial void OnIsProfileModsLoadingChanged(bool value) => RefreshProfileModStateProperties();

    partial void OnIsProfileModsEmptyChanged(bool value) => RefreshProfileModStateProperties();

    partial void OnHasProfileModsErrorChanged(bool value) => RefreshProfileModStateProperties();

    partial void OnHasNoModResultsChanged(bool value) => RefreshProfileModStateProperties();

    partial void OnAvailableLibraryModsChanged(ObservableCollection<LibraryModChoiceViewModel> value)
        => OnPropertyChanged(nameof(HasNoAvailableMods));

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
        var items = string.IsNullOrEmpty(query)
            ? ProfileMods.ToList()
            : ProfileMods.Where(mod =>
                mod.ModName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                mod.ModVersion.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        VisibleProfileMods = new ObservableCollection<ProfileModViewModel>(items);
        HasNoModResults = ProfileMods.Count > 0 && VisibleProfileMods.Count == 0;
    }

    partial void OnSelectedProfileChanged(ProfileItemViewModel? value)
    {
        IsAddModOpen = false;
        AvailableLibraryMods.Clear();
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
                        ModVersion = mod.Version,
                        Enabled = profileMod.Enabled,
                        Order = profileMod.Order,
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
            RenumberProfileMods();
            ApplyModFilter();
            IsProfileModsEmpty = ProfileMods.Count == 0;
            _ = LoadProfileModIconsAsync();
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
    private async Task LoadProfileModIconsAsync()
    {
        foreach (var mod in ProfileMods.Take(80))
        {
            await mod.LoadIconAsync();
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
                    Version = mod.Version,
                    Author = mod.Author
                })
                .ToList();

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

    [RelayCommand]
    private void OpenModOptions(ProfileModViewModel? profileMod)
    {
        if (profileMod == null || !profileMod.HasOptions) return;

        ModOptionsDialog = new ProfileModOptionsDialogViewModel(this, profileMod);
        IsModOptionsDialogOpen = true;
        _ = ModOptionsDialog.LoadImagesAsync();
    }

    [RelayCommand]
    public void CloseModOptions()
    {
        IsModOptionsDialogOpen = false;
        ModOptionsDialog = null;
    }

    /// <summary>点击图片（模组图标/选项图/子选项图）：在主窗口顶层的覆盖层预览原图。</summary>
    public void ShowImagePreview(Bitmap? image, string? title) => _imagePreview?.Show(image, title);

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
            ProfileMods.Remove(profileMod);
            SelectedProfile.ModCount = Math.Max(0, SelectedProfile.ModCount - 1);
            RenumberProfileMods();
            ApplyModFilter();
            IsProfileModsEmpty = ProfileMods.Count == 0;
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
        if (ProfileMods.Count == 0) return;

        var movingSet = movingModIds.Distinct().ToHashSet();
        if (movingSet.Count == 0 || movingSet.Count == ProfileMods.Count) return;
        if (ProfileMods.All(mod => !movingSet.Contains(mod.ModId))) return;

        var moving = ProfileMods.Where(mod => movingSet.Contains(mod.ModId)).ToList();
        var others = ProfileMods.Where(mod => !movingSet.Contains(mod.ModId)).ToList();

        // 落点换算为"anchor 之前的非拖动项数量"，anchor 本身也在拖动集中时视为放到末尾
        var insertIndex = others.Count;
        if (anchorModId is { } anchorId)
        {
            var anchorPos = -1;
            for (var i = 0; i < ProfileMods.Count; i++)
            {
                if (ProfileMods[i].ModId == anchorId)
                {
                    anchorPos = i;
                    break;
                }
            }
            if (anchorPos >= 0)
            {
                insertIndex = ProfileMods.Take(anchorPos).Count(mod => !movingSet.Contains(mod.ModId));
            }
        }

        var newOrder = new List<ProfileModViewModel>(ProfileMods.Count);
        newOrder.AddRange(others.Take(insertIndex));
        newOrder.AddRange(moving);
        newOrder.AddRange(others.Skip(insertIndex));

        if (newOrder.Select(mod => mod.ModId).SequenceEqual(ProfileMods.Select(mod => mod.ModId))) return;

        ProfileMods = new ObservableCollection<ProfileModViewModel>(newOrder);
        RenumberProfileMods();
        ApplyModFilter();

        try
        {
            await SuppressLocalChangeAsync(() =>
                _profileService.ReorderModsAsync(
                    SelectedProfile.Id, ProfileMods.Select(mod => mod.ModId).ToList()));
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"保存部署顺序失败: {ex.Message}", "配置文件");
            await LoadProfileModsAsync(SelectedProfile.Id);
        }
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
    private bool _isSelected;

    /// <summary>模组库目录，供“打开模组目录”使用。</summary>
    [ObservableProperty]
    private string _modPath = string.Empty;

    [ObservableProperty]
    private string _thumbnailPath = string.Empty;

    [ObservableProperty]
    private Bitmap? _icon;

    private bool _isLoadingIcon;

    public string PositionText => Position.ToString("00");

    partial void OnPositionChanged(int value) => OnPropertyChanged(nameof(PositionText));

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

    /// <summary>与库页 ModItemViewModel 相同的图标加载模式；失败保持 null 以显示序号占位。</summary>
    public async Task LoadIconAsync(CancellationToken cancellationToken = default)
    {
        if (Icon != null || _isLoadingIcon) return;
        if (string.IsNullOrEmpty(ThumbnailPath)) return;

        _isLoadingIcon = true;
        try
        {
            var bitmap = await Task.Run(() =>
            {
                if (File.Exists(ThumbnailPath))
                {
                    return new Bitmap(ThumbnailPath);
                }

                return (Bitmap?)null;
            }, cancellationToken);

            if (bitmap != null)
            {
                Icon = bitmap;
            }
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

/// <summary>
/// 模组选项覆盖层（应用内模态弹窗）：展示 v1 选项卡片（独立开关 + 带图片的子选项多选一）
/// 或 v0 互斥选择列表。任何改动立即经 ProfileViewModel 持久化到当前 Profile。
/// </summary>
public partial class ProfileModOptionsDialogViewModel : ObservableObject
{
    private readonly ProfileViewModel _owner;

    public ProfileModViewModel TargetMod { get; }

    /// <summary>v0 模组为 true：展示互斥选择列表而不是独立开关卡片。</summary>
    public bool IsLegacyMode { get; }

    public IReadOnlyList<ProfileModLegacyChoiceViewModel> LegacyChoices { get; }

    public IReadOnlyList<ProfileModOptionCardViewModel> OptionCards { get; }

    public string Title => TargetMod.ModName;

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
                    SubOptions = subChoices
                });
            }

            OptionCards = cards;
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
        SaveState();
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

    /// <summary>后台加载选项/子选项预览图；加载完成逐个刷新绑定。</summary>
    public async Task LoadImagesAsync()
    {
        foreach (var card in OptionCards)
        {
            await card.LoadImageAsync();
            foreach (var sub in card.SubOptions)
            {
                await sub.LoadImageAsync();
            }
        }
    }

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

/// <summary>v1 选项卡片：独立开关 + 可选的子选项（多选一）。</summary>
public partial class ProfileModOptionCardViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private Bitmap? _image;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public bool HasImage => Image != null;

    partial void OnImageChanged(Bitmap? value) => OnPropertyChanged(nameof(HasImage));

    public IReadOnlyList<ProfileModSubOptionChoiceViewModel> SubOptions { get; init; } =
        Array.Empty<ProfileModSubOptionChoiceViewModel>();

    public bool HasSubOptions => SubOptions.Count > 0;

    internal string? ImagePath { get; set; }

    public async Task LoadImageAsync()
    {
        if (Image != null || ImagePath == null) return;
        try
        {
            Image = await Task.Run(() => new Bitmap(ImagePath));
        }
        catch
        {
            // 预览图缺失或损坏时保持无图占位
        }
    }
}

/// <summary>v1 选项内的子选项（多选一变体），带预览图。</summary>
public partial class ProfileModSubOptionChoiceViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private Bitmap? _image;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public bool HasImage => Image != null;

    partial void OnImageChanged(Bitmap? value) => OnPropertyChanged(nameof(HasImage));

    internal string? ImagePath { get; set; }

    public async Task LoadImageAsync()
    {
        if (Image != null || ImagePath == null) return;
        try
        {
            Image = await Task.Run(() => new Bitmap(ImagePath));
        }
        catch
        {
            // 预览图缺失或损坏时保持无图占位
        }
    }
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
