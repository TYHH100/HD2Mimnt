using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

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

        var dialog = new FAContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = FAContentDialogButton.Close
        };
        return await dialog.ShowAsync(_owner) == FAContentDialogResult.Primary;
    }
}