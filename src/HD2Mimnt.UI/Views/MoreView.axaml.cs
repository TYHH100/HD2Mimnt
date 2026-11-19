using HD2Mimnt.UI.Localization;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HD2Mimnt.UI.ViewModels;
namespace HD2Mimnt.UI.Views;
public partial class MoreView : UserControl
{
    public MoreView() => InitializeComponent();

    private async void OnExportCandidateClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Hd2ClCandidateItem candidate } button ||
            DataContext is not MainWindowViewModel mainViewModel ||
            TopLevel.GetTopLevel(button) is not Window owner)
        {
            return;
        }

        var viewModel = mainViewModel.Hd2ClViewModel;
        if (viewModel is not { IsBusy: false }) return;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = UiLocalization.Current["UI.Hd2ClExportPickTitle"],
            AllowMultiple = false,
            SuggestedStartLocation = string.IsNullOrWhiteSpace(viewModel.SuggestedExportDirectory)
                ? null
                : await owner.StorageProvider.TryGetFolderFromPathAsync(viewModel.SuggestedExportDirectory)
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            await viewModel.ExportAsync(candidate, path);
    }

    private async void OnLaunchToolClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ExternalToolItemViewModel tool } button ||
            DataContext is not MainWindowViewModel mainViewModel ||
            TopLevel.GetTopLevel(button) is not Window owner)
        {
            return;
        }

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiLocalization.Current.Translate($"选择要交给 {tool.Name} 打开的文件"),
            AllowMultiple = false
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await mainViewModel.ExternalToolsViewModel.LaunchToolAsync(tool.Id, path);
        }
    }
}
