using System.Windows.Input;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>预览选择只保存在快照中，不修改创建草稿或配置文件。</summary>
public sealed class ModCreationPreviewViewModel : IModOptionsSelection, IDisposable
{
    private readonly ImagePreviewService? _imagePreview;
    private readonly List<(string? Path, Action<Bitmap> Set)> _images = [];
    private readonly List<Bitmap> _owned = [];
    public string Name { get; }
    public string Author { get; }
    public string Description { get; }
    public Bitmap? Cover { get; private set; }
    public bool IsLegacyMode { get; }
    public IReadOnlyList<ProfileModOptionCardViewModel> OptionCards { get; }
    public IReadOnlyList<ProfileModLegacyChoiceViewModel> LegacyChoices { get; }
    public ICommand ToggleOptionCommand { get; }

    public ModCreationPreviewViewModel(ModManifest manifest, ImagePreviewService? imagePreview)
    {
        _imagePreview = imagePreview;
        Name = manifest.Name; Author = manifest.Author; Description = manifest.Description;
        IsLegacyMode = manifest.ManifestVersion == ManifestVersion.Legacy;
        ToggleOptionCommand = new RelayCommand<ProfileModOptionCardViewModel>(card => { if (card != null) card.IsEnabled = !card.IsEnabled; });
        LegacyChoices = IsLegacyMode ? (manifest.LegacyOptions ?? []).Select((name, index) =>
            new ProfileModLegacyChoiceViewModel { Name = name, IsSelected = index == 0 }).ToList() : [];
        OptionCards = IsLegacyMode ? [] : (manifest.Options ?? []).Select(option =>
        {
            var children = (option.SubOptions ?? []).Select((sub, index) =>
            {
                var child = new ProfileModSubOptionChoiceViewModel { Name = sub.Name, Description = sub.Description, IsSelected = index == 0 };
                _images.Add((sub.Image, image => child.Image = image)); return child;
            }).ToList();
            var card = new ProfileModOptionCardViewModel { Name = option.Name, Description = option.Description,
                IsEnabled = option.DefaultEnabled ?? true, SubOptions = children };
            _images.Add((option.Image, image => card.Image = image)); return card;
        }).ToList();
        _images.Add((manifest.IconPath, image => Cover = image));
    }

    public async Task LoadImagesAsync(ModCreationService service, string root, CancellationToken token)
    {
        foreach (var (path, set) in _images)
        {
            var bytes = await service.ReadPreviewImageAsync(root, path, token);
            if (bytes == null) continue;
            Bitmap bitmap;
            try { bitmap = await Task.Run(() => { using var stream = new MemoryStream(bytes); return new Bitmap(stream); }, token); }
            catch (Exception error) when (error is not OperationCanceledException) { continue; }
            _owned.Add(bitmap); token.ThrowIfCancellationRequested(); set(bitmap);
        }
    }

    public void SelectSubOption(ProfileModSubOptionChoiceViewModel? choice)
    {
        var card = OptionCards.FirstOrDefault(card => card.SubOptions.Contains(choice!));
        if (card != null) foreach (var sub in card.SubOptions) sub.IsSelected = ReferenceEquals(sub, choice);
    }
    public void SelectLegacyChoice(ProfileModLegacyChoiceViewModel? choice)
    { foreach (var item in LegacyChoices) item.IsSelected = ReferenceEquals(item, choice); }
    public void ShowImagePreview(Bitmap? image, string? title) => _imagePreview?.Show(image, title);
    public void Dispose()
    {
        if (_owned.Contains(_imagePreview?.Image!)) _imagePreview!.Close();
        foreach (var image in _owned) image.Dispose(); _owned.Clear();
    }
}
