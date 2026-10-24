using Avalonia.Controls;
using Avalonia.Input;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Controls;

public partial class AddToProfileOverlay : UserControl
{
    public AddToProfileOverlay() => InitializeComponent();

    private void OnAddToProfileBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        (DataContext as LibraryViewModel)?.CancelAddToProfileCommand.Execute(null);
        e.Handled = true;
    }
}
