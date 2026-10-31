using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Controls;

public partial class AudioPreviewOverlay : UserControl
{
    private AudioPreviewViewModel? _viewModel;

    public AudioPreviewOverlay()
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
        base.OnDetachedFromVisualTree(e);
    }

    private void Connect()
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnStateChanged;
        _viewModel = DataContext as AudioPreviewViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnStateChanged;
        if (_viewModel?.IsOpen == true) CloseAudioPreviewButton.Focus();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 打开时把焦点交给关闭按钮，保证 Escape 的 KeyBinding 生效。
        if (e.PropertyName == nameof(AudioPreviewViewModel.IsOpen) && _viewModel?.IsOpen == true)
            CloseAudioPreviewButton.Focus();
    }
}
