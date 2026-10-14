using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

public partial class CrashDiagnosticsView : UserControl
{
    public CrashDiagnosticsView() => InitializeComponent();

    private async void OnBrowseClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not CrashDiagnosticsViewModel { IsBusy: false } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiLocalization.Current["Crash.Browse"], AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Minidump") { Patterns = ["*.dmp", "*.mdmp"] }, FilePickerFileTypes.All]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null && !vm.IsBusy) vm.DumpPath = path;
    }

    private async void OnExportClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not CrashDiagnosticsViewModel { IsBusy: false, HasReport: true } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = UiLocalization.Current["Crash.Export"], SuggestedFileName = "HD2-crash-report.json", DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }], ShowOverwritePrompt = true
        });
        var path = file?.TryGetLocalPath();
        if (path != null) await vm.ExportAsync(path);
    }
}
