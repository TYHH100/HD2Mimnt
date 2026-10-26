using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.UI.Localization;
namespace HD2Mimnt.UI.ViewModels;
public partial class LibraryViewModel
{
    public ModCompatibilityViewModel? Compatibility { get; }
    public Task CheckCompatibilityOnStartupAsync() => Compatibility?.CheckOnStartupAsync(_allMods, _notificationService) ?? Task.CompletedTask;
    public Task RestoreCompatibilityBadgesAsync() => Compatibility?.RestoreBadgesAsync() ?? Task.CompletedTask;
    [RelayCommand] private Task CheckLibraryCompatibilityAsync() => Compatibility?.OpenAsync(_allMods, UiLocalization.Current["Compatibility.LibraryScope"]) ?? Task.CompletedTask;
    [RelayCommand] private Task CheckSelectedCompatibilityAsync() => Compatibility?.OpenAsync(SelectedMods, UiLocalization.Current["Compatibility.SelectionScope"]) ?? Task.CompletedTask;
    [RelayCommand] private Task CheckModCompatibilityAsync(ModItemViewModel? mod) => mod == null ? Task.CompletedTask : Compatibility?.OpenAsync([mod], mod.Name) ?? Task.CompletedTask;
}
