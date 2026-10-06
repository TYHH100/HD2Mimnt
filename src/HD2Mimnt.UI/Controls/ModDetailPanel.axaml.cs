using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Controls;

public partial class ModDetailPanel : UserControl
{
    public ModDetailPanel()
    {
        InitializeComponent();
    }

    /// <summary>点击详情面板大图标：在主窗口顶层的覆盖层预览原图（无图标时无操作）。</summary>
    private void Icon_Tapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Border { DataContext: ModItemViewModel { IconBitmap: { } icon } mod }) return;

        // 详情面板的 DataContext 是模组项本身，向上找持有 ImagePreviewService 的宿主 ViewModel
        var owner = this.GetVisualAncestors()
            .OfType<UserControl>()
            .Select(view => view.DataContext)
            .OfType<LibraryViewModel>()
            .FirstOrDefault();
        owner?.ShowImagePreview(icon, mod.Name);
    }
}
