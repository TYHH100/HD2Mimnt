using Avalonia.Controls;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Controls;

public partial class ArchivePasswordsControl : UserControl
{
    public ArchivePasswordsControl()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => (DataContext as ArchivePasswordsViewModel)?.Reload();
    }
}
