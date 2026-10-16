using System.IO.Compression;
using System.Text.Json.Nodes;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Services;
using HD2Mimnt.Infrastructure.Archive;
using HD2Mimnt.Infrastructure.Storage;
using SharpCompress.Archives;

namespace HD2Mimnt.Tests.Services;

public sealed class ModCreationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-Creation-{Guid.NewGuid():N}");
    private readonly ModCreationService _service;
    private readonly StorageSettingsService _storage;
    private string Source => Path.Combine(_root, "source");
    public ModCreationServiceTests()
    {
        Directory.CreateDirectory(Source);
        _storage = new(new StorageLocationStore(Path.Combine(_root, "data")));
        _service = new(_storage);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private ModCreationDocument Draft()
    {
        var document = _service.Create(); document.SourceDirectory = Source;
        document.Manifest.Name = "测试模组"; document.Manifest.Author = "作者";
        document.Manifest.AuthorUrl = "https://afdian.com/a/example";
        return document;
    }

    [Fact]
    public async Task PreviewImages_ShouldStayInsideSourceAndKeepExistingOutputOnCancellation()
    {
        var png = new byte[] { 137, 80, 78, 71 };
        await File.WriteAllBytesAsync(Path.Combine(Source, "cover.png"), png);
        Assert.Equal(png, await _service.ReadPreviewImageAsync(Source, "cover.png", default));
        await File.WriteAllBytesAsync(Path.Combine(_root, "outside.png"), png);
        Assert.Null(await _service.ReadPreviewImageAsync(Source, "../outside.png", default));
        Assert.Null(await _service.ReadPreviewImageAsync(Source, "missing.png", default));
        var output = Path.Combine(_root, "preview.png");
        await File.WriteAllTextAsync(output, "existing");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.SavePreviewImageAsync(output, png, cancellation.Token));
        Assert.Equal("existing", await File.ReadAllTextAsync(output));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        await _service.SavePreviewImageAsync(output, png, default);
        Assert.Equal(png, await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task ReadDirectoryAsync_ShouldGenerateLegacyFolderNamesImagesAndMappings()
    {
        foreach (var folder in new[] { "appearance/red", "appearance/blue", "Models/helmet/gold", "empty" })
            Directory.CreateDirectory(Path.Combine(Source, folder));
        foreach (var image in new[] { "cover.PNG", "second.jpg", "appearance/preview.jpg", "appearance/red/red.png", "Models/helmet/helmet.bmp", "Models/helmet/gold/gold.webp" })
            await File.WriteAllBytesAsync(Path.Combine(Source, image), [1]);
        await File.WriteAllTextAsync(Path.Combine(Source, "123.patch_0"), "patch");
        var directory = await _service.ReadDirectoryAsync(Source);
        Assert.Equal("source", directory.Name); Assert.Equal(".", directory.RelativePath); Assert.Equal("cover.PNG", directory.Image);
        var options = _service.CreateDirectoryOptions(directory, "基础文件");
        Assert.Equal(4, options.Count);
        Assert.Equal(["."], options.Single(option => option.Name == "基础文件").Include);
        var appearance = options.Single(option => option.Name == "appearance");
        Assert.True(appearance.DefaultEnabled); Assert.Equal(["appearance"], appearance.Include);
        Assert.Equal("appearance/preview.jpg", appearance.Image);
        Assert.Equal(2, appearance.SubOptions!.Count);
        var red = appearance.SubOptions.Single(sub => sub.Name == "red");
        Assert.Equal("appearance/red/red.png", red.Image); Assert.Equal(["appearance/red"], red.Include);
        var helmet = options.Single(option => option.Name == "helmet");
        Assert.Equal(["Models/helmet"], helmet.Include); Assert.Equal("Models/helmet/helmet.bmp", helmet.Image);
        Assert.Equal("Models/helmet/gold/gold.webp", Assert.Single(helmet.SubOptions!).Image);
        Assert.Equal(["empty"], options.Single(option => option.Name == "empty").Include);
        Assert.DoesNotContain(options, option => option.Name == "Models");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ReadDirectoryAsync(Source, cancellation.Token));
    }

    [Fact]
    public async Task ExportArchiveAsync_ShouldIncludeAllFilesGeneratedManifestAndEmptyDirectoriesWithoutChangingSource()
    {
        var document = Draft();
        Directory.CreateDirectory(Path.Combine(Source, "variants", "red"));
        Directory.CreateDirectory(Path.Combine(Source, "empty"));
        await File.WriteAllTextAsync(Path.Combine(Source, "variants", "red", "123.patch_0"), "patch content");
        await File.WriteAllBytesAsync(Path.Combine(Source, "icon.png"), [1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(Source, "manifest.json"), "original manifest");
        await File.WriteAllTextAsync(Path.Combine(Source, ManifestPackage.MetadataFileName), "original metadata");
        document.Manifest.IconPath = "icon.png";
        document.Manifest.Options = [new() { Name = "颜色", Include = null, DefaultEnabled = false,
            SubOptions = [new() { Name = "红色", Include = ["variants/red"], Image = "icon.png" }, new() { Name = "不更换", Include = [] }] }];
        var zip = Path.Combine(_root, "mod.zip");
        await _service.ExportArchiveAsync(document, zip);
        Assert.Equal("original manifest", await File.ReadAllTextAsync(Path.Combine(Source, "manifest.json")));
        Assert.Equal("original metadata", await File.ReadAllTextAsync(Path.Combine(Source, ManifestPackage.MetadataFileName)));
        using (var archive = ZipFile.OpenRead(zip))
        {
            Assert.Single(archive.Entries.Where(entry => entry.FullName == "manifest.json"));
            Assert.Single(archive.Entries.Where(entry => entry.FullName == ManifestPackage.MetadataFileName));
            Assert.NotNull(archive.GetEntry("variants/red/123.patch_0"));
            Assert.NotNull(archive.GetEntry("empty/")); Assert.NotNull(archive.GetEntry("icon.png"));
            using var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
            var root = JsonNode.Parse(await reader.ReadToEndAsync())!;
            Assert.Null(root["Author"]); Assert.Null(root["ModVersion"]); Assert.Null(root["AuthorUrl"]);
            Assert.Null(root["Options"]![0]!["DefaultEnabled"]);
            var parsed = new ManifestParser().ParseFromJson(root.ToJsonString())!;
            Assert.Equal(ManifestVersion.V1, parsed.ManifestVersion); Assert.Equal(document.Manifest.Guid, parsed.Guid);
            Assert.Equal("1.0.0", parsed.Version);
            var option = Assert.Single(parsed.Options!); Assert.Null(option.Include); Assert.Null(option.DefaultEnabled);
            Assert.Equal(["variants/red"], option.SubOptions![0].Include); Assert.Empty(option.SubOptions[1].Include!);
        }
        var extracted = Path.Combine(_root, "extracted"); ZipFile.ExtractToDirectory(zip, extracted);
        var loaded = await _service.LoadAsync(Path.Combine(extracted, "manifest.json"));
        _service.Validate(loaded);
        Assert.Equal("测试模组", loaded.Manifest.Name);
        Assert.Equal("作者", loaded.Manifest.Author); Assert.Equal("1.0.0", loaded.Manifest.Version);
        Assert.Equal(document.Manifest.AuthorUrl, loaded.Manifest.AuthorUrl);
        Assert.False(Assert.Single(loaded.Manifest.Options!).DefaultEnabled);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LoadSaveAsync_ShouldPreserveUnknownFieldsNullAndEmptyMappingWhenOptionsAreRemoved()
    {
        var path = Path.Combine(Source, "manifest.json");
        await File.WriteAllTextAsync(path, """
            {"Version":1,"Guid":"11111111-1111-1111-1111-111111111111","Name":"original","Author":"author",
             "ModVersion":"2.3","Extra":{"license":"custom"},"NexusData":{"ModId":123,"Custom":true},
             "Options":[{"Name":"remove","Include":[],"ExtraOption":1},
                        {"Name":"keep","CustomOption":2,"SubOptions":[{"Name":"none","Include":[],"CustomSub":3}]}]}
            """);
        var document = await _service.LoadAsync(path);
        document.Manifest.Name = "edited"; document.Manifest.Options!.RemoveAt(0);
        await _service.SaveAsync(document, path);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.Null(root["Author"]); Assert.Null(root["ModVersion"]); Assert.Null(root["Extra"]);
        Assert.Null(root["NexusData"]!["Custom"]);
        var option = Assert.Single(root["Options"]!.AsArray())!;
        Assert.Null(option["CustomOption"]); Assert.Null(option["Include"]);
        Assert.Null(option["SubOptions"]![0]!["CustomSub"]);
        Assert.Empty(option["SubOptions"]![0]!["Include"]!.AsArray());
        var combined = await ManifestPackage.ReadAsync(path);
        Assert.Equal("custom", combined["Extra"]!["license"]!.GetValue<string>());
        Assert.True(combined["NexusData"]!["Custom"]!.GetValue<bool>());
        Assert.Equal(2, combined["Options"]![0]!["CustomOption"]!.GetValue<int>());
        Assert.Equal(3, combined["Options"]![0]!["SubOptions"]![0]!["CustomSub"]!.GetValue<int>());
        var reloaded = await _service.LoadAsync(path);
        Assert.Equal("author", reloaded.Manifest.Author); Assert.Equal("2.3", reloaded.Manifest.Version);
        Assert.Equal("2.3", combined["ModVersion"]!.GetValue<string>()); Assert.Equal(1, root["Version"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("nested/../../outside")]
    [InlineData("C:/outside")]
    public async Task SaveAsync_OutsideInclude_ShouldRejectBeforeChangingManifest(string include)
    {
        var document = Draft(); document.Manifest.Options = [new() { Name = "bad", Include = [include] }];
        var path = Path.Combine(Source, "manifest.json"); await File.WriteAllTextAsync(path, "original");
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.SaveAsync(document, path));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ExportArchiveAsync_LockedFile_ShouldKeepExistingDestinationAndCleanTemporaryArchive()
    {
        var document = Draft(); var path = Path.Combine(Source, "locked.patch_0");
        await File.WriteAllTextAsync(path, "content");
        var zip = Path.Combine(_root, "mod.zip"); await File.WriteAllTextAsync(zip, "existing archive");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        await Assert.ThrowsAnyAsync<IOException>(() => _service.ExportArchiveAsync(document, zip));
        Assert.Equal("existing archive", await File.ReadAllTextAsync(zip));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExportArchiveAsync_Cancelled_ShouldNotPublishOrLeaveTemporaryFiles()
    {
        var document = Draft(); var zip = Path.Combine(_root, "mod.zip");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ExportArchiveAsync(document, zip, cancellation.Token));
        Assert.False(File.Exists(zip)); Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExportArchiveAsync_CancelDuringWrite_ShouldKeepDestinationAndCleanTemporaryArchive()
    {
        var document = Draft(); var zip = Path.Combine(_root, "mod.zip");
        await File.WriteAllTextAsync(zip, "existing archive");
        await File.WriteAllBytesAsync(Path.Combine(Source, "large.patch_0"), System.Security.Cryptography.RandomNumberGenerator.GetBytes(16 * 1024 * 1024));
        using var cancellation = new CancellationTokenSource();
        using var watcher = new FileSystemWatcher(_root, "*.tmp");
        watcher.Created += (_, _) => cancellation.Cancel(); watcher.EnableRaisingEvents = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ExportArchiveAsync(document, zip, cancellation.Token));
        Assert.Equal("existing archive", await File.ReadAllTextAsync(zip));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExportArchiveAsync_InsideSource_ShouldReject()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ExportArchiveAsync(Draft(), Path.Combine(Source, "mod.zip")));
        Assert.Empty(Directory.GetFiles(Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAsync_LockedManifest_ShouldRestoreMetadataAndCleanTemporaryFiles(bool existingMetadata)
    {
        var path = Path.Combine(Source, "manifest.json");
        var metadataPath = Path.Combine(Source, ManifestPackage.MetadataFileName);
        await File.WriteAllTextAsync(path, "original manifest");
        if (existingMetadata) await File.WriteAllTextAsync(metadataPath, "original metadata");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Record.ExceptionAsync(() => _service.SaveAsync(Draft(), path));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Save unexpectedly succeeded.");
        }
        Assert.Equal("original manifest", await File.ReadAllTextAsync(path));
        if (existingMetadata) Assert.Equal("original metadata", await File.ReadAllTextAsync(metadataPath));
        else Assert.False(File.Exists(metadataPath));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Metadata_ShouldNeverOverrideStandardFieldsOrApplyToAnotherManifest()
    {
        var document = Draft(); document.Manifest.Options = [new() { Name = "option", Include = [], DefaultEnabled = false }];
        var path = Path.Combine(Source, "manifest.json"); await _service.SaveAsync(document, path);
        var metadataPath = Path.Combine(Source, ManifestPackage.MetadataFileName);
        var metadata = ManifestPackage.Parse(await File.ReadAllTextAsync(metadataPath));
        metadata["Extensions"]!["Name"] = "override";
        metadata["Extensions"]!["Options"]![0]!["Include"] = new JsonArray("../outside");
        await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString());
        var parsed = (await new ManifestParser().ParseFromFileAsync(path))!;
        Assert.Equal(document.Manifest.Name, parsed.Name); Assert.Empty(parsed.Options![0].Include!);
        Assert.False(parsed.Options[0].DefaultEnabled);
        metadata["ManifestGuid"] = Guid.NewGuid().ToString();
        await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.LoadAsync(path));
        parsed = (await new ManifestParser().ParseFromFileAsync(path))!;
        Assert.Equal(document.Manifest.Name, parsed.Name); Assert.Null(parsed.Options![0].DefaultEnabled);
        Assert.NotNull(new ManifestParser().ParseFromFile(path));
        await File.WriteAllTextAsync(metadataPath, "invalid json");
        Assert.NotNull(await new ManifestParser().ParseFromFileAsync(path));
    }

    [Fact]
    public async Task SaveAsync_ReservedMetadataFileName_ShouldRejectWithoutChangingEitherFile()
    {
        var path = Path.Combine(Source, ManifestPackage.MetadataFileName);
        await File.WriteAllTextAsync(path, "original metadata");
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.SaveAsync(Draft(), path));
        Assert.Equal("original metadata", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(Path.Combine(Source, "manifest.json")));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows")]
    [InlineData("https://secret@example.com/author")]
    public async Task SaveAsync_InvalidAuthorWebsite_ShouldRejectWithoutPublishing(string website)
    {
        var document = Draft(); document.Manifest.AuthorUrl = website;
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.SaveAsync(document, Path.Combine(Source, "manifest.json")));
        Assert.Empty(Directory.GetFiles(Source));
    }

    [Theory]
    [InlineData(ManifestVersion.Legacy)]
    [InlineData(ManifestVersion.V1)]
    public async Task Workflows_ShouldReadSeparateMetadataAndExcludeItFromDeploymentAndCleanup(ManifestVersion version)
    {
        var manifest = Draft().Manifest; manifest.ManifestVersion = version;
        manifest.Options = [new() { Name = "option", Include = [], DefaultEnabled = false }];
        manifest.LegacyOptions = ["red", "blue"];
        var path = Path.Combine(Source, "manifest.json"); new ManifestParser().SaveToFile(manifest, path);
        await File.WriteAllTextAsync(Path.Combine(Source, "123.patch_0"), "patch");
        HD2Mimnt.Core.Interfaces.IWorkflow workflow = version == ManifestVersion.V1
            ? new HD2Mimnt.Infrastructure.Workflows.V1Workflow() : new HD2Mimnt.Infrastructure.Workflows.LegacyWorkflow();
        var loaded = (await workflow.ParseManifestAsync(Source))!;
        Assert.Equal(manifest.Author, loaded.Author); Assert.Equal(manifest.AuthorUrl, loaded.AuthorUrl);
        Assert.Equal(manifest.Version, loaded.Version);
        if (version == ManifestVersion.V1) Assert.False(loaded.Options![0].DefaultEnabled);
        Assert.Equal("123.patch_0", Assert.Single(await workflow.GetDeploymentFilesAsync(Source, loaded)).TargetRelativePath);
        Assert.Equal("123.patch_0", Assert.Single(await workflow.GetCleanupFilesAsync(Source, loaded)));
    }

    [Fact]
    public async Task SaveAsync_RemovedAuthorWebsite_ShouldNotRestorePreviousValue()
    {
        var path = Path.Combine(Source, "manifest.json"); await _service.SaveAsync(Draft(), path);
        var document = await _service.LoadAsync(path); document.Manifest.AuthorUrl = "";
        await _service.SaveAsync(document, path);
        Assert.Null((await _service.LoadAsync(path)).Manifest.AuthorUrl);
        Assert.Null(ManifestPackage.Parse(await File.ReadAllTextAsync(Path.Combine(Source, ManifestPackage.MetadataFileName)))["Extensions"]!["AuthorUrl"]);
    }

    [Fact]
    public async Task LoadAsync_Legacy_ShouldLeaveOriginalUntouched()
    {
        var path = Path.Combine(Source, "manifest.json"); const string legacy = "{\"Name\":\"legacy\",\"Options\":[\"red\",\"blue\"]}";
        await File.WriteAllTextAsync(path, legacy);
        var document = await _service.LoadAsync(path);
        Assert.Equal(ManifestVersion.Legacy, document.Manifest.ManifestVersion);
        Assert.Equal(["red", "blue"], document.Manifest.LegacyOptions);
        Assert.Equal(legacy, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Legacy_SaveExportReload_ShouldKeepDirectoryOptionsAndSeparateMetadata()
    {
        var document = Draft();
        document.Manifest.ManifestVersion = ManifestVersion.Legacy;
        document.Manifest.Version = "3.2";
        document.Manifest.LegacyOptions = ["Models/red", "blue"];
        foreach (var path in document.Manifest.LegacyOptions)
        {
            Directory.CreateDirectory(Path.Combine(Source, path));
            await File.WriteAllTextAsync(Path.Combine(Source, path, "123.patch_0"), "patch");
        }
        // 隐藏的 V1 草稿不影响 Legacy 保存校验。
        document.Manifest.Options = [new() { Name = "", Include = ["missing"] }];
        var manifestPath = Path.Combine(Source, "manifest.json");
        await _service.SaveAsync(document, manifestPath);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
        Assert.Null(root["Version"]); Assert.Null(root["Author"]); Assert.Null(root["ModVersion"]);
        Assert.Equal(["Models/red", "blue"], root["Options"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal(root.ToJsonString(), JsonNode.Parse(_service.Preview(document))!.ToJsonString());
        var loaded = await _service.LoadAsync(manifestPath);
        Assert.Equal(ManifestVersion.Legacy, loaded.Manifest.ManifestVersion);
        Assert.Equal(document.Manifest.Guid, loaded.Manifest.Guid);
        Assert.Equal("3.2", loaded.Manifest.Version); Assert.Equal("作者", loaded.Manifest.Author);
        var zip = Path.Combine(_root, "legacy.zip");
        await _service.ExportArchiveAsync(loaded, zip);
        var extracted = Path.Combine(_root, "legacy-extracted");
        ZipFile.ExtractToDirectory(zip, extracted);
        var exported = await _service.LoadAsync(Path.Combine(extracted, "manifest.json"));
        _service.Validate(exported);
        Assert.Equal(ManifestVersion.Legacy, exported.Manifest.ManifestVersion);
        Assert.Equal(document.Manifest.LegacyOptions, exported.Manifest.LegacyOptions);
        Assert.Equal("patch", await File.ReadAllTextAsync(Path.Combine(extracted, "Models/red/123.patch_0")));
        document.Manifest.LegacyOptions = ["../outside"];
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.SaveAsync(document, manifestPath));
        Assert.Equal(root.ToJsonString(), JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.ToJsonString());
    }

    [Theory]
    [InlineData("7z", ModArchiveCompression.Store)]
    [InlineData("7z", ModArchiveCompression.Fast)]
    [InlineData("7z", ModArchiveCompression.Normal)]
    [InlineData("7z", ModArchiveCompression.High)]
    [InlineData("7z", ModArchiveCompression.Ultra)]
    [InlineData("zip", ModArchiveCompression.Store)]
    [InlineData("zip", ModArchiveCompression.Fast)]
    [InlineData("zip", ModArchiveCompression.Normal)]
    [InlineData("zip", ModArchiveCompression.High)]
    [InlineData("zip", ModArchiveCompression.Ultra)]
    public async Task ExportArchiveAsync_CompressionChoices_ShouldCreateRealArchivesWithCompleteContent(string extension, ModArchiveCompression level)
    {
        var document = Draft(); var folder = Path.Combine(Source, "颜色 选项"); Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(Source, "空目录"));
        var content = string.Concat(Enumerable.Repeat("HD2 patch data 中文 0123456789\n", 16000));
        await File.WriteAllTextAsync(Path.Combine(folder, "外观.patch_0"), content);
        await File.WriteAllTextAsync(Path.Combine(Source, "manifest.json"), "old source manifest");
        await File.WriteAllTextAsync(Path.Combine(Source, ManifestPackage.MetadataFileName), "old source metadata");
        var output = Path.Combine(_root, "模组 包." + extension);
        await _service.ExportArchiveAsync(document, output, level);
        var signature = (await File.ReadAllBytesAsync(output))[..6];
        if (extension == "7z") Assert.Equal(new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c }, signature);
        else Assert.Equal(new byte[] { 0x50, 0x4b }, signature[..2]);
        using (var archive = ArchiveFactory.OpenArchive(output))
        {
            Assert.Single(archive.Entries, entry => entry.Key == "manifest.json");
            Assert.Single(archive.Entries, entry => entry.Key == ManifestPackage.MetadataFileName);
            Assert.Contains(archive.Entries, entry => entry.IsDirectory && entry.Key!.TrimEnd('/', '\\') == "空目录");
        }
        var extracted = Path.Combine(_root, "unpacked");
        await new SharpCompressArchiveExtractor().ExtractToDirectoryAsync(output, extracted);
        Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(extracted, "颜色 选项", "外观.patch_0")));
        var loaded = await _service.LoadAsync(Path.Combine(extracted, "manifest.json"));
        Assert.Equal(document.Manifest.Guid, loaded.Manifest.Guid);
        Assert.Equal("作者", loaded.Manifest.Author);
        Assert.Equal("old source manifest", await File.ReadAllTextAsync(Path.Combine(Source, "manifest.json")));
        Assert.Equal("old source metadata", await File.ReadAllTextAsync(Path.Combine(Source, ManifestPackage.MetadataFileName)));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
        if (Directory.Exists(_storage.Current.TempPath)) Assert.Empty(Directory.GetDirectories(_storage.Current.TempPath));
    }

    [Fact]
    public async Task ExportSevenZip_Ultra_ShouldCompressMoreThanStore()
    {
        await File.WriteAllBytesAsync(Path.Combine(Source, "repeated.patch_0"), new byte[1024 * 1024]);
        var stored = Path.Combine(_root, "store.7z"); var compressed = Path.Combine(_root, "ultra.7z");
        await _service.ExportArchiveAsync(Draft(), stored, ModArchiveCompression.Store);
        await _service.ExportArchiveAsync(Draft(), compressed, ModArchiveCompression.Ultra);
        Assert.True(new FileInfo(compressed).Length < new FileInfo(stored).Length / 10);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportSevenZip_CancelOrReadFailure_ShouldKeepDestinationAndCleanStaging(bool cancel)
    {
        var document = Draft(); var output = Path.Combine(_root, "existing.7z");
        await File.WriteAllTextAsync(output, "existing archive");
        var file = Path.Combine(Source, "large.patch_0");
        await File.WriteAllBytesAsync(file, System.Security.Cryptography.RandomNumberGenerator.GetBytes(16 * 1024 * 1024));
        using var cancellation = new CancellationTokenSource();
        using var watcher = new FileSystemWatcher(_root, "*.tmp");
        watcher.Created += (_, _) => cancellation.Cancel(); watcher.EnableRaisingEvents = cancel;
        using var locked = cancel ? null : new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ExportArchiveAsync(document, output, cancellation.Token));
        else
            await Assert.ThrowsAsync<IOException>(() => _service.ExportArchiveAsync(document, output));
        Assert.Equal("existing archive", await File.ReadAllTextAsync(output));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(_storage.Current.TempPath));
    }
}
