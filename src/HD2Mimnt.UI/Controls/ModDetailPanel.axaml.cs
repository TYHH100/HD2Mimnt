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

    private async void PreviewModel(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not ModItemViewModel mod) return;
        foreach (var owner in this.GetVisualAncestors().OfType<UserControl>().Select(view => view.DataContext))
        {
            if (owner is LibraryViewModel { ModelPreview: { } preview })
            {
                await preview.OpenAsync(mod.Id);
                return;
            }
            if (owner is ProfileViewModel { ModelPreview: { } profilePreview, SelectedProfile: { } selected })
            {
                await profilePreview.OpenAsync(mod.Id, selected.Id);
                return;
            }
        }
    }

    /// <summary>点击详情面板大图标：在主窗口顶层的覆盖层预览原图（无图标时无操作）。</summary>
    private async void Icon_Tapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Border { DataContext: ModItemViewModel mod }) return;

        // 详情面板的 DataContext 是模组项本身，向上找持有 ImagePreviewService 的宿主 ViewModel
        foreach (var owner in this.GetVisualAncestors().OfType<UserControl>().Select(view => view.DataContext))
        {
            if (owner is LibraryViewModel library)
            {
                await library.ShowModImagePreviewAsync(mod.Id, mod.Name);
                return;
            }
            if (owner is ProfileViewModel profile)
            {
                await profile.ShowModImagePreviewAsync(mod.Id, mod.Name);
                return;
            }
        }
    }
}
