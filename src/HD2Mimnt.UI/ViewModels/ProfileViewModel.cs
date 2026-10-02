using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using System.Collections.ObjectModel;

namespace HD2Mimnt.UI.ViewModels;

public partial class ProfileViewModel : ObservableObject
{
    private readonly ProfileService _profileService;
    private readonly ModService _modService;

    [ObservableProperty]
    private ObservableCollection<ProfileItemViewModel> _profiles = new();

    [ObservableProperty]
    private ProfileItemViewModel? _selectedProfile;

    [ObservableProperty]
    private ObservableCollection<ProfileModViewModel> _profileMods = new();

    [ObservableProperty]
    private bool _isLoading;

    public ProfileViewModel(ProfileService profileService, ModService modService)
    {
        _profileService = profileService;
        _modService = modService;
    }

    public async Task LoadProfilesAsync()
    {
        IsLoading = true;
        try
        {
            var profiles = await _profileService.GetAllProfilesAsync();
            Profiles.Clear();
            foreach (var profile in profiles)
            {
                Profiles.Add(new ProfileItemViewModel(profile));
            }

            // 选中激活的 Profile
            var activeProfile = Profiles.FirstOrDefault(p => p.IsActive);
            if (activeProfile != null)
            {
                SelectedProfile = activeProfile;
                await LoadProfileModsAsync(activeProfile.Id);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSelectedProfileChanged(ProfileItemViewModel? value)
    {
        if (value != null)
        {
            _ = LoadProfileModsAsync(value.Id);
        }
    }

    private async Task LoadProfileModsAsync(Guid profileId)
    {
        var profile = await _profileService.GetProfileByIdAsync(profileId);
        if (profile == null) return;

        ProfileMods.Clear();
        foreach (var profileMod in profile.Mods.OrderBy(m => m.Order))
        {
            var mod = await _modService.GetModByIdAsync(profileMod.ModId);
            if (mod != null)
            {
                ProfileMods.Add(new ProfileModViewModel
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
    }

    [RelayCommand]
    private async Task CreateProfileAsync()
    {
        // TODO: 打开创建 Profile 对话框
        var name = $"New Profile {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        await _profileService.CreateProfileAsync(name);
        await LoadProfilesAsync();
    }

    [RelayCommand]
    private async Task DeleteProfileAsync(ProfileItemViewModel? profile)
    {
        if (profile == null) return;

        // TODO: 确认对话框

        await _profileService.DeleteProfileAsync(profile.Id);
        Profiles.Remove(profile);
    }

    [RelayCommand]
    private async Task DuplicateProfileAsync(ProfileItemViewModel? profile)
    {
        if (profile == null) return;

        var newName = $"{profile.Name} (Copy)";
        await _profileService.DuplicateProfileAsync(profile.Id, newName);
        await LoadProfilesAsync();
    }

    [RelayCommand]
    private async Task SetActiveProfileAsync(ProfileItemViewModel? profile)
    {
        if (profile == null) return;

        await _profileService.SetActiveProfileAsync(profile.Id);

        // 更新所有 Profile 的激活状态
        foreach (var p in Profiles)
        {
            p.IsActive = p.Id == profile.Id;
        }
    }

    [RelayCommand]
    private async Task ToggleModEnabledAsync(ProfileModViewModel? profileMod)
    {
        if (profileMod == null || SelectedProfile == null) return;

        profileMod.Enabled = !profileMod.Enabled;
        await _profileService.SetModEnabledAsync(SelectedProfile.Id, profileMod.ModId, profileMod.Enabled);
    }

    [RelayCommand]
    private async Task RemoveModFromProfileAsync(ProfileModViewModel? profileMod)
    {
        if (profileMod == null || SelectedProfile == null) return;

        await _profileService.RemoveModFromProfileAsync(SelectedProfile.Id, profileMod.ModId);
        ProfileMods.Remove(profileMod);
    }
}

public partial class ProfileItemViewModel : ObservableObject
{
    private readonly Profile _profile;

    public Guid Id => _profile.Id;

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
