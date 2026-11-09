using Avalonia.Controls;
using Avalonia.Interactivity;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

/// <summary>插件贡献的浮窗：只渲染声明式区块，插件进程无法访问这里的任何控件。</summary>
public partial class PluginUiDialog : Window
{
    public PluginUiDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachSurface(DataContext as PluginUiSurfaceViewModel);
    }

    public static async Task ShowAsync(Window owner, PluginUiSurfaceViewModel surface)
    {
        var dialog = new PluginUiDialog { DataContext = surface };
        await dialog.ShowDialog(owner);
    }

    private void AttachSurface(PluginUiSurfaceViewModel? surface)
    {
        if (surface is null) return;
        surface.DialogRequested += OnDialogRequested;
        surface.ResultReady += OnResultReady;
        Closed += (_, _) =>
        {
            surface.DialogRequested -= OnDialogRequested;
            surface.ResultReady -= OnResultReady;
        };
    }

    private async void OnDialogRequested(object? sender, PluginDialogRequestEventArgs e)
    {
        var dialogSurface = (DataContext as PluginUiSurfaceViewModel)?.CreateDialogSurface(e.DialogId);
        if (dialogSurface is null) return;
        await ShowAsync(this, dialogSurface);
    }

    private async void OnResultReady(object? sender, PluginCommandResultEventArgs e)
        => await PluginCommandResultDialog.ShowAsync(this, e);

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
