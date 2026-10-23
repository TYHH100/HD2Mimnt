using Avalonia;
using Avalonia.Headless.XUnit;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.Tests.UI;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task ModCardPreview_LibraryAndProfile_ShouldDecodeOriginalInsteadOfThumbnail()
    {
        await using var scope = await UiScope.CreateAsync();
        var source = Path.Combine(scope.Root, "original-preview-source");
        Directory.CreateDirectory(source);
        using (var image = new System.Drawing.Bitmap(1024, 768))
        {
            using var graphics = System.Drawing.Graphics.FromImage(image);
            graphics.Clear(System.Drawing.Color.Red);
            image.Save(Path.Combine(source, "cover.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        await File.WriteAllTextAsync(Path.Combine(source, "body.patch"), "content");
        var mods = scope.Services.GetRequiredService<ModService>();
        var id = await mods.ImportModAsync(source);
        var mod = (await scope.Services.GetRequiredService<IModRepository>().GetByIdAsync(id))!;
        using (var thumbnail = new System.Drawing.Bitmap(mod.ThumbnailPath))
            Assert.Equal(256, thumbnail.Width);

        var preview = scope.Services.GetRequiredService<ImagePreviewService>();
        try
        {
            await scope.Services.GetRequiredService<LibraryViewModel>().ShowModImagePreviewAsync(id, "Library");
            Assert.True(preview.IsOpen);
            Assert.Equal(new PixelSize(1024, 768), preview.Image!.PixelSize);
            preview.Close();
            Assert.Null(preview.Image);
            await scope.Services.GetRequiredService<ProfileViewModel>().ShowModImagePreviewAsync(id, "Profile");
            Assert.True(preview.IsOpen);
            Assert.Equal(new PixelSize(1024, 768), preview.Image!.PixelSize);

            var read = new TaskCompletionSource<byte[]?>();
            var pending = preview.ShowOriginalAsync(() => read.Task, "Closed");
            preview.Close();
            read.SetResult(await File.ReadAllBytesAsync(Path.Combine(source, "cover.png")));
            await pending;
            Assert.False(preview.IsOpen);
            Assert.Null(preview.Image);
        }
        finally { preview.Close(); }
    }
}
