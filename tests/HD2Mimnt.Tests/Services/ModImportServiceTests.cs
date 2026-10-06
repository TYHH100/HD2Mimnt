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
    /// 验证 IArchiveExtractor 接入后 rar/7z 等格式共用同一链路。
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
