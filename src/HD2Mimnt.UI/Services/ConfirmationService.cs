using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.UI.Localization;

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
        var localizer = UiLocalization.Current;
        using var titleBinding = dialog.Bind(FAContentDialog.TitleProperty, localizer.TextBinding(title));
        using var messageBinding = ((TextBlock)dialog.Content!).Bind(TextBlock.TextProperty, localizer.TextBinding(message));
        using var confirmBinding = dialog.Bind(FAContentDialog.PrimaryButtonTextProperty, localizer.TextBinding("确认"));
        using var cancelBinding = dialog.Bind(FAContentDialog.CloseButtonTextProperty, localizer.TextBinding("取消"));
        return await dialog.ShowAsync(_owner) == FAContentDialogResult.Primary;
    }
}
