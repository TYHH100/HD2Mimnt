using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using System.Collections.ObjectModel;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

public partial class ProfileViewModel : ObservableObject
{
    private readonly ProfileService _profileService;
    private readonly ModService _modService;
    private readonly NotificationService _notificationService;
    private readonly IConfirmationService _confirmationService;

    public event EventHandler? ActiveProfileChanged;

    [ObservableProperty]
    private ObservableCollection<ProfileItemViewModel> _profiles = new();

    [ObservableProperty]
    private ObservableCollection<ProfileItemViewModel> _visibleProfiles = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _newProfileName = string.Empty;

    [ObservableProperty]
    private ProfileItemViewModel? _selectedProfile;

    [ObservableProperty]
    private ObservableCollection<ProfileModViewModel> _profileMods = new();

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
    private bool _isEmpty;

    [ObservableProperty]
    private bool _hasNoResults;

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
    public bool IsProfileModsContentVisible => HasSelectedProfile && !IsProfileModsLoading && !IsProfileModsEmpty && !HasProfileModsError;
    public bool IsProfileModsEmptyStateVisible => HasSelectedProfile && !IsProfileModsLoading && IsProfileModsEmpty && !HasProfileModsError;
    public bool IsProfileModsLoadingStateVisible => HasSelectedProfile && IsProfileModsLoading;
    public bool IsProfileModsErrorStateVisible => HasSelectedProfile && !IsProfileModsLoading && HasProfileModsError;

    public ProfileViewModel(ProfileService profileService, ModService modService, NotificationService notificationService, IConfirmationService confirmationService)
    {
        _profileService = profileService;
        _modService = modService;
        _notificationService = notificationService;
        _confirmationService = confirmationService;
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
                Profiles.Add(new ProfileItemViewModel(profile));
            }
            ApplySearch();

            // 选中激活的 Profile
            var activeProfile = Profiles.FirstOrDefault(p => p.IsActive);
            if (activeProfile != null)
            {
                SelectedProfile = activeProfile;
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

    partial void OnSearchTextChanged(string value) => ApplySearch();

    partial void OnIsProfileModsLoadingChanged(bool value) => RefreshProfileModStateProperties();

    partial void OnIsProfileModsEmptyChanged(bool value) => RefreshProfileModStateProperties();

    partial void OnHasProfileModsErrorChanged(bool value) => RefreshProfileModStateProperties();

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
    private void ClearSearch() => SearchText = string.Empty;

    private void ApplySearch()
    {
        var query = SearchText.Trim();
        var result = string.IsNullOrEmpty(query)
            ? Profiles
            : new ObservableCollection<ProfileItemViewModel>(Profiles.Where(p =>
                p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Description.Contains(query, StringComparison.OrdinalIgnoreCase)));
        VisibleProfiles = result;
        IsEmpty = Profiles.Count == 0;
        HasNoResults = Profiles.Count > 0 && VisibleProfiles.Count == 0;
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
            _ = LoadProfileModsAsync(value.Id);
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
                    profileMods.Add(new ProfileModViewModel
                    {
                        ProfileId = profileMod.ProfileId,
                        ModId = profileMod.ModId,
                        ModName = mod.Name,
                        ModVersion = mod.Version,
                        Enabled = profileMod.Enabled,
                        Order = profileMod.Order
                    });
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            ProfileMods = new ObservableCollection<ProfileModViewModel>(profileMods);
            IsProfileModsEmpty = ProfileMods.Count == 0;
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
            await _profileService.AddModToProfileAsync(SelectedProfile.Id, mod.Id);
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
            await _profileService.CreateProfileAsync(name);
            NewProfileName = string.Empty;
            await LoadProfilesAsync();
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"创建失败: {ex.Message}", "新建配置文件");
        }
    }

    [RelayCommand]
    private async Task DeleteProfileAsync(ProfileItemViewModel? profile)
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
            await _profileService.DeleteProfileAsync(profile.Id);
            Profiles.Remove(profile);
            if (ReferenceEquals(SelectedProfile, profile))
            {
                SelectedProfile = Profiles.FirstOrDefault(item => item.IsActive) ?? Profiles.FirstOrDefault();
            }
            ApplySearch();
            _notificationService.ShowSuccess($"已删除配置文件“{profile.Name}”", "删除配置文件");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"删除失败: {ex.Message}", "删除配置文件");
        }
    }

    [RelayCommand]
    private async Task RenameProfileAsync(ProfileItemViewModel? profile)
    {
        if (profile == null) return;
        var name = profile.Name.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            await _profileService.RenameProfileAsync(profile.Id, name);
            await LoadProfilesAsync();
            _notificationService.ShowSuccess("配置文件名称已更新", "重命名配置文件");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"重命名失败: {ex.Message}", "重命名配置文件");
        }
    }

    [RelayCommand]
    private async Task DuplicateProfileAsync(ProfileItemViewModel? profile)
    {
        if (profile == null) return;

        var newName = $"{profile.Name} (Copy)";
        try
        {
            await _profileService.DuplicateProfileAsync(profile.Id, newName);
            await LoadProfilesAsync();
            _notificationService.ShowSuccess($"已复制配置文件为“{newName}”", "复制配置文件");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"复制失败: {ex.Message}", "复制配置文件");
        }
    }

    [RelayCommand]
    private async Task SetActiveProfileAsync(ProfileItemViewModel? profile)
    {
        if (profile == null) return;

        try
        {
            await _profileService.SetActiveProfileAsync(profile.Id);

            // 更新所有 Profile 的激活状态
            foreach (var p in Profiles)
            {
                p.IsActive = p.Id == profile.Id;
            }
            SelectedProfile = profile;
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
            await _profileService.SetModEnabledAsync(SelectedProfile.Id, profileMod.ModId, enabled);
            profileMod.Enabled = enabled;
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"更新模组状态失败: {ex.Message}", "配置文件");
        }
    }

    [RelayCommand]
    private async Task RemoveModFromProfileAsync(ProfileModViewModel? profileMod)
    {
        if (profileMod == null || SelectedProfile == null) return;

        try
        {
            await _profileService.RemoveModFromProfileAsync(SelectedProfile.Id, profileMod.ModId);
            ProfileMods.Remove(profileMod);
            SelectedProfile.ModCount = Math.Max(0, SelectedProfile.ModCount - 1);
            IsProfileModsEmpty = ProfileMods.Count == 0;
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"移除模组失败: {ex.Message}", "配置文件");
        }
    }
}

public partial class ProfileItemViewModel : ObservableObject
{
    private readonly Profile _profile;

    public Guid Id => _profile.Id;
    public DateTime ModifiedAt => _profile.ModifiedAt;

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
