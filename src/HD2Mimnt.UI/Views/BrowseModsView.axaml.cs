using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

public partial class BrowseModsView : UserControl
{
    public BrowseModsView()
    {
        InitializeComponent();
        DetachedFromVisualTree += async (_, _) =>
        {
            if (DataContext is BrowseModsViewModel vm) await vm.StopAsync();
        };
    }

    private async void Cover_Tapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is BrowseModsViewModel vm && sender is Control { DataContext: BrowseModCardViewModel card })
        {
            e.Handled = true;
            await vm.PreviewCoverCommand.ExecuteAsync(card.Info);
        }
    }

    private async void Download_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BrowseModsViewModel { CanDownload: true } vm || TopLevel.GetTopLevel(this) is not { } owner) return;
        if (vm.UseBrowserDownload) { vm.OpenBrowserDownload(); return; }
        var selected = vm.SelectedFile;
        var details = vm.Details;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = UiLocalization.Current.Translate("保存模组压缩包"), SuggestedFileName = vm.SuggestedFileName,
            FileTypeChoices = new[] { FilePickerFileTypes.All }
        });
        if (vm.CanDownload && !vm.UseBrowserDownload && ReferenceEquals(details, vm.Details) && ReferenceEquals(selected, vm.SelectedFile) &&
            file?.TryGetLocalPath() is { } path)
            await vm.DownloadAsync(path);
    }

    private async void ChooseBrowserFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not BrowseModsViewModel { IsWaitingForBrowser: true } vm || TopLevel.GetTopLevel(this) is not { } owner) return;
        var selected = vm.SelectedFile;
        var completion = vm.Completion;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiLocalization.Current["BrowserDownload.ChooseFile"], AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(UiLocalization.Current.Translate("模组压缩包"))
                { Patterns = ["*.zip", "*.7z", "*.rar", "*.tar", "*.gz", "*.bz2", "*.xz"] }]
        });
        if (vm.IsWaitingForBrowser && ReferenceEquals(completion, vm.Completion) && ReferenceEquals(selected, vm.SelectedFile) && files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            await vm.SelectBrowserFileAsync(path);
    }
}
