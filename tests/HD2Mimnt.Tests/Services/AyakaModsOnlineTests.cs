using System.IO.Compression;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Platforms;
using HD2Mimnt.Infrastructure.Repositories;
using Moq;

namespace HD2Mimnt.Tests.Services;

public sealed class AyakaModsOnlineTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BrowserUpdate_ShouldRejectChangedBindingOrUnavailableAttachmentBeforeReadingArchive(bool changedBinding)
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-AyakaRejected-" + Guid.NewGuid().ToString("N"));
        try
        {
            var link = new ModPlatformLink("ayakamods", "helldivers2", "42", "https://ayakamods.com/mods/42/");
            var mod = new Mod { Id = Guid.NewGuid(), PlatformId = "ayakamods", PlatformModId = changedBinding ? "43" : "42" };
            mod.PlatformLink = link with { ModId = mod.PlatformModId };
            var repository = new Mock<IModRepository>();
            repository.Setup(r => r.GetByIdAsync(mod.Id, It.IsAny<CancellationToken>())).ReturnsAsync(mod);
            var platform = new Mock<IModPlatform>();
            platform.SetupGet(p => p.Id).Returns("ayakamods");
            platform.Setup(p => p.GetFilesAsync("42", null, It.IsAny<CancellationToken>())).ReturnsAsync(new List<PlatformModFile>());
            var registry = new PlatformRegistry(); registry.RegisterPlatform(platform.Object);
            var online = new ModOnlineService(registry, new ModService(repository.Object, root), new(registry));
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => online.UpdateFromBrowserFileAsync(mod.Id, link,
                new PlatformModFile { Id = "ayaka-file:9:10", Version = "2.0" }, "must-not-open.zip"));
            Assert.Contains(changedBinding ? "链接已变更" : "已不可用", exception.Message);
            platform.Verify(p => p.GetFilesAsync("42", null, It.IsAny<CancellationToken>()), changedBinding ? Times.Never() : Times.Once());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AyakaLinkAndUpdate_ShouldPersistProviderAndPreserveOriginalModAndProfile(bool fromBrowser)
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-AyakaOnline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "test.db");
            var mod = new Mod { Id = Guid.NewGuid(), Name = "Local name", Version = "1.0", Tags = new() { "keep" },
                Path = Path.Combine(root, "storage", "original"), Hash = "old" };
            const string patch = "0123456789abcdef.patch_0";
            Directory.CreateDirectory(mod.Path);
            await File.WriteAllTextAsync(Path.Combine(mod.Path, patch), "old");
            var profile = new Profile { Id = Guid.NewGuid(), Name = "Profile", Mods = new() {
                new() { ModId = mod.Id, Enabled = false, Order = 7, OptionsJson = "keep options" } } };
            var file = new PlatformModFile { Id = "ayaka-file:9:10", WebsiteFileId = "10", Version = "2.0" };
            var platform = new Mock<IModPlatform>();
            var downloader = platform.As<IModFileDownloadPlatform>();
            platform.SetupGet(p => p.Id).Returns("ayakamods");
            platform.Setup(p => p.GetModInfoAsync("42", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PlatformModInfo { Id = "42", Name = "Remote" });
            platform.Setup(p => p.GetVersionsAsync("42", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PlatformModVersion> { new() { Version = "2.0" } });
            platform.Setup(p => p.GetFilesAsync("42", null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PlatformModFile> { file });
            using var bytes = new MemoryStream();
            using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(zip.CreateEntry(patch).Open())) writer.Write("new patch");
            file.Size = bytes.Length;
            var browserArchive = Path.Combine(root, "download.zip");
            if (fromBrowser) await File.WriteAllBytesAsync(browserArchive, bytes.ToArray());
            downloader.Setup(p => p.DownloadFileAsync("42", file.Id, It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream(bytes.ToArray()));
            var registry = new PlatformRegistry(); registry.RegisterPlatform(platform.Object);
            using (var database = new HD2MimntDbContext(databasePath))
            {
                await database.InitializeDatabaseAsync();
                var repository = new ModRepository(database);
                await repository.AddAsync(mod);
                await new ProfileRepository(database).AddAsync(profile);
                var mods = new ModService(repository, Path.Combine(root, "storage"));
                var links = new ModLinkService(registry, mods);
                var link = await links.FetchAsync("https://ayakamods.com/mods/armor.42/?tab=history");
                await links.SaveAsync(mod.Id, link);
                Assert.Null((await repository.GetByIdAsync(mod.Id))!.NexusModId);
            }
            using (var database = new HD2MimntDbContext(databasePath))
            {
                await database.InitializeDatabaseAsync();
                var repository = new ModRepository(database);
                var bound = (await repository.GetByIdAsync(mod.Id))!;
                Assert.Equal("ayakamods", bound.PlatformId);
                Assert.Equal("42", bound.PlatformLink!.ModId);
                var mods = new ModService(repository, Path.Combine(root, "storage"));
                var online = new ModOnlineService(registry, mods, new(registry));
                var info = await online.CheckAsync(mod.Id);
                Assert.Equal(file.Id, Assert.Single(info.Files).Id);
                if (fromBrowser)
                {
                    await online.UpdateFromBrowserFileAsync(mod.Id, bound.PlatformLink, file, browserArchive);
                    Assert.Equal(bytes.ToArray(), await File.ReadAllBytesAsync(browserArchive));
                }
                else await online.UpdateAsync(mod.Id, bound.PlatformLink, file);
            }
            using (var database = new HD2MimntDbContext(databasePath))
            {
                await database.InitializeDatabaseAsync();
                var saved = (await new ModRepository(database).GetByIdAsync(mod.Id))!;
                Assert.Equal("Local name", saved.Name);
                Assert.Equal("2.0", saved.Version);
                Assert.Equal(ModSource.AyakaMods, saved.Source);
                Assert.Equal(file.Id, saved.PlatformLink!.InstalledFileVersionId);
                Assert.Null(saved.NexusModId);
                Assert.Equal(mod.Tags, saved.Tags);
                Assert.Equal("new patch", await File.ReadAllTextAsync(Path.Combine(saved.Path, patch)));
                var assignment = Assert.Single((await new ProfileRepository(database).GetByIdAsync(profile.Id))!.Mods);
                Assert.Equal(mod.Id, assignment.ModId);
                Assert.False(assignment.Enabled);
                Assert.Equal(7, assignment.Order);
                Assert.Equal("keep options", assignment.OptionsJson);
            }
            downloader.Verify(p => p.DownloadFileAsync("42", file.Id, It.IsAny<IProgress<DownloadProgress>>(), It.IsAny<CancellationToken>()),
                fromBrowser ? Times.Never() : Times.Once());
        }
        finally { Directory.Delete(root, true); }
    }
}
