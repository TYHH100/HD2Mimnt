using CommunityToolkit.Mvvm.ComponentModel;
using HD2Mimnt.Application.Services;

namespace HD2Mimnt.UI.ViewModels;

public partial class ModCreationDirectoryViewModel : ObservableObject
{
    public ModCreationDirectory Directory { get; }
    public string Name => Directory.Name;
    public string RelativePath => Directory.RelativePath;
    public IReadOnlyList<ModCreationDirectoryViewModel> Children { get; }
    [ObservableProperty] private bool _isSelected;

    public ModCreationDirectoryViewModel(ModCreationDirectory directory, IReadOnlySet<string> selected)
    {
        Directory = directory;
        IsSelected = selected.Contains(directory.RelativePath);
        Children = directory.Children.Select(child => new ModCreationDirectoryViewModel(child, selected)).ToList();
    }

    public IEnumerable<ModCreationDirectory> SelectedDirectories()
    {
        if (IsSelected) yield return Directory;
        foreach (var child in Children)
        foreach (var selected in child.SelectedDirectories()) yield return selected;
    }
}
