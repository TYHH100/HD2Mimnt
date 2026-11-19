using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

public partial class Hd2ClMmdView : UserControl
{
    public Hd2ClMmdView() => InitializeComponent();

    private async void OnPickFile(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MmdAdaptationViewModel { IsBusy: false } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiLocalization.Current["UI.Hd2ClMmdPickFile"],
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("MMD") { Patterns = ["*.zip", "*.7z", "*.patch_*"] },
                new FilePickerFileType("All") { Patterns = ["*.*"] }
            ]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) vm.SourcePath = path;
    }

    private async void OnPickFolder(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MmdAdaptationViewModel { IsBusy: false } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = UiLocalization.Current["UI.Hd2ClMmdPickFolder"],
            AllowMultiple = false
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) vm.SourcePath = path;
    }

    private async void OnExport(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not MmdAdaptationViewModel { IsBusy: false, CanConvert: true } vm ||
            TopLevel.GetTopLevel(this) is not { } top) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = UiLocalization.Current["UI.Hd2ClExportPickTitle"],
            AllowMultiple = false,
            SuggestedStartLocation = string.IsNullOrWhiteSpace(vm.SuggestedExportDirectory)
                ? null
                : await top.StorageProvider.TryGetFolderFromPathAsync(vm.SuggestedExportDirectory)
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) await vm.ExportAsync(path);
    }
}
