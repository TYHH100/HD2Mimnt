using HD2Mimnt.UI.Localization;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private async void OnPickBackgroundClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel || TopLevel.GetTopLevel(this) is not Window owner) return;

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiLocalization.Current.Translate("选择背景图片"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(UiLocalization.Current.Translate("图片文件"))
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" }
                }
            }
        });

        var path = files.Count > 0 ? files[0].Path.LocalPath : null;
        if (!string.IsNullOrWhiteSpace(path)) await viewModel.SetBackgroundImagePathAsync(path);
    }

    private async Task PickFolderAsync(Action<string> apply)
    {
        if (DataContext is not SettingsViewModel || TopLevel.GetTopLevel(this) is not Window owner) return;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = UiLocalization.Current.Translate("选择文件夹"), AllowMultiple = false
        });
        if (folders.Count > 0 && !string.IsNullOrWhiteSpace(folders[0].Path.LocalPath)) apply(folders[0].Path.LocalPath);
    }

    private async void OnPickAppDataClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await PickFolderAsync(path => ((SettingsViewModel)DataContext!).AppDataPath = path);
    private async void OnPickModsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await PickFolderAsync(path => ((SettingsViewModel)DataContext!).ModsStoragePath = path);
    private async void OnPickCacheClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await PickFolderAsync(path => ((SettingsViewModel)DataContext!).TempCachePath = path);
    private async void OnPickHellForgeClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await PickFolderAsync(path => ((SettingsViewModel)DataContext!).HellForgePath = path);
}
