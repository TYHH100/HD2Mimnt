using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

public partial class ModCreationView
{
    private async void CreationTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source == CreationTabs && CreationTabs.SelectedItem == CreationManagerPreviewTab
            && Model is { CanEdit: true } vm) await vm.RefreshManagerPreviewAsync();
    }
    private async void RefreshPreview_Click(object? sender, RoutedEventArgs e)
    { if (Model is { CanEdit: true } vm) await vm.RefreshManagerPreviewAsync(); }

    private async void Screenshot_Click(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanEdit: true } vm || Storage is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new()
        {
            Title = UiLocalization.Current["Creator.ExportScreenshot"], ShowOverwritePrompt = true,
            SuggestedFileName = vm.ExportBaseName + ".png", DefaultExtension = "png",
            FileTypeChoices = [new("PNG") { Patterns = ["*.png"] }]
        });
        if (file?.TryGetLocalPath() is { } path && vm.CanEdit)
            await vm.ExportScreenshotAsync(path, RenderScreenshotAsync);
    }

    /// <summary>单独布局完整内容，不能截取屏幕中的滚动视口，也不改变当前预览的展开状态。</summary>
    public async Task<byte[]> RenderScreenshotAsync(ModCreationPreviewViewModel preview, CancellationToken token)
    {
        var content = new ModCreationPreview { DataContext = preview, Width = 760 };
        CreationScreenshotHost.Children.Add(content);
        try
        {
            content.Measure(new Size(760, double.PositiveInfinity));
            content.Arrange(new Rect(content.DesiredSize));
            // 截图使用最终展开态，避免把渐显动画的半透明中间帧写入 PNG。
            foreach (var visual in content.GetVisualDescendants().OfType<Animatable>()) visual.Transitions = null;
            foreach (var expander in content.GetVisualDescendants().OfType<Expander>())
            { expander.ContentTransition = null; expander.IsExpanded = true; }
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            token.ThrowIfCancellationRequested();
            content.Measure(new Size(760, double.PositiveInfinity));
            content.Arrange(new Rect(content.DesiredSize));
            content.UpdateLayout();
            using var bitmap = new RenderTargetBitmap(new PixelSize(760, (int)Math.Ceiling(content.Bounds.Height)), new Vector(96, 96));
            bitmap.Render(content);
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var stream = new MemoryStream(); bitmap.Save(stream); return stream.ToArray();
            }, token);
        }
        finally { CreationScreenshotHost.Children.Remove(content); content.DataContext = null; }
    }
}
