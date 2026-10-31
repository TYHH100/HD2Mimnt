using CommunityToolkit.Mvvm.Input;

namespace HD2Mimnt.UI.ViewModels;

public partial class LibraryViewModel
{
    public AudioPreviewViewModel? AudioPreview { get; }

    [RelayCommand]
    private Task PreviewAudioAsync(ModItemViewModel? mod) => mod is null || AudioPreview is null
        ? Task.CompletedTask : AudioPreview.OpenAsync(mod.Id);
}
