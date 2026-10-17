using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Archive;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using HD2Mimnt.Application.Services;
using System.IO.Compression;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;

namespace HD2Mimnt.Tests.Services;

/// <summary>
/// 导入命名回归测试：真实模组压缩包常带一层同名子文件夹，
/// manifest 必须能被找到（名字取 manifest.Name），缺失时回退压缩包文件名而不是临时解压目录的 GUID。
/// </summary>
public sealed class ModImportServiceTests : IDisposable
{
    private readonly HD2MimntDbContext _dbContext;
    private readonly IModRepository _modRepository;
    private readonly string _databasePath;
    private readonly string _storageDirectory;
    private readonly string _tempDirectory;

    public ModImportServiceTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"hd2mimnt-import-{Guid.NewGuid():N}.db");
        _storageDirectory = Path.Combine(Path.GetTempPath(), $"hd2mimnt-import-{Guid.NewGuid():N}");
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"hd2mimnt-import-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _dbContext = new HD2MimntDbContext(_databasePath);
        _dbContext.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _modRepository = new ModRepository(_dbContext);
    }

    [Fact]
    public async Task ImportModAsync_WithNestedManifest_ShouldUseManifestName()
    {
        var zipPath = await CreateZipAsync(zipRoot =>
        {
            var modDir = Path.Combine(zipRoot, "Super Armor Mod");
            Directory.CreateDirectory(modDir);
            File.WriteAllText(Path.Combine(modDir, "manifest.json"),
                """{"name":"超级护甲","version":"2.5.0","author":"Author"}""", Encoding.UTF8);
            File.WriteAllText(Path.Combine(modDir, "body.patch"), "content");
        });

        var importedId = await CreateService().ImportModAsync(zipPath);

        var mod = (await _modRepository.GetByIdAsync(importedId))!;
        Assert.Equal("超级护甲", mod.Name);
        Assert.Equal("2.5.0", mod.Version);
        // Mod 根目录应是 manifest 所在的子目录，patch 文件直接可寻
        Assert.True(File.Exists(Path.Combine(mod.Path, "body.patch")));
    }

    [Fact]
    public async Task ImportModAsync_WithoutManifest_ShouldFallBackToZipFileName()
    {
        var zipPath = Path.Combine(_tempDirectory, "Cool Weapon Pack.zip");
        var contentRoot = Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        File.WriteAllText(Path.Combine(contentRoot, "weapon.patch"), "content");
        ZipFile.CreateFromDirectory(contentRoot, zipPath);

        var importedId = await CreateService().ImportModAsync(zipPath);

        var mod = (await _modRepository.GetByIdAsync(importedId))!;
        Assert.Equal("Cool Weapon Pack", mod.Name);
        Assert.False(mod.Name.Contains("hd2mimnt_import_"), $"不应使用临时目录名: {mod.Name}");
    }

    [Fact]
    public async Task ImportModAsync_WithRootManifest_ShouldUseManifestName()
    {
        var zipPath = await CreateZipAsync(zipRoot =>
        {
            File.WriteAllText(Path.Combine(zipRoot, "manifest.json"),
                """{"name":"根目录模组","version":"1.1.0"}""", Encoding.UTF8);
            File.WriteAllText(Path.Combine(zipRoot, "patch.patch"), "content");
        });

        var importedId = await CreateService().ImportModAsync(zipPath);

        var mod = (await _modRepository.GetByIdAsync(importedId))!;
        Assert.Equal("根目录模组", mod.Name);
    }

    /// <summary>
    /// 非 ZIP 格式（tar.gz）也应走压缩包解压路径导入，
    /// 验证复合压缩格式仍使用流式读取链路。
    /// </summary>
    [Fact]
    public async Task ImportModAsync_WithTarGzArchive_ShouldExtractAndImport()
    {
        var tarGzPath = Path.Combine(_tempDirectory, $"{Guid.NewGuid():N}.tar.gz");
        var contentRoot = Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        File.WriteAllText(Path.Combine(contentRoot, "manifest.json"),
            """{"name":"Tgz 模组","version":"1.0.0"}""", Encoding.UTF8);
        File.WriteAllText(Path.Combine(contentRoot, "armor.patch"), "content");

        using (var targetStream = File.Create(tarGzPath))
        using (var gzipStream = new GZipStream(targetStream, CompressionMode.Compress))
        using (var writer = WriterFactory.OpenWriter(gzipStream, ArchiveType.Tar, new WriterOptions(CompressionType.None)))
        {
            writer.WriteAll(contentRoot, "*", SearchOption.AllDirectories);
        }

        var importedId = await CreateService().ImportModAsync(tarGzPath);

        var mod = (await _modRepository.GetByIdAsync(importedId))!;
        Assert.Equal("Tgz 模组", mod.Name);
        Assert.True(File.Exists(Path.Combine(mod.Path, "armor.patch")));
    }

    [Theory]
    [InlineData(".7z")]
    [InlineData(".zip")]
    public async Task ImportModAsync_WithSevenZipContent_ShouldDetectByContent(string extension)
    {
        var sourceRoot = Path.Combine(_tempDirectory, "sevenzip-source");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "manifest.json"), """{"name":"7z 模组","version":"1.0.0"}""", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceRoot, "armor.patch"), "sevenzip content");
        var archivePath = Path.Combine(_tempDirectory, "mod" + extension);
        using (var output = File.Create(archivePath))
        using (var writer = WriterFactory.OpenWriter(output, ArchiveType.SevenZip, new WriterOptions(CompressionType.LZMA)))
            writer.WriteAll(sourceRoot, "*", SearchOption.AllDirectories);

        var importedId = await CreateService().ImportModAsync(archivePath);
        var mod = (await _modRepository.GetByIdAsync(importedId))!;
        Assert.Equal("7z 模组", mod.Name);
        Assert.Equal("sevenzip content", await File.ReadAllTextAsync(Path.Combine(mod.Path, "armor.patch")));
    }

    [Fact]
    public async Task ExtractToDirectoryAsync_WithUnrecognizedContent_ShouldExplainFailure()
    {
        var archivePath = Path.Combine(_tempDirectory, "broken.zip");
        await File.WriteAllTextAsync(archivePath, "this is not an archive");
        var extractor = new SharpCompressArchiveExtractor();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => extractor.ExtractToDirectoryAsync(
            archivePath, Path.Combine(_tempDirectory, "invalid-extract")));
        Assert.Contains("broken.zip", error.Message);
        Assert.Contains("无法识别压缩包", error.Message);
        Assert.IsType<SharpCompress.Common.InvalidFormatException>(error.InnerException);
    }

    [Fact]
    public async Task ImportModAsync_WithV1ManifestWithOptions_ShouldStoreManifestOptions()
    {
        // 回归：导入必须把 manifest（含选项定义）写入 Mod.Manifest，
        // 否则配置文件页的模组选项功能无入口
        var zipPath = await CreateZipAsync(zipRoot =>
        {
            var modDir = Path.Combine(zipRoot, "Option Mod");
            Directory.CreateDirectory(modDir);
            File.WriteAllText(Path.Combine(modDir, "manifest.json"),
                """
                {
                  "Version": 1,
                  "Name": "选项模组",
                  "Options": [
                    { "Name": "本体", "Include": ["本体"] },
                    { "Name": "丝袜", "Include": ["丝袜"] }
                  ]
                }
                """);
            File.WriteAllText(Path.Combine(modDir, "body.patch"), "content");
        });

        var importedId = await CreateService().ImportModAsync(zipPath);

        var mod = (await _modRepository.GetByIdAsync(importedId))!;
        Assert.NotNull(mod.Manifest);
        Assert.Equal(ManifestVersion.V1, mod.Manifest!.ManifestVersion);
        Assert.NotNull(mod.Manifest.Options);
        Assert.Equal(2, mod.Manifest.Options!.Count);
        Assert.Equal("本体", mod.Manifest.Options[0].Include![0]);
    }

    [Theory]
    [InlineData("a.zip", true)]
    [InlineData("a.rar", true)]
    [InlineData("a.7z", true)]
    [InlineData("a.tar.gz", true)]
    [InlineData("a.exe", false)]
    [InlineData("a.patch", false)]
    public void IsArchiveFile_ShouldRecognizeSupportedFormats(string fileName, bool expected)
    {
        var extractor = new SharpCompressArchiveExtractor();
        Assert.Equal(expected, extractor.IsArchiveFile(fileName));
    }

    [Fact]
    public async Task ImportModAsync_WithoutManifest_WithSubDirs_ShouldInferLegacyOptions()
    {
        // 与旧版管理器一致：无 manifest 且有子目录时，把子目录名推断为互斥选项
        var contentRoot = Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(contentRoot, "VariantA"));
        Directory.CreateDirectory(Path.Combine(contentRoot, "VariantB"));
        File.WriteAllText(Path.Combine(contentRoot, "VariantA", "a.patch"), "a");
        File.WriteAllText(Path.Combine(contentRoot, "VariantB", "b.patch"), "b");

        var importedId = await CreateService().ImportModAsync(contentRoot);

        var mod = (await _modRepository.GetByIdAsync(importedId))!;
        Assert.NotNull(mod.Manifest);
        Assert.Equal(ManifestVersion.Legacy, mod.Manifest!.ManifestVersion);
        Assert.Equal(new List<string> { "VariantA", "VariantB" }, mod.Manifest.LegacyOptions);
    }

    private ModImportService CreateService()
        => new(_modRepository, _storageDirectory);

    [Fact]
    public async Task ImportModAsync_ShouldPersistActualImportTime()
    {
        var source = Path.Combine(_tempDirectory, "TimedMod");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "body.patch"), "content");
        var before = DateTime.UtcNow;
        var id = await CreateService().ImportModAsync(source);
        var after = DateTime.UtcNow;
        var mod = (await _modRepository.GetByIdAsync(id))!;
        Assert.InRange(mod.ImportedAt.ToUniversalTime(), before, after);
        Assert.Equal(mod.ImportedAt, mod.ModifiedAt);
    }

    [Fact]
    public async Task ImportModAsync_ShouldReportRealSteps()
    {
        var zip = await CreateZipAsync(root =>
        {
            File.WriteAllText(Path.Combine(root, "body.patch"), "content");
        });
        var reports = new List<ModImportProgress>();
        var id = await CreateService().ImportModAsync(zip, progress: new InlineProgress(reports.Add));
        var mod = (await _modRepository.GetByIdAsync(id))!;
        Assert.Equal(new[] { "检查源文件", "解压模组", "识别 HD2IAS 组件", "读取模组清单", "分析模组文件", "复制模组文件", "识别资源类型", "计算哈希", "保存模组", "模组已保存" },
            reports.Select(p => p.Step).Distinct());
        Assert.Equal(reports.Select(p => p.Percentage).Order(), reports.Select(p => p.Percentage));
        Assert.Contains(reports, p => p.Step == "解压模组" && p.IsIndeterminate);
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(mod.Path, "body.patch")));
        Assert.Matches("^[0-9a-f]{64}$", mod.Hash);
        Assert.Equal(await ModHashService.CalculateAsync(mod.Path), mod.Hash);
    }

    [Fact]
    public async Task ImportModAsync_FromFolderAndArchive_ShouldPersistSameContentHash()
    {
        var zip = await CreateZipAsync(root => File.WriteAllText(Path.Combine(root, "body.patch"), "content"));
        var folder = Path.Combine(_tempDirectory, "same-folder");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "body.patch"), "content");
        var duplicateSettings = new SettingsService(Path.Combine(_tempDirectory, "duplicate-settings"));
        await duplicateSettings.SetSettingAsync(SettingKeys.AllowDuplicateImports, true);
        var duplicateImporter = new ModImportService(_modRepository, _storageDirectory, tempDirectory: _tempDirectory, settingsService: duplicateSettings);
        var archiveId = await duplicateImporter.ImportModAsync(zip);
        var folderId = await duplicateImporter.ImportModAsync(folder);
        var archiveMod = (await _modRepository.GetByIdAsync(archiveId))!;
        var folderMod = (await _modRepository.GetByIdAsync(folderId))!;
        Assert.Equal(archiveMod.Hash, folderMod.Hash);
    }

    [Fact]
    public async Task EnsureModHashAsync_ShouldBackfillMissingHashAndReusePersistedValue()
    {
        var folder = Path.Combine(_tempDirectory, "legacy");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "body.patch"), "content");
        var legacy = new Mod { Id = Guid.NewGuid(), Name = "Legacy", Path = folder, Hash = string.Empty };
        await _modRepository.AddAsync(legacy);
        var service = new ModService(_modRepository, _storageDirectory);
        var hash = await service.EnsureModHashAsync(legacy.Id);
        Assert.Equal(await ModHashService.CalculateAsync(folder), hash);
        var saved = (await _modRepository.GetByIdAsync(legacy.Id))!;
        Assert.Equal(hash, saved.Hash);
        Assert.Equal(legacy.ImportedAt, saved.ImportedAt);
        Assert.Equal(legacy.ModifiedAt, saved.ModifiedAt);
        Directory.Delete(folder, true);
        Assert.Equal(hash, await service.EnsureModHashAsync(legacy.Id));
    }

    [Fact]
    public async Task ImportModAsync_CancelDuringHash_ShouldNotPersistPartialHashOrMod()
    {
        var zip = await CreateZipAsync(root => File.WriteAllText(Path.Combine(root, "body.patch"), "content"));
        using var cancellation = new CancellationTokenSource();
        var extractor = new TrackingExtractor();
        var service = new ModImportService(_modRepository, _storageDirectory, archiveExtractor: extractor);
        var progress = new InlineProgress(value => { if (value.Step == "计算哈希") cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportModAsync(zip, cancellation.Token, progress));
        Assert.Empty(await _modRepository.GetAllAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(_storageDirectory));
        Assert.False(Directory.Exists(extractor.Destination));
    }

    [Fact]
    public async Task ImportModAsync_FromFolder_ShouldPreserveEmptyOptionDirectories()
    {
        var source = Path.Combine(_tempDirectory, "folder-source");
        Directory.CreateDirectory(Path.Combine(source, "empty-option"));
        await File.WriteAllTextAsync(Path.Combine(source, "body.patch"), "content");
        var id = await CreateService().ImportModAsync(source);
        var mod = (await _modRepository.GetByIdAsync(id))!;
        Assert.True(Directory.Exists(Path.Combine(mod.Path, "empty-option")));
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(mod.Path, "body.patch")));
    }

    [Fact]
    public async Task ImportModAsync_CancelDuringCopy_ShouldRemovePartialModAndExtractedFiles()
    {
        var zip = await CreateZipAsync(root => File.WriteAllText(Path.Combine(root, "body.patch"), "content"));
        using var cancellation = new CancellationTokenSource();
        var extractor = new TrackingExtractor();
        var service = new ModImportService(_modRepository, _storageDirectory, archiveExtractor: extractor);
        var progress = new InlineProgress(p => { if (p.Step == "复制模组文件") cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportModAsync(zip, cancellation.Token, progress));
        Assert.Empty(await _modRepository.GetAllAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(_storageDirectory));
        Assert.NotNull(extractor.Destination);
        Assert.False(Directory.Exists(extractor.Destination));
        Assert.True(File.Exists(zip));
    }

    private sealed class InlineProgress(Action<ModImportProgress> report) : IProgress<ModImportProgress>
    {
        public void Report(ModImportProgress value) => report(value);
    }

    private sealed class TrackingExtractor : IArchiveExtractor
    {
        public string? Destination { get; private set; }
        public bool IsArchiveFile(string path) => new SharpCompressArchiveExtractor().IsArchiveFile(path);
        public Task ExtractToDirectoryAsync(string archivePath, string destinationDirectory, CancellationToken cancellationToken = default)
        {
            Destination = destinationDirectory;
            return new SharpCompressArchiveExtractor().ExtractToDirectoryAsync(archivePath, destinationDirectory, cancellationToken);
        }
    }

    /// <summary>在回调中准备解压根目录内容后打包为 ZIP。</summary>
    private async Task<string> CreateZipAsync(Action<string> buildContent)
    {
        var zipRoot = Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(zipRoot);
        buildContent(zipRoot);

        var zipPath = Path.Combine(_tempDirectory, $"{Guid.NewGuid():N}.zip");
        ZipFile.CreateFromDirectory(zipRoot, zipPath);
        await Task.CompletedTask;
        return zipPath;
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        foreach (var directory in new[] { _storageDirectory, _tempDirectory })
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
