using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

public partial class IasSystemView : UserControl
{
    public IasSystemView() => InitializeComponent();

    private async void OnBrowseClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not IasSystemViewModel { IsBusy: false } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = UiLocalization.Current["IasSystem.Browse"], AllowMultiple = false
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path && !vm.IsBusy) vm.AcceptLocation(path);
    }
}
