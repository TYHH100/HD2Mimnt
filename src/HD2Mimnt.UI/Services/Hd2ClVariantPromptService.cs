using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.Core.Hd2IAS;

namespace HD2Mimnt.UI.Services;

/// <summary>合集模组存在多个可转换版本时，让用户选择要转换哪一个。</summary>
public interface IHd2ClVariantPrompt
{
    Task<Hd2ClVariant?> ChooseAsync(IReadOnlyList<Hd2ClVariant> variants);
}

public sealed class Hd2ClVariantPromptService : IHd2ClVariantPrompt
{
    private Window? _owner;

    public void SetOwner(Window owner) => _owner = owner;

    public async Task<Hd2ClVariant?> ChooseAsync(IReadOnlyList<Hd2ClVariant> variants)
    {
        if (_owner == null || variants.Count == 0) return null;
        var list = new ListBox
        {
            ItemsSource = variants,
            SelectedIndex = 0,
            MaxHeight = 320,
            ItemTemplate = new FuncDataTemplate<Hd2ClVariant>((variant, _) => new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = variant.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(variant.Description)
                            ? $"{variant.ComponentFolders.Count} 个组件"
                            : $"{variant.Description} · {variant.ComponentFolders.Count} 个组件",
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.7
                    }
                }
            }, supportsRecycling: false)
        };
        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "该模组包含多个可转换版本，请选择要转换的一个。",
                    TextWrapping = TextWrapping.Wrap
                },
                list
            }
        };
        var dialog = new FAContentDialog
        {
            Title = "选择转换版本",
            Content = content,
            PrimaryButtonText = "转换所选版本",
            CloseButtonText = "取消",
            DefaultButton = FAContentDialogButton.Primary
        };
        return await dialog.ShowAsync(_owner) == FAContentDialogResult.Primary
            ? list.SelectedItem as Hd2ClVariant
            : null;
    }
}
