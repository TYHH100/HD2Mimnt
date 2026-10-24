using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

public partial class DirectDownloadView : UserControl
{
    public DirectDownloadView() => InitializeComponent();

    private async void BrowseFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DirectDownloadViewModel { CanEdit: true } vm || TopLevel.GetTopLevel(this) is not { } owner) return;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = UiLocalization.Current["DirectDownload.ChooseFolder"], AllowMultiple = false
        });
        if (vm.CanEdit && folders.FirstOrDefault()?.TryGetLocalPath() is { } path) vm.Destination = path;
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DirectDownloadViewModel { HasSavedFile: true } vm) return;
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add("/select,"); start.ArgumentList.Add(vm.SavedPath);
            Process.Start(start);
        }
        catch (Exception ex) { vm.StatusMessage = ex.Message; }
    }

    private async void ImportBrowserFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DirectDownloadViewModel { CanEdit: true } vm || TopLevel.GetTopLevel(this) is not { } owner) return;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiLocalization.Current["DirectDownload.ImportBrowserFile"], AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(UiLocalization.Current["DirectDownload.DownloadedArchive"])
                { Patterns = ["*.zip", "*.7z", "*.rar"] }, FilePickerFileTypes.All]
        });
        if (vm.CanEdit && files.FirstOrDefault()?.TryGetLocalPath() is { } path) await vm.ImportBrowserFileAsync(path);
    }
}
