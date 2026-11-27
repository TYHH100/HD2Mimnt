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

        WebSurface.ErrorChanged -= OnWebErrorChanged;
        _surface = surface;

        if (_surface is { } current)
        {
            current.DialogRequested += OnDialogRequested;
            current.ResultReady += OnResultReady;
            WebSurface.ErrorChanged += OnWebErrorChanged;
            if (current.IsWebView)
            {
                WebSurface.Configure(
                    current.PluginId,
                    current.SurfaceId,
                    current.WebEntryPath,
                    current.WebViewEnvironment,
                    current.ExecuteWebCommandAsync);
            }
            else
            {
                WebSurface.Reset();
            }
        }
        else
        {
            WebSurface.Reset();
        }
    }

    public void SetWebViewHostVisible(bool visible) => WebSurface.SetHostVisible(visible);

    private void OnWebErrorChanged(object? sender, string? error)
    {
        if (_surface is { } current) current.WebErrorMessage = error;
    }

    private void OnReloadWebViewClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        WebSurface.Reload();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachSurface(DataContext as PluginUiSurfaceViewModel);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        WebSurface.ErrorChanged -= OnWebErrorChanged;
        WebSurface.Reset();
        base.OnDetachedFromVisualTree(e);
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
