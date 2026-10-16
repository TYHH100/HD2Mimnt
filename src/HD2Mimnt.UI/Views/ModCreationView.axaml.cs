using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.Application.Services;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.UI.Views;

public partial class ModCreationView : UserControl
{
    public ModCreationView()
    {
        InitializeComponent();
        InitializeSorting();
    }
    private ModCreationViewModel? Model => DataContext as ModCreationViewModel;
    private IStorageProvider? Storage => TopLevel.GetTopLevel(this)?.StorageProvider;

    private async void Source_Click(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanEdit: true } vm || Storage is not { } storage) return;
        var folders = await storage.OpenFolderPickerAsync(new() { Title = UiLocalization.Current["Creator.SourceDirectory"] });
        if (vm.CanEdit && folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
        { vm.SourceDirectory = path; await vm.InitializeSourceAsync(); }
    }
    private async void Source_LostFocus(object? sender, RoutedEventArgs e)
    { if (Model is { CanEdit: true } vm) await vm.InitializeSourceAsync(); }
    private async void Source_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Model is not { CanEdit: true } vm) return;
        e.Handled = true; await vm.InitializeSourceAsync();
    }
    private async void Open_Click(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanEdit: true } vm || Storage is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new() { Title = UiLocalization.Current["Creator.Open"],
            FileTypeFilter = [new("JSON") { Patterns = ["*.json"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path && vm.CanEdit && await vm.ConfirmDiscardAsync()) await vm.OpenAsync(path);
    }
    private async void Save_Click(object? sender, RoutedEventArgs e) => await SaveOrExportAsync(false);
    private async void Export_Click(object? sender, RoutedEventArgs e) => await SaveOrExportAsync(true);
    private async Task SaveOrExportAsync(bool archive)
    {
        if (Model is not { CanEdit: true } vm || Storage is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new()
        {
            Title = UiLocalization.Current[archive ? "Creator.Export" : "Creator.Save"], ShowOverwritePrompt = true,
            SuggestedFileName = archive ? vm.ArchiveFileName : "manifest.json", DefaultExtension = archive ? vm.ArchiveExtension : "json",
            SuggestedStartLocation = string.IsNullOrWhiteSpace(vm.SourceDirectory) ? null : await storage.TryGetFolderFromPathAsync(vm.SourceDirectory),
            FileTypeChoices = [new(archive ? vm.ArchiveExtension.ToUpperInvariant() : "JSON")
                { Patterns = [archive ? "*." + vm.ArchiveExtension : "*.json"] }]
        });
        if (file?.TryGetLocalPath() is not { } path || !vm.CanEdit) return;
        if (archive) await vm.ExportAsync(path); else await vm.SaveAsync(path);
    }
    private async void Image_Click(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanEdit: true } vm || Storage is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new() { Title = UiLocalization.Current["Creator.OptionImage"],
            FileTypeFilter = [FilePickerFileTypes.ImageAll] });
        if (!vm.CanEdit || files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        try
        {
            var relative = vm.RelativePath(path);
            switch ((sender as Control)?.DataContext)
            {
                case ModCreationOptionViewModel option: option.ImagePath = relative; break;
                case ModCreationSubOptionViewModel sub: sub.ImagePath = relative; break;
                default: vm.IconPath = relative; break;
            }
        }
        catch (Exception error) { vm.ShowError(error); }
    }
    private async void Include_Click(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanEdit: true } vm || TopLevel.GetTopLevel(this) is not Window owner
            || (sender as Control)?.DataContext is not { } target) return;
        try
        {
            var current = target switch
            {
                ModCreationOptionViewModel option => option.IncludePaths,
                ModCreationSubOptionViewModel sub => sub.IncludePaths,
                _ => string.Empty
            };
            var directories = await PickDirectoriesAsync(vm, owner, current);
            if (directories != null && ReferenceEquals(Model, vm) && vm.CanEdit)
                vm.ApplyDirectories(target, directories);
        }
        catch (Exception error) { vm.ShowError(error); }
    }

    private async void LegacyInclude_Click(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanEdit: true, IsLegacyManifest: true } vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        try
        {
            var directories = await PickDirectoriesAsync(vm, owner, string.Empty);
            if (directories != null && ReferenceEquals(Model, vm) && vm.CanEdit)
                vm.AddLegacyDirectories(directories);
        }
        catch (Exception error) { vm.ShowError(error); }
    }

    private async Task<List<ModCreationDirectory>?> PickDirectoriesAsync(
        ModCreationViewModel vm, Window owner, string current)
    {
        var directory = await vm.ReadDirectoriesAsync();
        if (directory == null || !vm.CanEdit || !ReferenceEquals(Model, vm) || !this.IsAttachedToVisualTree()) return null;
        var selected = new HashSet<string>(ModCreationOptionViewModel.ParsePaths(current)
            .Select(path => path.Replace('\\', '/').TrimEnd('/')), StringComparer.OrdinalIgnoreCase);
        var root = new ModCreationDirectoryViewModel(directory, selected);
        var picker = new ModCreationDirectoryPicker(root);
        var dialog = new FAContentDialog
        {
            Title = UiLocalization.Current["Creator.AddDirectory"], Content = picker,
            PrimaryButtonText = UiLocalization.Current["Common.Confirm"], CloseButtonText = UiLocalization.Current["Common.Cancel"],
            DefaultButton = FAContentDialogButton.Primary
        };
        using var titleBinding = dialog.Bind(FAContentDialog.TitleProperty, LocExtension.CreateBinding("Creator.AddDirectory"));
        using var confirmBinding = dialog.Bind(FAContentDialog.PrimaryButtonTextProperty, LocExtension.CreateBinding("Common.Confirm"));
        using var cancelBinding = dialog.Bind(FAContentDialog.CloseButtonTextProperty, LocExtension.CreateBinding("Common.Cancel"));
        return await dialog.ShowAsync(owner) == FAContentDialogResult.Primary ? root.SelectedDirectories().ToList() : null;
    }
}
