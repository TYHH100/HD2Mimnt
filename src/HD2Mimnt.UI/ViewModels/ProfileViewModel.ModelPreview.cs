using CommunityToolkit.Mvvm.Input;

namespace HD2Mimnt.UI.ViewModels;

public partial class ProfileViewModel
{
    public ModelPreviewViewModel? ModelPreview { get; }

    [RelayCommand]
    private Task PreviewModelAsync(ProfileModViewModel? mod) => mod is null || mod.IsSeparator || ModelPreview is null || SelectedProfile is null
        ? Task.CompletedTask : ModelPreview.OpenAsync(mod.ModId, SelectedProfile.Id);
}
