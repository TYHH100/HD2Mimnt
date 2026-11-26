using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.UI.Localization;

namespace HD2Mimnt.UI.Services;

public enum ConfirmationChoice
{
    Primary,
    Secondary,
    Cancel
}

public interface IConfirmationService
{
    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>三选一确认；文案参数均为资源键，按当前语言实时绑定。</summary>
    Task<ConfirmationChoice> AskAsync(string title, string message, string primaryText, string secondaryText,
        string closeText = "Common.Cancel");
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

    public async Task<ConfirmationChoice> AskAsync(string title, string message, string primaryText,
        string secondaryText, string closeText = "Common.Cancel")
    {
        if (_owner == null) return ConfirmationChoice.Cancel;

        var dialog = new FAContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            SecondaryButtonText = secondaryText,
            CloseButtonText = closeText,
            DefaultButton = FAContentDialogButton.Primary
        };
        using var titleBinding = dialog.Bind(FAContentDialog.TitleProperty, LocExtension.CreateBinding(title));
        using var messageBinding = ((TextBlock)dialog.Content!).Bind(TextBlock.TextProperty, LocExtension.CreateBinding(message));
        using var primaryBinding = dialog.Bind(FAContentDialog.PrimaryButtonTextProperty, LocExtension.CreateBinding(primaryText));
        using var secondaryBinding = dialog.Bind(FAContentDialog.SecondaryButtonTextProperty, LocExtension.CreateBinding(secondaryText));
        using var closeBinding = dialog.Bind(FAContentDialog.CloseButtonTextProperty, LocExtension.CreateBinding(closeText));
        return await dialog.ShowAsync(_owner) switch
        {
            FAContentDialogResult.Primary => ConfirmationChoice.Primary,
            FAContentDialogResult.Secondary => ConfirmationChoice.Secondary,
            _ => ConfirmationChoice.Cancel
        };
    }
}
