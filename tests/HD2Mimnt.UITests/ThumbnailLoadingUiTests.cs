using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task Thumbnails_ShouldLoadBeyondBothFormerLimitsEvenWhenLibraryIsFiltered()
    {
        await using var scope = await UiScope.CreateAsync();
        var imagePath = Path.Combine(scope.Root, "thumbnail.png");
        using (var image = new WriteableBitmap(new PixelSize(16, 16), new Vector(96, 96)))
            image.Save(imagePath);
        var repository = scope.Services.GetRequiredService<IModRepository>();
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var profileId = await profiles.CreateProfileAsync("Thumbnail regression");
        await profiles.SetActiveProfileAsync(profileId);
        for (var index = 0; index < 90; index++)
        {
            var mod = new Mod
            {
                Id = Guid.NewGuid(), Name = $"HD2Mimnt Test Thumbnail {index:D3}",
                Path = scope.Root, ThumbnailPath = imagePath
            };
            await repository.AddAsync(mod);
            await profiles.AddModToProfileAsync(profileId, mod.Id);
        }

        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var library = main.LibraryViewModel;
        library.SearchText = "no matching mod";
        await main.InitializeAsync();
        for (var attempt = 0; attempt < 250 &&
            (main.ProfileViewModel.ProfileMods.Count != 90 || main.ProfileViewModel.ProfileMods.Any(mod => mod.Icon == null)); attempt++)
            await Task.Delay(20);
        Assert.Equal(90, main.ProfileViewModel.ProfileMods.Count);
        Assert.All(main.ProfileViewModel.ProfileMods, mod => Assert.NotNull(mod.Icon));

        library.SearchText = string.Empty;
        await library.ExecuteSearchCommand.ExecuteAsync(null);
        for (var attempt = 0; attempt < 250 && library.FilteredMods.Any(mod => mod.Icon == null); attempt++)
            await Task.Delay(20);
        Assert.Equal(90, library.FilteredMods.Count);
        Assert.All(library.FilteredMods, mod => Assert.NotNull(mod.Icon));
    }

    [AvaloniaFact]
    public async Task Thumbnail_ShouldRetryAfterTemporaryReadFailureAndNotifyDetailBinding()
    {
        await using var scope = await UiScope.CreateAsync();
        var imagePath = Path.Combine(scope.Root, "retry.png");
        using (var image = new WriteableBitmap(new PixelSize(16, 16), new Vector(96, 96)))
            image.Save(imagePath);
        var mod = new Mod { ThumbnailPath = imagePath };
        var first = new ModItemViewModel(mod);
        using (var locked = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await first.LoadIconAsync();
        Assert.Null(first.Icon);

        var detailNotified = false;
        first.PropertyChanged += (_, args) => detailNotified |= args.PropertyName == nameof(first.IconBitmap);
        await first.LoadIconAsync();
        Assert.NotNull(first.Icon);
        Assert.True(detailNotified);
        var refreshed = new ModItemViewModel(mod);
        await refreshed.LoadIconAsync();
        Assert.Same(first.Icon, refreshed.Icon);
    }
}
