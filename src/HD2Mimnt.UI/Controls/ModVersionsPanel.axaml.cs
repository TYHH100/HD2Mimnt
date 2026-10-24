using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Controls;

public partial class ModVersionsPanel : UserControl
{
    public ModVersionsPanel() => InitializeComponent();

    private async void PickArchive(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ModVersionsViewModel { IsBusy: false } vm || TopLevel.GetTopLevel(this) is not { } owner) return;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiLocalization.Current["Versions.UpdateArchive"], AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Mod archives") { Patterns = new[] { "*.zip", "*.7z", "*.rar", "*.tar", "*.gz" } } }
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await vm.UpdateFromArchiveAsync(path);
    }
}
