using Avalonia.Controls;
using Avalonia.Input;
using HD2Mimnt.Application.Services;

namespace HD2Mimnt.UI.Controls;

public partial class NotificationToastHost : UserControl
{
    private NotificationPanelViewModel? _attachedViewModel;
    private bool _attached;

    public NotificationToastHost()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => { _attached = true; AttachViewModel(); };
        DetachedFromVisualTree += (_, _) => { _attached = false; AttachViewModel(); };
        DataContextChanged += (_, _) => AttachViewModel();
    }

    private void AttachViewModel()
    {
        _attachedViewModel?.StopToasts();
        _attachedViewModel = _attached ? DataContext as NotificationPanelViewModel : null;
        _attachedViewModel?.StartToasts();
    }

    private void OnCardPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Border { DataContext: Notification notification })
            _attachedViewModel?.PauseToast(notification, true);
    }

    private void OnCardPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Border { DataContext: Notification notification })
            _attachedViewModel?.PauseToast(notification, false);
    }
}
