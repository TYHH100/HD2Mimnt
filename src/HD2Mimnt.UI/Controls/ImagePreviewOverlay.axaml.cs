using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.Controls;

public partial class ImagePreviewOverlay : UserControl
{
    private ImagePreviewService? _service;

    public ImagePreviewOverlay()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_service != null)
            {
                _service.PropertyChanged -= OnServicePropertyChanged;
            }

            _service = DataContext as ImagePreviewService;
            if (_service != null)
            {
                _service.PropertyChanged += OnServicePropertyChanged;
            }

            UpdateImageBounds();
        };
    }

    private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImagePreviewService.Image))
        {
            UpdateImageBounds();
        }
    }

    /// <summary>
    /// 把图片尺寸上限限制为原始像素：大图等比缩小适配窗口，小图（如选项缩略图）不放大失真。
    /// </summary>
    private void UpdateImageBounds()
    {
        var bitmap = _service?.Image;
        var size = bitmap?.Size;
        PreviewImage.MaxWidth = size?.Width ?? double.PositiveInfinity;
        PreviewImage.MaxHeight = size?.Height ?? double.PositiveInfinity;
    }

    /// <summary>点击背景空白处关闭；点击图片本身不关闭（可继续查看）。</summary>
    private void Root_Tapped(object? sender, TappedEventArgs e)
    {
        if (ReferenceEquals(e.Source, PreviewImage)) return;
        _service?.Close();
    }
}
