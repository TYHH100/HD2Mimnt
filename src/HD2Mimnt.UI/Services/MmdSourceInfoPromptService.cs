using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.UI.Localization;

namespace HD2Mimnt.UI.Services;

/// <summary>原始 MMD 包缺少清单时补充名称/标识/作者/图标/说明。</summary>
public interface IHd2ClMmdSourceInfoPrompt
{
    Task<MmdAdaptationSourceInfo?> EditAsync(MmdAdaptationSourceInfo? current);
}

public sealed class MmdSourceInfoPromptService : IHd2ClMmdSourceInfoPrompt
{
    private Window? _owner;

    public void SetOwner(Window owner) => _owner = owner;

    public async Task<MmdAdaptationSourceInfo?> EditAsync(MmdAdaptationSourceInfo? current)
    {
        if (_owner is null) return null;
        var name = new TextBox { Text = current?.Name ?? string.Empty };
        var key = new TextBox { Text = current?.Key ?? string.Empty };
        var author = new TextBox { Text = current?.Author ?? string.Empty };
        var icon = new TextBox { Text = current?.Icon ?? string.Empty };
        var description = new TextBox
        {
            Text = current?.Description ?? string.Empty,
            AcceptsReturn = true,
            MinHeight = 70,
            TextWrapping = TextWrapping.Wrap
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("130,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 10,
            RowSpacing = 8
        };
        AddLabel(grid, "UI.Hd2ClMmdInfoName", 0);
        AddRow(grid, name, 0);
        AddLabel(grid, "UI.Hd2ClMmdInfoKey", 1);
        AddRow(grid, key, 1);
        AddLabel(grid, "UI.Hd2ClMmdInfoAuthor", 2);
        AddRow(grid, author, 2);
        AddLabel(grid, "UI.Hd2ClMmdInfoIcon", 3);
        var iconRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        iconRow.Children.Add(icon);
        var browse = new Button { Content = "…" };
        Grid.SetColumn(browse, 1);
        browse.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(_owner) is not { } top) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = UiLocalization.Current["UI.Hd2ClMmdInfoIcon"],
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Image") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"] }
                ]
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) icon.Text = path;
        };
        iconRow.Children.Add(browse);
        Grid.SetColumn(iconRow, 1);
        Grid.SetRow(iconRow, 3);
        grid.Children.Add(iconRow);
        AddLabel(grid, "UI.Hd2ClMmdInfoDescription", 4);
        AddRow(grid, description, 4);

        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
        hint.Bind(TextBlock.TextProperty, LocExtension.CreateBinding("UI.Hd2ClMmdInfoHint"));
        var content = new StackPanel { Spacing = 12, Children = { hint, grid } };

        var dialog = new FAContentDialog
        {
            Content = content,
            DefaultButton = FAContentDialogButton.Primary
        };
        using var titleBinding = dialog.Bind(FAContentDialog.TitleProperty, LocExtension.CreateBinding("UI.Hd2ClMmdInfoTitle"));
        using var primaryBinding = dialog.Bind(FAContentDialog.PrimaryButtonTextProperty, LocExtension.CreateBinding("UI.Hd2ClMmdInfoSave"));
        using var closeBinding = dialog.Bind(FAContentDialog.CloseButtonTextProperty, LocExtension.CreateBinding("Common.Cancel"));
        if (await dialog.ShowAsync(_owner) != FAContentDialogResult.Primary) return null;
        return new MmdAdaptationSourceInfo(
            name.Text?.Trim() ?? string.Empty,
            EmptyToNull(key.Text),
            EmptyToNull(author.Text),
            EmptyToNull(icon.Text),
            EmptyToNull(description.Text));
    }

    private static void AddLabel(Grid grid, string key, int row)
    {
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
        label.Bind(TextBlock.TextProperty, LocExtension.CreateBinding(key));
        Grid.SetRow(label, row);
        grid.Children.Add(label);
    }

    private static void AddRow(Grid grid, Control control, int row)
    {
        Grid.SetColumn(control, 1);
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
