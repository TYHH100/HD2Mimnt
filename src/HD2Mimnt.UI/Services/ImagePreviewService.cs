using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HD2Mimnt.UI.Services;

/// <summary>
/// 应用内图片预览覆盖层的共享状态（DI 单例）。各页面图片点击后调用 Show，
/// 主窗口顶层的 ImagePreviewOverlay 即显示原图；点击背景/关闭按钮/Esc 关闭。
/// 必须在 UI 线程调用。
/// </summary>
public partial class ImagePreviewService : ObservableObject
{
    private Bitmap? _ownedImage;
    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private Bitmap? _image;

    [ObservableProperty]
    private string _title = string.Empty;

    public void Show(Bitmap? image, string? title, bool ownsImage = false)
    {
        if (image == null) return;

        Close();
        _ownedImage = ownsImage ? image : null;
        Image = image;
        Title = title ?? string.Empty;
        IsOpen = true;
    }

    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
        Image = null;
        Title = string.Empty;
        _ownedImage?.Dispose();
        _ownedImage = null;
    }
}
