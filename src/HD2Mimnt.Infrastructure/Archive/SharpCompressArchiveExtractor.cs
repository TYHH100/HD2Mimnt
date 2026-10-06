using System.Text;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace HD2Mimnt.Infrastructure.Archive;

/// <summary>
/// 基于 SharpCompress 的压缩包提取器。
/// 支持单一实现的多种格式（zip、7z、rar、tar、gz、bz2、xz 等），格式按内容自动识别。
/// 非 UTF-8 文件名的条目（常见于国内工具打包的 GBK 文件名 zip）按 cp936 解码，
/// 与系统 ANSI 代码页行为一致；带 UTF-8 标志的条目仍按 UTF-8 解码。
/// </summary>
public class SharpCompressArchiveExtractor : IArchiveExtractor
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz",
    };

    private static readonly ReaderOptions ReaderOptions = CreateReaderOptions();

    private readonly ILogger<SharpCompressArchiveExtractor>? _logger;

    public SharpCompressArchiveExtractor(ILogger<SharpCompressArchiveExtractor>? logger = null)
    {
        _logger = logger;
    }

    public bool IsArchiveFile(string path)
    {
        return SupportedExtensions.Contains(Path.GetExtension(path));
    }

    public async Task ExtractToDirectoryAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException($"压缩包不存在: {archivePath}");
        }

        Directory.CreateDirectory(destinationDirectory);

        // SharpCompress 的读取 API 是同步的，放到线程池执行避免阻塞调用方（UI 线程）。
        await Task.Run(
            () =>
            {
                using var stream = File.OpenRead(archivePath);
                using var reader = ReaderFactory.OpenReader(stream, ReaderOptions);
                while (reader.MoveToNextEntry())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reader.Entry.IsDirectory)
                    {
                        continue;
                    }

                    reader.WriteEntryToDirectory(
                        destinationDirectory,
                        new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
                }
            },
            cancellationToken);
    }

    private static ReaderOptions CreateReaderOptions()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var ansiEncoding = Encoding.GetEncoding(936);

        return new ReaderOptions
        {
            ArchiveEncoding = new ArchiveEncoding
            {
                Default = ansiEncoding,
                Password = ansiEncoding,
            },
        };
    }
}
