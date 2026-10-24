using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

public partial class Hd2IASManagementView : UserControl
{
    public Hd2IASManagementView() => InitializeComponent();

    private async void OnBrowseClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not Hd2IASManagementViewModel { IsBusy: false } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiLocalization.Current["IAS.Browse"], AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("HD2IAS ZIP") { Patterns = ["*.zip"] }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path && !vm.IsBusy) vm.PackagePath = path;
    }
}
