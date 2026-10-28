using CommunityToolkit.Mvvm.Input;

namespace HD2Mimnt.UI.ViewModels;

public partial class LibraryViewModel
{
    public ModelPreviewViewModel? ModelPreview { get; }

    [RelayCommand]
    private Task PreviewModelAsync(ModItemViewModel? mod) => mod is null || ModelPreview is null
        ? Task.CompletedTask : ModelPreview.OpenAsync(mod.Id);
}
