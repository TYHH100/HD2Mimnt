using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

public partial class GmodIasConversionView : UserControl
{
    public GmodIasConversionView() => InitializeComponent();

    private async void OnBrowseAddonFileClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not GmodIasConversionViewModel { CanEdit: true } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiLocalization.Current["GmodIas.BrowseAddon"], AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("GMAD addon") { Patterns = ["*.gma"] }, FilePickerFileTypes.All]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path && vm.CanEdit) vm.AcceptAddonPath(path);
    }

    private async void OnBrowseAddonFolderClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not GmodIasConversionViewModel { CanEdit: true } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = UiLocalization.Current["GmodIas.BrowseAddon"], AllowMultiple = false
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path && vm.CanEdit) vm.AcceptAddonPath(path);
    }

    private async void OnBrowseModelClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not GmodIasConversionViewModel { CanEdit: true } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        // 模型必须能按 addon 内相对路径解析；未选 addon 时先让用户选 addon（选完会自动列出模型）。
        if (string.IsNullOrWhiteSpace(vm.AddonPath))
        {
            vm.ReportAddonRequired();
            return;
        }
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiLocalization.Current["GmodIas.BrowseModel"], AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Source model") { Patterns = ["*.mdl"] }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path || !vm.CanEdit) return;
        // 目录 addon 需要 addon 内相对路径；GMA 是单一归档文件，无法从本地路径反推 addon 内路径。
        if (Directory.Exists(vm.AddonPath))
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(vm.AddonPath));
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                vm.ReportModelOutsideAddon();
                return;
            }
            vm.AcceptModelPath(Path.GetRelativePath(root, full).Replace('\\', '/'));
            return;
        }
        vm.ReportGmaManualPath();
    }

    private async void OnBrowseOutputClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not GmodIasConversionViewModel { CanEdit: true } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = UiLocalization.Current["GmodIas.BrowseOutput"], AllowMultiple = false
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path && vm.CanEdit) vm.AcceptOutputDirectory(path);
    }
}
