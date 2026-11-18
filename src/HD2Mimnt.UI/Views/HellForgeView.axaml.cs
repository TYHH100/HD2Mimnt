using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

public partial class HellForgeView : UserControl
{
    private HellForgeViewModel? _subscribed;

    public HellForgeView()
    {
        InitializeComponent();
        // DataContext 可能晚于 AttachedToVisualTree 生效（TabControl 延迟实现内容），两处都要接。
        DataContextChanged += (_, _) => BindViewModel(DataContext as HellForgeViewModel);
        AttachedToVisualTree += (_, _) => BindViewModel(DataContext as HellForgeViewModel);
        DetachedFromVisualTree += (_, _) => BindViewModel(null);
    }

    private void BindViewModel(HellForgeViewModel? viewModel)
    {
        if (ReferenceEquals(_subscribed, viewModel)) return;
        if (_subscribed != null)
        {
            _subscribed.ExportLogsRequested -= OnExportLogsRequested;
            _subscribed.CrashReportRequested -= OnCrashReportRequested;
            _subscribed.AddModRequested -= OnAddModRequested;
        }
        _subscribed = viewModel;
        if (_subscribed != null)
        {
            _subscribed.ExportLogsRequested += OnExportLogsRequested;
            _subscribed.CrashReportRequested += OnCrashReportRequested;
            _subscribed.AddModRequested += OnAddModRequested;
        }
    }

    private async void OnAddModRequested(object? sender, EventArgs e)
    {
        if (DataContext is not HellForgeViewModel viewModel || TopLevel.GetTopLevel(this) is not Window owner) return;
        try
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = UiLocalization.Current.Translate("HellForge.Mods.Add"),
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(UiLocalization.Current.Translate("HellForge.Mods.ArchiveFile"))
                        { Patterns = new[] { "*.zip", "*.rar", "*.7z" } },
                    new FilePickerFileType(UiLocalization.Current.Translate("HellForge.Mods.ManifestFile"))
                        { Patterns = new[] { "*.json" } }
                }
            });
            var path = files.FirstOrDefault()?.Path.LocalPath;
            if (!string.IsNullOrWhiteSpace(path)) await viewModel.AddModAsync(path);
        }
        catch (Exception error)
        {
            viewModel.ReportUiError(error.Message);
        }
    }

    private async void OnAddModFolderClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not HellForgeViewModel viewModel || TopLevel.GetTopLevel(this) is not Window owner) return;
        try
        {
            var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = UiLocalization.Current.Translate("HellForge.Mods.AddFolder"),
                AllowMultiple = false
            });
            var folder = folders.FirstOrDefault()?.Path.LocalPath;
            if (!string.IsNullOrWhiteSpace(folder)) await viewModel.AddModFromFolderAsync(folder);
        }
        catch (Exception error)
        {
            viewModel.ReportUiError(error.Message);
        }
    }

    private void OnOpenModsFolderClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not HellForgeViewModel viewModel || string.IsNullOrEmpty(viewModel.ModsDirectory)) return;
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add(viewModel.ModsDirectory);
            Process.Start(start);
        }
        catch (Exception error)
        {
            viewModel.ReportUiError(error.Message);
        }
    }

    private async void OnExportLogsRequested(object? sender, EventArgs e)
    {
        if (DataContext is not HellForgeViewModel viewModel || TopLevel.GetTopLevel(this) is not Window owner) return;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = UiLocalization.Current.Translate("HellForge.Logs.Export"),
            SuggestedFileName = $"hellforge-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            FileTypeChoices = new[]
            {
                new FilePickerFileType(UiLocalization.Current.Translate("HellForge.Logs.ZipFile"))
                    { Patterns = new[] { "*.zip" } }
            }
        });
        var path = file?.Path.LocalPath;
        if (!string.IsNullOrWhiteSpace(path)) await viewModel.ExportLogsAsync(path);
    }

    private async void OnCrashReportRequested(object? sender, EventArgs e)
    {
        if (DataContext is not HellForgeViewModel viewModel || TopLevel.GetTopLevel(this) is not Window owner) return;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = UiLocalization.Current.Translate("HellForge.Logs.CrashReport"),
            SuggestedFileName = $"hellforge-crash-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            FileTypeChoices = new[]
            {
                new FilePickerFileType(UiLocalization.Current.Translate("HellForge.Logs.ZipFile"))
                    { Patterns = new[] { "*.zip" } }
            }
        });
        var path = file?.Path.LocalPath;
        if (!string.IsNullOrWhiteSpace(path)) await viewModel.CreateCrashReportAsync(path);
    }
}
