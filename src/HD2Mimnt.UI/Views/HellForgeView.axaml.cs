using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

public partial class HellForgeView : UserControl
{
    public HellForgeView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            if (DataContext is HellForgeViewModel viewModel)
            {
                viewModel.ExportLogsRequested -= OnExportLogsRequested;
                viewModel.ExportLogsRequested += OnExportLogsRequested;
                viewModel.CrashReportRequested -= OnCrashReportRequested;
                viewModel.CrashReportRequested += OnCrashReportRequested;
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (DataContext is HellForgeViewModel viewModel)
            {
                viewModel.ExportLogsRequested -= OnExportLogsRequested;
                viewModel.CrashReportRequested -= OnCrashReportRequested;
            }
        };
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
