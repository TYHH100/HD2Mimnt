using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace HD2Mimnt.UI.Services;

public interface IConfirmationService
{
    Task<bool> ConfirmAsync(string title, string message);
}

public sealed class ConfirmationService : IConfirmationService
{
    private Window? _owner;

    public void SetOwner(Window owner) => _owner = owner;

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        if (_owner == null) return false;

        var cancel = new Button { Content = "取消", MinWidth = 80 };
        var confirm = new Button { Content = "确认", MinWidth = 80 };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, confirm }
        };
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            Height = 190,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 18,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    actions
                }
            }
        };
        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        return await dialog.ShowDialog<bool>(_owner);
    }
}
