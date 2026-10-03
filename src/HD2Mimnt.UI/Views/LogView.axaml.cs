using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

public partial class LogView : UserControl
{
    public LogView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            if (DataContext is LogViewModel viewModel)
            {
                viewModel.ExportRequested -= OnExportRequested;
                viewModel.ExportRequested += OnExportRequested;
                viewModel.CopyRequested -= OnCopyRequested;
                viewModel.CopyRequested += OnCopyRequested;
            }
        };
    }

    private async void OnExportRequested(object? sender, EventArgs e)
    {
        if (DataContext is not LogViewModel viewModel || TopLevel.GetTopLevel(this) is not Window owner) return;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出应用日志",
            SuggestedFileName = $"hd2mimnt-log-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            FileTypeChoices = new[] { new FilePickerFileType("日志文件") { Patterns = new[] { "*.log", "*.txt" } } }
        });
        var path = file?.Path.LocalPath;
        if (!string.IsNullOrWhiteSpace(path)) await viewModel.ExportAsync(path);
    }

    private async void OnCopyRequested(object? sender, EventArgs e)
    {
        if (DataContext is not LogViewModel viewModel || TopLevel.GetTopLevel(this) is not TopLevel owner || owner.Clipboard is null) return;
        await owner.Clipboard.SetTextAsync(viewModel.GetRecentText());
    }
}
