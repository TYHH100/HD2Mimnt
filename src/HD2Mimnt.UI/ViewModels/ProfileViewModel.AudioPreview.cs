using CommunityToolkit.Mvvm.Input;

namespace HD2Mimnt.UI.ViewModels;

public partial class ProfileViewModel
{
    public AudioPreviewViewModel? AudioPreview { get; }

    [RelayCommand]
    private Task PreviewAudioAsync(ProfileModViewModel? mod) => mod is null || mod.IsSeparator || AudioPreview is null || SelectedProfile is null
        ? Task.CompletedTask : AudioPreview.OpenAsync(mod.ModId, SelectedProfile.Id);
}
