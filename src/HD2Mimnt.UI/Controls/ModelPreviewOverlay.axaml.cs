using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
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
        ViewportHost.Content = _viewport;
        CloseModelPreviewButton.Focus();
    }

    private void OnRenderFailed(string message) => _viewModel?.ReportRenderFailure(message);

    private void RemoveViewport()
    {
        if (_viewport is null) return;
        _viewport.RenderFailed -= OnRenderFailed;
        _viewport.Scene = null;
        _viewport.DataContext = null;
        ViewportHost.Content = null;
        _viewport = null;
    }

    private void ResetCamera(object? sender, RoutedEventArgs e) => _viewport?.ResetCamera();
}
