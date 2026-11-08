using Avalonia.Controls;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

/// <summary>插件贡献的独立页面：渲染声明式区块，并承接浮窗与结果窗口。</summary>
public partial class PluginPageView : UserControl
{
    private PluginUiSurfaceViewModel? _surface;

    public PluginPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachSurface(DataContext as PluginUiSurfaceViewModel);
    }

    private void AttachSurface(PluginUiSurfaceViewModel? surface)
    {
        if (_surface is { } previous)
        {
            previous.DialogRequested -= OnDialogRequested;
            previous.ResultReady -= OnResultReady;
        }

        _surface = surface;

        if (_surface is { } current)
        {
            current.DialogRequested += OnDialogRequested;
            current.ResultReady += OnResultReady;
        }
    }

    private async void OnDialogRequested(object? sender, PluginDialogRequestEventArgs e)
    {
        var dialogSurface = _surface?.CreateDialogSurface(e.DialogId);
        if (dialogSurface is null || TopLevel.GetTopLevel(this) is not Window owner) return;
        await PluginUiDialog.ShowAsync(owner, dialogSurface);
    }

    private async void OnResultReady(object? sender, PluginCommandResultEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        await PluginCommandResultDialog.ShowAsync(owner, e);
    }
}
