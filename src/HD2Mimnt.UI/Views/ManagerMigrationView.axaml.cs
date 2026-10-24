using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

public partial class ManagerMigrationView : UserControl
{
    public ManagerMigrationView() => InitializeComponent();
    private async void OnBrowseClicked(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not ManagerMigrationViewModel { IsBusy: false } vm || sender is not Button button || TopLevel.GetTopLevel(this) is not { } top) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = UiLocalization.Current["Migration.Browse"], AllowMultiple = false });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path == null || vm.IsBusy) return;
        switch (button.Tag as string) { case "installation": vm.InstallationPath = path; break; case "mods": vm.ModsPath = path; break; default: vm.DataPath = path; break; }
    }
}
