using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.UI.Localization;
namespace HD2Mimnt.UI.ViewModels;
public partial class ProfileViewModel
{
    public ModCompatibilityViewModel? Compatibility { get; }
    [RelayCommand] private Task CheckProfileCompatibilityAsync() => Compatibility?.OpenAsync(ProfileMods.Where(m => !m.IsSeparator && m.SourceInfo != null).Select(m => m.SourceInfo!), UiLocalization.Current.Format("Compatibility.ProfileScope", SelectedProfile?.Name ?? "")) ?? Task.CompletedTask;
    [RelayCommand] private Task CheckSelectedCompatibilityAsync() => Compatibility?.OpenAsync(ProfileMods.Where(m => !m.IsSeparator && m.IsSelected && m.SourceInfo != null).Select(m => m.SourceInfo!), UiLocalization.Current["Compatibility.SelectionScope"]) ?? Task.CompletedTask;
    [RelayCommand] private Task CheckModCompatibilityAsync(ProfileModViewModel? mod) => mod is not { IsSeparator: false, SourceInfo: { } source } ? Task.CompletedTask : Compatibility?.OpenAsync([source], source.Name) ?? Task.CompletedTask;
}
