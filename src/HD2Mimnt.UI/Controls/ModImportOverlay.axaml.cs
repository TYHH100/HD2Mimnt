using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Controls;

public partial class ModImportOverlay : UserControl
{
    private bool _selecting;

    public ModImportOverlay() => InitializeComponent();

    private async void OnSelectArchives(object? sender, RoutedEventArgs e) => await SelectAsync(false);
    private async void OnSelectFolder(object? sender, RoutedEventArgs e) => await SelectAsync(true);

    private async Task SelectAsync(bool folders)
    {
        if (_selecting || DataContext is not LibraryViewModel vm || vm.Import.IsImporting) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;
        _selecting = true;
        try
        {
            IEnumerable<IStorageItem> items;
            if (folders)
            {
                items = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "选择模组文件夹", AllowMultiple = true
                });
            }
            else
            {
                items = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "选择模组压缩包", AllowMultiple = true,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("模组压缩包") { Patterns = new[] { "*.zip", "*.rar", "*.7z", "*.tar", "*.gz", "*.tgz", "*.bz2", "*.tbz2", "*.xz" } },
                        new FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
                    }
                });
            }
            var paths = items.Select(item => item.TryGetLocalPath())
                .Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>();
            await vm.ImportPathsAsync(paths);
        }
        finally { _selecting = false; }
    }
}
