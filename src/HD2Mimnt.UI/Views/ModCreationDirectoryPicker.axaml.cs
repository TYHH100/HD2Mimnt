using Avalonia.Controls;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

public partial class ModCreationDirectoryPicker : UserControl
{
    public ModCreationDirectoryPicker() => InitializeComponent();

    public ModCreationDirectoryPicker(ModCreationDirectoryViewModel root) : this()
        => this.FindControl<TreeView>("CreationDirectoryTree")!.ItemsSource = new[] { root };
}
