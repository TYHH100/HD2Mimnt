using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Services;
using HD2Mimnt.Infrastructure.Archive;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Deployment;
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
        using (var writer = WriterFactory.OpenWriter(gzipStream, SharpCompress.Common.ArchiveType.Tar, new WriterOptions(CompressionType.None)))
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
        using (var writer = WriterFactory.OpenWriter(output, SharpCompress.Common.ArchiveType.SevenZip, new WriterOptions(CompressionType.LZMA)))
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

        // Phase 6.8 起 ForceManifestV1 默认开启；验证旧语义需显式关闭
        var importedId = await CreateService(
            TestHelpers.SeededSettingsService.Create(forceManifestV1: false)).ImportModAsync(contentRoot);

        var mod = (await _modRepository.GetByIdAsync(importedId))!;
        Assert.NotNull(mod.Manifest);
        Assert.Equal(ManifestVersion.Legacy, mod.Manifest!.ManifestVersion);
        Assert.Equal(new List<string> { "VariantA", "VariantB" }, mod.Manifest.LegacyOptions);
    }

    [Fact]
    public async Task ImportModAsync_WithoutManifest_ShouldGenerateManifestFileInModFolder()
    {
        // 导入没有档案的模组后，库内模组文件夹应自动生成档案，内容与数据库清单一致
        var source = Path.Combine(_tempDirectory, "generated-manifest");
        Directory.CreateDirectory(Path.Combine(source, "VariantA"));
        Directory.CreateDirectory(Path.Combine(source, "VariantB"));
        File.WriteAllText(Path.Combine(source, "VariantA", "a.patch"), "a");
        File.WriteAllText(Path.Combine(source, "VariantB", "b.patch"), "b");

        var importedId = await CreateService().ImportModAsync(source);
        var mod = (await _modRepository.GetByIdAsync(importedId))!;

        var manifestPath = Path.Combine(mod.Path, "manifest.json");
        Assert.True(File.Exists(manifestPath));
        Assert.True(ManifestPackage.IsGeneratedManifest(mod.Path));
        var generated = await new ManifestParser().ParseFromFileAsync(manifestPath);
        Assert.NotNull(generated);
        Assert.Equal(ManifestVersion.V1, generated.ManifestVersion);
        Assert.Equal(mod.Name, generated.Name);
        Assert.Equal(mod.Manifest!.Options!.Select(option => option.Name), generated.Options!.Select(option => option.Name));
        // 推断选项默认启用，避免选项弹窗打开即全部未勾选
        Assert.All(mod.Manifest.Options!, option => Assert.True(option.DefaultEnabled));
        Assert.All(generated.Options!, option => Assert.True(option.DefaultEnabled));
    }

    [Fact]
    public async Task GetAllModsAsync_ShouldGenerateArchiveAndEnableInferredOptionsForExistingMods()
    {
        // 已在库中的旧条目：没有档案时自动生成；未编辑的推断选项恢复默认启用
        var parser = new ManifestParser();
        Mod CreateExistingMod(ModManifest manifest, bool writeGeneratedFile, out string modDirectory)
        {
            modDirectory = Path.Combine(_storageDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(modDirectory, "data"));
            File.WriteAllText(Path.Combine(modDirectory, "data", "a.patch"), "a");
            if (writeGeneratedFile) parser.SaveGeneratedToFile(manifest, Path.Combine(modDirectory, "manifest.json"));
            return new Mod
            {
                Id = Guid.NewGuid(), Name = manifest.Name, Path = modDirectory, Manifest = manifest,
                Hash = "content-hash", ResourceTypeIds = []
            };
        }
        static ModManifest Inferred(Guid guid) => new()
        {
            Guid = guid,
            Name = "推断模组",
            Description = "A locally imported mod.",
            Version = "1.0.0",
            Author = "Unknown",
            ManifestVersion = ManifestVersion.V1,
            Options = [new() { Name = "data", Description = "Option for data", DefaultEnabled = false, SubOptions = [] }]
        };

        var legacy = CreateExistingMod(Inferred(Guid.Empty), writeGeneratedFile: false, out var legacyDirectory);
        var generated = CreateExistingMod(Inferred(Guid.NewGuid()), writeGeneratedFile: true, out var generatedDirectory);
        // 有作者信息的清单保持原样，不按推断结果改写或补写根档案
        var authoredManifest = new ModManifest
        {
            Guid = Guid.NewGuid(), Name = "作者模组", Description = "作者清单", Version = "2.0.0", Author = "作者",
            ManifestVersion = ManifestVersion.V1,
            Options = [new() { Name = "data", Description = "作者选项", Include = ["data"], DefaultEnabled = false }]
        };
        var authored = CreateExistingMod(authoredManifest, writeGeneratedFile: false, out var authoredDirectory);
        // 数据库里也没有清单的旧条目：本次回填推断后立即生成档案
        var missingDirectory = Path.Combine(_storageDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(missingDirectory, "data"));
        File.WriteAllText(Path.Combine(missingDirectory, "data", "a.patch"), "a");
        var missing = new Mod
        {
            Id = Guid.NewGuid(), Name = "无清单模组", Path = missingDirectory, Manifest = null,
            Hash = "content-hash", ResourceTypeIds = []
        };
        foreach (var mod in new[] { legacy, generated, authored, missing }) await _modRepository.AddAsync(mod);

        await new ModService(_modRepository, _storageDirectory).GetAllModsAsync();

        Assert.True((await _modRepository.GetByIdAsync(legacy.Id))!.Manifest!.Options![0].DefaultEnabled);
        Assert.True((await _modRepository.GetByIdAsync(generated.Id))!.Manifest!.Options![0].DefaultEnabled);
        Assert.False((await _modRepository.GetByIdAsync(authored.Id))!.Manifest!.Options![0].DefaultEnabled);

        // 旧条目补齐生成档案（旧推断的空 Guid 同步替换），已有生成档案同步默认值
        var legacyFile = await parser.ParseFromFileAsync(Path.Combine(legacyDirectory, "manifest.json"));
        Assert.True(ManifestPackage.IsGeneratedManifest(legacyDirectory));
        Assert.NotEqual(Guid.Empty, legacyFile!.Guid);
        Assert.True(legacyFile.Options![0].DefaultEnabled);
        var generatedFile = await parser.ParseFromFileAsync(Path.Combine(generatedDirectory, "manifest.json"));
        Assert.True(generatedFile!.Options![0].DefaultEnabled);
        // 作者清单不补写根档案
        Assert.False(File.Exists(Path.Combine(authoredDirectory, "manifest.json")));
        // 数据库无清单的旧条目推断后同样生成档案
        Assert.NotNull((await _modRepository.GetByIdAsync(missing.Id))!.Manifest);
        Assert.True(ManifestPackage.IsGeneratedManifest(missingDirectory));
    }

    [Fact]
    public async Task ImportModAsync_WithoutManifest_RenamedArchiveWithSameContent_IsRejected()
    {
        // 生成档案的内容随来源名称变化，但不能因此绕过重复导入检查
        var source = Path.Combine(_tempDirectory, "duplicate-source");
        Directory.CreateDirectory(Path.Combine(source, "VariantA"));
        Directory.CreateDirectory(Path.Combine(source, "VariantB"));
        File.WriteAllText(Path.Combine(source, "VariantA", "a.patch"), "a");
        File.WriteAllText(Path.Combine(source, "VariantB", "b.patch"), "b");
        await CreateService().ImportModAsync(source);

        var zip = await CreateZipAsync(root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "VariantA"));
            Directory.CreateDirectory(Path.Combine(root, "VariantB"));
            File.WriteAllText(Path.Combine(root, "VariantA", "a.patch"), "a");
            File.WriteAllText(Path.Combine(root, "VariantB", "b.patch"), "b");
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService().ImportModAsync(zip));
        Assert.Contains("模组库中已存在相同内容", error.Message);
        Assert.Single(await _modRepository.GetAllAsync());
    }

    [Fact]
    public async Task ImportModAsync_SingleRootFolderNoManifest_ShouldFlattenAndDeploy()
    {
        // 旧管理器同款简装包：唯一根文件夹套着补丁、没有清单。不拍平时会推断出无 Include 的
        // V1 选项，部署回退全量目录只扫模组根直属文件，整个模组被跳过（“没有可部署的补丁”）。
        var zipPath = await CreateZipAsync(zipRoot =>
        {
            var modDir = Path.Combine(zipRoot, "尖峰t58拖拉机");
            Directory.CreateDirectory(modDir);
            File.WriteAllText(Path.Combine(modDir, "a193a00ce4bbd529.patch_0"), "patch-content");
        });

        var importedId = await CreateService().ImportModAsync(zipPath);
        var mod = (await _modRepository.GetByIdAsync(importedId))!;

        // 拍平后补丁位于模组根目录，没有子目录可推断选项：仍生成基础档案，部署走全量根扫描
        Assert.NotNull(mod.Manifest);
        Assert.Null(mod.Manifest!.Options);
        Assert.True(File.Exists(Path.Combine(mod.Path, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(mod.Path, "a193a00ce4bbd529.patch_0")));
        Assert.False(Directory.Exists(Path.Combine(mod.Path, "尖峰t58拖拉机")));

        // 端到端：加入配置并部署，补丁应出现在游戏 data 目录
        var gameDir = Path.Combine(_tempDirectory, "game");
        Directory.CreateDirectory(gameDir);
        var profileRepository = new ProfileRepository(_dbContext);
        var profileService = new ProfileService(profileRepository, _modRepository);
        var profileId = await profileService.CreateProfileAsync("简装包");
        await profileService.AddModToProfileAsync(profileId, importedId);
        IDeploymentRepository deploymentRepository = new DeploymentRepository(_dbContext);
        var deploymentService = new DeploymentService(profileRepository, _modRepository, deploymentRepository,
            new IDeploymentMethod[] { new CopyDeploymentMethod() });
        var result = await deploymentService.DeployProfileAsync(profileId, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, gameDir);

        Assert.Equal(DeploymentStatus.Completed, result.Status);
        Assert.Equal("patch-content",
            await File.ReadAllTextAsync(Path.Combine(gameDir, "data", "a193a00ce4bbd529.patch_0")));
    }

    [Fact]
    public async Task ImportModAsync_SingleRootFolderWithManifest_ShouldNotFlatten()
    {
        // 带清单的包不拍平：结构交给嵌套清单查找，模组内容与清单声明保持一致
        var zipPath = await CreateZipAsync(zipRoot =>
        {
            var modDir = Path.Combine(zipRoot, "Wrapped Mod");
            Directory.CreateDirectory(modDir);
            File.WriteAllText(Path.Combine(modDir, "manifest.json"),
                """{"name":"包裹模组","version":"1.0.0"}""", Encoding.UTF8);
            File.WriteAllText(Path.Combine(modDir, "body.patch"), "content");
        });

        var importedId = await CreateService().ImportModAsync(zipPath);
        var mod = (await _modRepository.GetByIdAsync(importedId))!;
        Assert.Equal("包裹模组", mod.Name);
        Assert.True(File.Exists(Path.Combine(mod.Path, "body.patch")));
    }

    private ModImportService CreateService(SettingsService? settingsService = null)
        => new(_modRepository, _storageDirectory, settingsService: settingsService);

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
        // 自动生成的档案不参与内容哈希：来源名称不同也保持同哈希
        Assert.True(ManifestPackage.IsGeneratedManifest(archiveMod.Path));
        Assert.True(ManifestPackage.IsGeneratedManifest(folderMod.Path));
    }

    [Fact]
    public async Task ImportModAsync_DuplicateCheckFingerprint_ShouldReportComparisonStep()
    {
        // 相同作者清单、不同内容：判重无法走哈希快路径，会对两个目录再算一轮指纹。
        const string manifest = """{"Name":"DupProbe","Version":"1.0"}""";
        var first = Path.Combine(_tempDirectory, "dup-first");
        Directory.CreateDirectory(first);
        File.WriteAllText(Path.Combine(first, "manifest.json"), manifest);
        File.WriteAllText(Path.Combine(first, "body.patch"), "first");
        var second = Path.Combine(_tempDirectory, "dup-second");
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, "manifest.json"), manifest);
        File.WriteAllText(Path.Combine(second, "body.patch"), "second");

        await CreateService().ImportModAsync(first);
        var reports = new List<ModImportProgress>();
        await CreateService().ImportModAsync(second, progress: new InlineProgress(reports.Add));

        Assert.Contains(reports, p => p.Step == "判重比对");
        Assert.Equal(reports.Select(p => p.Percentage).Order(), reports.Select(p => p.Percentage));
        Assert.Equal(2, (await _modRepository.GetAllAsync()).Count());
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
