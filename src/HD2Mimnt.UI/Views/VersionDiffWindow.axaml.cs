using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

/// <summary>本地版本文件内容对比窗口；打开后按左侧文件列表懒加载两侧内容。</summary>
public partial class VersionDiffWindow : Window
{
    public VersionDiffWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    public static async Task ShowAsync(Window owner, VersionDiffViewModel viewModel)
    {
        var window = new VersionDiffWindow { DataContext = viewModel };
        await window.ShowDialog(owner);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Close();
        e.Handled = true;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
