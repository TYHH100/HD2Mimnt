using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HD2Mimnt.Core.ModelPreview;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Controls;

public partial class ModelPreviewOverlay : UserControl
{
    private ModelPreviewViewModel? _viewModel;
    private ModelPreviewViewport? _viewport;

    public ModelPreviewOverlay()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Connect();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Connect();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnStateChanged;
        _viewModel = null;
        RemoveViewport();
        base.OnDetachedFromVisualTree(e);
    }

    private void Connect()
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnStateChanged;
        if (!ReferenceEquals(_viewModel, DataContext)) RemoveViewport();
        _viewModel = DataContext as ModelPreviewViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnStateChanged;
        UpdateViewport();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModelPreviewViewModel.IsOpen)) UpdateViewport();
    }

    private void UpdateViewport()
    {
        if (_viewModel?.IsOpen != true) { RemoveViewport(); return; }
        if (_viewport is not null) return;
        _viewport = new ModelPreviewViewport { DataContext = _viewModel };
        _viewport.Bind(ModelPreviewViewport.SceneProperty, new Binding(nameof(ModelPreviewViewModel.Scene)));
        _viewport.Bind(ModelPreviewViewport.WireframeProperty, new Binding(nameof(ModelPreviewViewModel.Wireframe)));
        _viewport.Bind(ModelPreviewViewport.UseTexturesProperty, new Binding(nameof(ModelPreviewViewModel.UseTextures)));
        _viewport.Bind(ModelPreviewViewport.UseAlphaProperty, new Binding(nameof(ModelPreviewViewModel.UseAlpha)));
        _viewport.Bind(ModelPreviewViewport.ShowGridProperty, new Binding(nameof(ModelPreviewViewModel.ShowGrid)));
        _viewport.RenderFailed += OnRenderFailed;
        _viewport.TexturesUploaded += OnTexturesUploaded;
        _viewport.TextureReloadRequired += OnTextureReloadRequired;
        ViewportHost.Content = _viewport;
        CloseModelPreviewButton.Focus();
    }

    private void OnRenderFailed(string message) => _viewModel?.ReportRenderFailure(message);

    // 上传完成在 GL 线程触发；释放 CPU 像素只改字段，但仍封送回 UI 线程，避免与重载交错。
    private void OnTexturesUploaded(IReadOnlyDictionary<ulong, TexturePreviewData> textures)
    {
        try { Dispatcher.UIThread.Post(() => _viewModel?.ReleaseTexturePixels(textures)); }
        catch (InvalidOperationException)
        {
            // 应用正在关闭
        }
    }

    private void OnTextureReloadRequired()
    {
        try { Dispatcher.UIThread.Post(() => _viewModel?.RequestTextureReload()); }
        catch (InvalidOperationException)
        {
            // 应用正在关闭
        }
    }

    private void RemoveViewport()
    {
        if (_viewport is null) return;
        _viewport.RenderFailed -= OnRenderFailed;
        _viewport.TexturesUploaded -= OnTexturesUploaded;
        _viewport.TextureReloadRequired -= OnTextureReloadRequired;
        _viewport.Scene = null;
        _viewport.DataContext = null;
        ViewportHost.Content = null;
        _viewport = null;
    }

    private void ResetCamera(object? sender, RoutedEventArgs e) => _viewport?.ResetCamera();
}
