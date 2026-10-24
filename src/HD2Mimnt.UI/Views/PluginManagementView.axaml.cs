using HD2Mimnt.UI.Localization;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

public partial class PluginManagementView : UserControl
{
    public PluginManagementView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            if (DataContext is PluginManagementViewModel viewModel)
            {
                viewModel.PermissionEditorRequested -= OnPermissionEditorRequested;
                viewModel.PermissionEditorRequested += OnPermissionEditorRequested;
            }
        };
    }

    private async void OnPermissionEditorRequested(object? sender, PluginItemViewModel plugin)
    {
        if (DataContext is not PluginManagementViewModel viewModel ||
            TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var dialogViewModel = await PluginPermissionDialog.ShowManagementAsync(owner, plugin);
        if (dialogViewModel.Result == PluginPermissionDialogResult.AllowAlways)
        {
            await viewModel.ApplyPermissionsAsync(plugin, dialogViewModel.RequestedPermissions);
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnInstallPluginClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not PluginManagementViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null || topLevel is not Window owner) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiLocalization.Current.Translate("选择插件 manifest"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(UiLocalization.Current.Translate("插件 manifest")) { Patterns = new[] { "*.json" } },
                new FilePickerFileType(UiLocalization.Current.Translate("所有文件")) { Patterns = new[] { "*" } }
            }
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            var manifest = await vm.GetManifestPreviewAsync(path);
            if (manifest == null) return;
            var permissionResult = await PluginPermissionDialog.ShowAsync(
                owner,
                manifest.Name,
                manifest.Version,
                manifest.Author,
                manifest.Description,
                manifest.PermissionMode.ToString(),
                manifest.RequestedPermissions);
            if (permissionResult == PluginPermissionDialogResult.Deny) return;

            await vm.InstallPluginAsync(path);
        }
    }
}
