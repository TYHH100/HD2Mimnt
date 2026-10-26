using Avalonia.Controls;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.Core.Darktide;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.Services;

public interface IDarktideDialogService
{
    void SetOwner(Window owner);
    Task<string?> PickArchiveAsync();
    Task<string?> PickFolderAsync();
    Task<string?> PickImageAsync();
    Task<string?> PromptAsync(string titleKey, string labelKey, string initialValue = "");
    Task<IReadOnlyList<DarktideArchiveCandidate>?> PickCandidatesAsync(IReadOnlyList<DarktideArchiveCandidate> candidates);
    Task<bool> ConfirmAsync(string title, string message);
    void OpenFolder(string path);
    void OpenFile(string path);
}

public sealed class DarktideDialogService : IDarktideDialogService
{
    private readonly IConfirmationService _confirmation;
    private Window? _owner;

    public DarktideDialogService(IConfirmationService confirmation) => _confirmation = confirmation;

    public void SetOwner(Window owner) => _owner = owner;

    public async Task<string?> PickArchiveAsync()
    {
        if (_owner == null) return null;
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiLocalization.Current["Darktide.Library.SelectArchive"],
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("ZIP") { Patterns = new[] { "*.zip" } } }
        });
        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickImageAsync()
    {
        if (_owner == null) return null;
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiLocalization.Current["UI.ChooseImage"],
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Image") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" } } }
        });
        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFolderAsync()
    {
        if (_owner == null) return null;
        var folders = await _owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = UiLocalization.Current["Darktide.Game.ChooseFolderTitle"], AllowMultiple = false
        });
        return folders.Count == 1 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PromptAsync(string titleKey, string labelKey, string initialValue = "")
    {
        if (_owner == null) return null;
        var localizer = UiLocalization.Current;
        var input = new TextBox { Text = initialValue, MinWidth = 360 };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = localizer[labelKey], TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        content.Children.Add(input);
        var dialog = new FAContentDialog
        {
            Title = localizer[titleKey], Content = content,
            PrimaryButtonText = localizer["Darktide.Common.Confirm"],
            CloseButtonText = localizer["Darktide.Common.Cancel"],
            DefaultButton = FAContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync(_owner);
        return result == FAContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text)
            ? input.Text.Trim() : null;
    }

    public async Task<IReadOnlyList<DarktideArchiveCandidate>?> PickCandidatesAsync(
        IReadOnlyList<DarktideArchiveCandidate> candidates)
    {
        if (candidates.Count == 1) return new[] { candidates[0] };
        if (_owner == null) return null;
        var localizer = UiLocalization.Current;
        var labels = new List<string> { localizer.Format("Darktide.Library.InstallAllCandidates", candidates.Count) };
        labels.AddRange(candidates.Select(candidate => $"{candidate.DisplayPath}  —  {candidate.InternalName}"));
        var combo = new ComboBox { ItemsSource = labels, SelectedIndex = 0, MinWidth = 420 };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = localizer["Darktide.Library.SelectCandidate"], TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        content.Children.Add(combo);
        var dialog = new FAContentDialog
        {
            Title = localizer["Darktide.Library.SelectCandidate"], Content = content,
            PrimaryButtonText = localizer["Darktide.Common.Confirm"],
            CloseButtonText = localizer["Darktide.Common.Cancel"],
            DefaultButton = FAContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync(_owner);
        if (result != FAContentDialogResult.Primary || combo.SelectedIndex < 0) return null;
        return combo.SelectedIndex == 0
            ? candidates
            : new[] { candidates[combo.SelectedIndex - 1] };
    }

    public Task<bool> ConfirmAsync(string title, string message) => _confirmation.ConfirmAsync(title, message);

    public void OpenFolder(string path)
    {
        if (Directory.Exists(path))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"")
                { UseShellExecute = true });
    }

    public void OpenFile(string path)
    {
        if (!File.Exists(path)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            { UseShellExecute = true });
    }
}
