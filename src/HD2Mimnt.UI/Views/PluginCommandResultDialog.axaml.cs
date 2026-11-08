using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

/// <summary>
/// 插件命令结果窗口。
///
/// AI 与插件普遍返回 Markdown，直接以原始文本展示可读性很差，
/// 因此默认按 Markdown 渲染，并保留“原文”切换以便复制精确文本。
/// </summary>
public partial class PluginCommandResultDialog : Window
{
    private bool _showSource;

    public PluginCommandResultDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindContent();
        UpdateToggleLabel();
    }

    public static async Task ShowAsync(Window owner, PluginCommandResultEventArgs result)
    {
        var dialog = new PluginCommandResultDialog { DataContext = result };
        await dialog.ShowDialog(owner);
    }

    private void BindContent()
    {
        if (DataContext is not PluginCommandResultEventArgs result) return;

        MarkdownContent.Markdown = result.Content;
        RawText.Text = result.Content;
    }

    private void OnToggleSourceClick(object? sender, RoutedEventArgs e)
    {
        _showSource = !_showSource;
        RenderedHost.IsVisible = !_showSource;
        RawHost.IsVisible = _showSource;
        UpdateToggleLabel();
    }

    private void UpdateToggleLabel()
    {
        ToggleSourceButton.Content = _showSource
            ? UiLocalization.Current["UI.MarkdownRendered"]
            : UiLocalization.Current["UI.MarkdownSource"];
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PluginCommandResultEventArgs result) return;
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(result.Content);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
