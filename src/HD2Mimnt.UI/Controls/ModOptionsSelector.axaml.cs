using Avalonia.Controls;
using Avalonia.Input;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Controls;

public partial class ModOptionsSelector : UserControl
{
    public ModOptionsSelector() => InitializeComponent();
    private void OptionPreview_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
            e.Handled = true;
    }
    private void SubChoice_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ProfileModSubOptionChoiceViewModel choice }
            && DataContext is IModOptionsSelection selection) selection.SelectSubOption(choice);
    }
    private void LegacyChoice_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ProfileModLegacyChoiceViewModel choice }
            && DataContext is IModOptionsSelection selection) selection.SelectLegacyChoice(choice);
    }
    private void GroupCard_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ProfileModOptionGroupViewModel group }
            && DataContext is IModOptionsSelection selection && group.CanOpen)
            selection.OpenGroupCommand.Execute(group);
    }
    private void PreviewImage_Tapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not IModOptionsSelection selection || sender is not Control control) return;
        if (control.DataContext is ProfileModOptionCardViewModel card) selection.ShowImagePreview(card.Image, card.Name);
        if (control.DataContext is ProfileModSubOptionChoiceViewModel sub) selection.ShowImagePreview(sub.Image, sub.Name);
        if (control.DataContext is ProfileModOptionGroupViewModel group) selection.ShowImagePreview(group.Image, group.Name);
    }
}
