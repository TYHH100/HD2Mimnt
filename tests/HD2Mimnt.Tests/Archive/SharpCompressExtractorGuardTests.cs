using System.IO.Compression;
using System.Text;
using HD2Mimnt.Infrastructure.Archive;

namespace HD2Mimnt.Tests.Archive;

/// <summary>解压防护：应用层条目路径校验与压缩炸弹配额。</summary>
public sealed class SharpCompressExtractorGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hd2-extract-guard-{Guid.NewGuid():N}");

    public SharpCompressExtractorGuardTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task NormalZip_ExtractsNestedEntries()
    {
        var zipPath = Path.Combine(_root, "normal.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("ModFolder/data/file.txt");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            await writer.WriteAsync("content");
        }

        var destination = Path.Combine(_root, "out-normal");
        await new SharpCompressArchiveExtractor().ExtractToDirectoryAsync(zipPath, destination);

        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(destination, "ModFolder", "data", "file.txt")));
    }

    [Fact]
    public async Task TraversalEntry_IsRejectedWithoutEscaping()
    {
        var zipPath = Path.Combine(_root, "evil.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../evil.txt");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            await writer.WriteAsync("escaped");
        }

        var destination = Path.Combine(_root, "nested", "out-evil");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SharpCompressArchiveExtractor().ExtractToDirectoryAsync(zipPath, destination));

        // 逃逸目标不得存在；目标目录未创建（解压在暂存阶段即被拒绝）
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task ExtremeCompressionRatio_IsRejectedAsBomb()
    {
        // Deflate 对全零数据可达 ~1000×；100MB 零的包应触发压缩比配额
        var payloadPath = Path.Combine(_root, "zeros.bin");
        var buffer = new byte[4 * 1024 * 1024];
        await using (var stream = File.Create(payloadPath))
        {
            for (var written = 0L; written < 100L * 1024 * 1024; written += buffer.Length)
            {
                await stream.WriteAsync(buffer);
            }
        }
        var zipPath = Path.Combine(_root, "bomb.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(payloadPath, "zeros.bin", CompressionLevel.Optimal);
        }

        var destination = Path.Combine(_root, "out-bomb");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SharpCompressArchiveExtractor().ExtractToDirectoryAsync(zipPath, destination));
    }
}
