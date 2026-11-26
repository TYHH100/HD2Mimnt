using System.Text;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;

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

    // 解压配额：防止单个恶意压缩包（压缩炸弹）写满磁盘
    private const long MaxTotalExtractedBytes = 4L * 1024 * 1024 * 1024;
    private const long MaxSingleEntryBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxCompressionRatio = 1000;

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
        => await ExtractWithPasswordAsync(archivePath, destinationDirectory, null, cancellationToken);

    public async Task ExtractWithPasswordAsync(string archivePath, string destinationDirectory, string? password,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException($"压缩包不存在: {archivePath}");
        }

        var staging = Path.GetFullPath(destinationDirectory).TrimEnd(Path.DirectorySeparatorChar) + "." + Guid.NewGuid().ToString("N") + ".extract";
        Directory.CreateDirectory(staging);
        var encrypted = false;
        var isSevenZip = false;
        var options = CreateReaderOptions(password);
        try
        {

            // SharpCompress 的读取 API 是同步的，放到线程池执行避免阻塞调用方（UI 线程）。
            await Task.Run(
                () =>
                {
                    long totalExtracted = 0;
                    long totalActualBytes = 0;
                    using var stream = File.OpenRead(archivePath);
                    // 7z 需要可定位的 Archive API，ReaderFactory 的流式格式列表不包含它。
                    isSevenZip = SevenZipArchive.IsSevenZipFile(stream);
                    stream.Position = 0;
                    if (!isSevenZip && SharpCompress.Archives.Zip.ZipArchive.IsZipFile(stream))
                    {
                        using var zip = SharpCompress.Archives.Zip.ZipArchive.OpenArchive(Reset(stream), options);
                        foreach (var entry in zip.Entries)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            encrypted |= entry.IsEncrypted;
                            if (entry.IsEncrypted && password == null) throw new ArchivePasswordRequiredException();
                            if (entry.IsDirectory || entry.LinkTarget is not null) continue;
                            var destination = ValidateEntry(staging, entry.Key, entry.Size, entry.CompressedSize, ref totalExtracted);
                            entry.WriteToDirectory(staging, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
                            MeterActualBytes(staging, destination, ref totalActualBytes);
                        }
                        return;
                    }
                    using var archive = isSevenZip ? ArchiveFactory.OpenArchive(Reset(stream), options) : null;
                    using var reader = archive != null ? archive.ExtractAllEntries() : OpenReader(Reset(stream), archivePath, options);
                    while (reader.MoveToNextEntry())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        encrypted |= reader.Entry.IsEncrypted;
                        if (reader.Entry.IsEncrypted && password == null) throw new ArchivePasswordRequiredException();
                        if (reader.Entry.IsDirectory || reader.Entry.LinkTarget is not null)
                        {
                            continue;
                        }

                        var destination = ValidateEntry(staging, reader.Entry.Key, reader.Entry.Size, reader.Entry.CompressedSize, ref totalExtracted);
                        reader.WriteEntryToDirectory(
                            staging,
                            new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
                        MeterActualBytes(staging, destination, ref totalActualBytes);
                    }
                },
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(destinationDirectory);
            foreach (var directory in Directory.EnumerateDirectories(staging, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(destinationDirectory, Path.GetRelativePath(staging, directory)));
            foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(file, Path.Combine(destinationDirectory, Path.GetRelativePath(staging, file)), true);
            }
        }
        catch (SharpCompressException ex) when (isSevenZip && password != null
            && ex.GetType().FullName == "SharpCompress.Compressors.LZMA.DataErrorException")
        { throw new ArchivePasswordRequiredException(); }
        catch (SharpCompress.Common.CryptographicException) { throw new ArchivePasswordRequiredException(); }
        catch (Exception ex) when (encrypted && ex is SharpCompressException or InvalidDataException)
        { throw new ArchivePasswordRequiredException(); }
        finally { Directory.Delete(staging, true); }
    }

    private static Stream Reset(Stream stream) { stream.Position = 0; return stream; }

    /// <summary>
    /// 应用层防护（库内置校验之外的第二层）：条目路径不得越出暂存根目录，
    /// 声明的解压大小受单条目/总量/压缩比配额约束，作为写入前的前置过滤。
    /// 条目头声明值可被伪造且 SharpCompress 解压不按声明值截断，最终配额以
    /// <see cref="MeterActualBytes"/> 计量的实际写入字节为准。
    /// 返回条目落盘的完整路径，供写入后计量。
    /// </summary>
    private static string ValidateEntry(string root, string? key, long? size, long? compressedSize, ref long totalExtracted)
    {
        var destination = Path.Combine(root, key ?? string.Empty);
        if (!string.IsNullOrEmpty(key))
        {
            var rootPrefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            destination = Path.GetFullPath(destination);
            if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"压缩包条目路径越界，已拒绝: {key}");
            }
        }
        else
        {
            destination = Path.GetFullPath(destination);
        }

        if (size is not { } entrySize)
        {
            return destination;
        }

        if (entrySize > MaxSingleEntryBytes)
        {
            throw new InvalidDataException($"压缩包条目声明大小过大（{entrySize:N0} 字节），已拒绝: {key}");
        }
        if (compressedSize is > 0 && entrySize / compressedSize > MaxCompressionRatio)
        {
            throw new InvalidDataException($"压缩包条目压缩比异常（{entrySize / compressedSize:N0}×），疑似压缩炸弹，已拒绝: {key}");
        }
        totalExtracted += entrySize;
        if (totalExtracted > MaxTotalExtractedBytes)
        {
            throw new InvalidDataException($"压缩包总解压大小超出上限（{MaxTotalExtractedBytes / (1024 * 1024 * 1024)}GB），已拒绝。");
        }
        return destination;
    }

    /// <summary>
    /// 按实际落盘字节复核配额（声明值配额之后的第一道事实校验）：
    /// 每写完一个条目就统计其真实大小，伪造声明大小的压缩炸弹在此被拦截；
    /// size 未声明（null）的条目此前完全不计数，此处同样覆盖。
    /// </summary>
    private static void MeterActualBytes(string root, string destination, ref long totalActualBytes)
    {
        if (!File.Exists(destination))
        {
            // 实际写入路径与条目键解析不一致（异常形态，如链接条目）：
            // 以暂存目录当前总量快照为准，避免按条目累加造成重复计数
            var snapshot = 0L;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                snapshot += new FileInfo(file).Length;
            }

            totalActualBytes = snapshot;
            if (totalActualBytes > MaxTotalExtractedBytes)
            {
                throw new InvalidDataException($"压缩包实际解压总大小超出上限（{MaxTotalExtractedBytes / (1024 * 1024 * 1024)}GB），已拒绝。");
            }
            return;
        }

        var written = new FileInfo(destination).Length;
        if (written > MaxSingleEntryBytes)
        {
            throw new InvalidDataException($"压缩包条目实际解压大小过大（{written:N0} 字节），已拒绝。");
        }

        totalActualBytes += written;
        if (totalActualBytes > MaxTotalExtractedBytes)
        {
            throw new InvalidDataException($"压缩包实际解压总大小超出上限（{MaxTotalExtractedBytes / (1024 * 1024 * 1024)}GB），已拒绝。");
        }
    }

    private static IReader OpenReader(Stream stream, string archivePath, ReaderOptions options)
    {
        try
        {
            return ReaderFactory.OpenReader(stream, options);
        }
        catch (InvalidFormatException ex)
        {
            throw new InvalidDataException($"无法识别压缩包“{Path.GetFileName(archivePath)}”的实际格式，请确认文件完整、不是分卷片段，并且内容是受支持的压缩包。", ex);
        }
    }

    private static ReaderOptions CreateReaderOptions(string? password)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var ansiEncoding = Encoding.GetEncoding(936);

        return new ReaderOptions
        {
            Password = password,
            ArchiveEncoding = new ArchiveEncoding
            {
                Default = ansiEncoding,
                Password = Encoding.UTF8,
            },
        };
    }
}
