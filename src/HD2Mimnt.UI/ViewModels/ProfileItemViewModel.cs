using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.UI.ViewModels;

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
