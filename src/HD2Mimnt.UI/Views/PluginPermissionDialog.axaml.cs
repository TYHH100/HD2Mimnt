using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

public partial class PluginPermissionDialog : Window
{
    public PluginPermissionDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public static async Task<PluginPermissionDialogResult> ShowAsync(
        Window owner,
        string pluginName,
        string version,
        string author,
        string description,
        string permissionMode,
        IEnumerable<string> requestedPermissions)
    {
        var dialog = new PluginPermissionDialog();
        var viewModel = new PluginPermissionDialogViewModel();
        viewModel.Initialize(pluginName, version, author, description, permissionMode, requestedPermissions);
        dialog.DataContext = viewModel;

        await dialog.ShowDialog(owner);

        return viewModel.Result;
    }

    public static async Task<PluginPermissionDialogViewModel> ShowManagementAsync(
        Window owner,
        PluginItemViewModel plugin)
    {
        var dialog = new PluginPermissionDialog();
        var viewModel = new PluginPermissionDialogViewModel();
        viewModel.InitializeManagement(plugin);
        dialog.DataContext = viewModel;
        await dialog.ShowDialog(owner);
        return viewModel;
    }
}
