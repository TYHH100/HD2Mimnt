using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using HD2Mimnt.Core.AudioPreview;
using HD2Mimnt.Core.ModelPreview;
using HD2Mimnt.Infrastructure.Analysis;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Infrastructure.AudioPreview;

/// <summary>
/// 一个游戏原版音频包（如 <c>9ba626afa44a3aa3</c>）的只读比对基线，供模组条目与游戏自身
/// 媒体比较。支持两种游戏布局：legacy 安装（散装 <c>data/&lt;base&gt;</c> + <c>.stream</c>
/// 文件）与 slim 安装（资源在 LZ4 DSAR bundle 内，经 <see cref="GameUnitReferenceReader"/>
/// 的归档会话读取）。
///
/// 内存与 IO 约束（语音包单模组可达近万条目，这里失控会拖垮整机）：
/// - 基线只驻留 TOC 元数据与每媒体 32 字节 SHA-256，不保留任何媒体字节数组；
/// - 游戏侧读取有硬预算（条数 + 字节数），耗尽后一律回答"未知"而不是继续读；
/// - legacy 布局持有常开只读句柄（避免逐条目 open/close 的随机 IO 风暴）；
/// - slim 布局的 bundle 解码缓存由 GameUnitReferenceReader 的 ArchiveSession 提供。
/// </summary>
internal sealed class GameAudioBaseline : IGameAudioBaseline
{
    private const int HeaderSize = 72;
    private const int TypeEntrySize = 32;
    private const int FileEntrySize = 80;
    private const int PatchHeaderMagic = unchecked((int)0xF0000011);
    private const int MaxTypes = 1000;
    private const int MaxFiles = 100000;
    private const int MaxBundleRangeBytes = 64 * 1024 * 1024;
    private const long MaxBankBytes = 64L * 1024 * 1024;
    private const int MaxMediaBytes = 32 * 1024 * 1024;

    /// <summary>单个基线（一个游戏包）的比对预算：超过任一限额即停止比对。</summary>
    internal const int MaxComparisons = 6000;
    internal const long MaxReadBudgetBytes = 96L * 1024 * 1024;

    private delegate byte[]? RangeReader(long offset, int length);

    private readonly RangeReader _readTocRange;
    private readonly RangeReader? _readStreamRange;
    private readonly Dictionary<ulong, TocRecord> _tocEntries;
    private readonly Dictionary<ulong, BankMetadata> _banks = [];
    private readonly List<FileStream> _ownedStreams = [];
    private List<IDisposable>? _ownedHandles;
    private FileStream? _persistentTocStream;
    private int _remainingComparisons = MaxComparisons;
    private long _remainingReadBytes = MaxReadBudgetBytes;

    private enum EntryKind
    {
        Bank,
        Stream,
    }

    private readonly record struct TocRecord(ulong FileId, EntryKind Kind, long TocDataOffset, uint TocDataSize, long StreamOffset, uint StreamSize);

    /// <summary>bank 的 DIDX 元数据 + 逐媒体惰性哈希。不持有媒体字节。</summary>
    private sealed class BankMetadata(AudioBankChunkReader.BankChunks chunks)
    {
        public AudioBankChunkReader.BankChunks Chunks { get; } = chunks;
        public Dictionary<uint, byte[]> Hashes { get; } = [];
        public bool Complete { get; set; }
    }

    private GameAudioBaseline(RangeReader readTocRange, RangeReader? readStreamRange, Dictionary<ulong, TocRecord> tocEntries)
    {
        _readTocRange = readTocRange;
        _readStreamRange = readStreamRange;
        _tocEntries = tocEntries;
    }

    public void Dispose()
    {
        foreach (var stream in _ownedStreams)
        {
            try
            {
                stream.Dispose();
            }
            catch (Exception)
            {
                // 释放路径不做故障放大。
            }
        }
        _ownedStreams.Clear();
        if (_ownedHandles is not null)
        {
            foreach (var handle in _ownedHandles)
            {
                try
                {
                    handle.Dispose();
                }
                catch (Exception)
                {
                    // 释放路径不做故障放大。
                }
            }
            _ownedHandles.Clear();
        }
    }

    /// <summary>查询 bank 媒体的原始尺寸；不消耗读取预算。</summary>
    public AudioBaselineLookup TryGetBankMediaSize(ulong bankFileId, uint sourceId, out uint size)
    {
        size = 0;
        if (!_tocEntries.TryGetValue(bankFileId, out var record) || record.Kind != EntryKind.Bank)
            return AudioBaselineLookup.Unavailable;
        var metadata = GetBankMetadata(record);
        if (metadata is null)
            return AudioBaselineLookup.Unavailable;
        foreach (var media in metadata.Chunks.Didx)
        {
            if (media.Id == sourceId)
            {
                size = media.Size;
                return AudioBaselineLookup.Found;
            }
        }

        return AudioBaselineLookup.ResourceMissing;
    }

    /// <summary>查询流媒体的原始尺寸；不消耗读取预算。</summary>
    public AudioBaselineLookup TryGetStreamMediaSize(ulong streamFileId, out uint size)
    {
        size = 0;
        if (!_tocEntries.TryGetValue(streamFileId, out var record) || record.Kind != EntryKind.Stream)
            return AudioBaselineLookup.Unavailable;
        size = record.StreamSize;
        return AudioBaselineLookup.Found;
    }

    /// <summary>计算 bank 媒体的原始 SHA-256（消耗比对预算；结果按 source id 缓存）。</summary>
    public byte[]? TryGetBankMediaHash(ulong bankFileId, uint sourceId)
    {
        if (!_tocEntries.TryGetValue(bankFileId, out var record) || record.Kind != EntryKind.Bank)
            return null;
        var metadata = GetBankMetadata(record);
        if (metadata is null)
            return null;
        if (metadata.Hashes.TryGetValue(sourceId, out var cached))
            return cached;

        AudioBankChunkReader.DidxMedia? media = null;
        foreach (var candidate in metadata.Chunks.Didx)
        {
            if (candidate.Id == sourceId)
            {
                media = candidate;
                break;
            }
        }
        if (media is not { } found || found.Size == 0 || found.Size > MaxMediaBytes)
            return null;
        if (metadata.Complete)
        {
            // slim 布局：哈希在元数据加载时一次性全量算完（避免重复解码 bundle item），
            // 查询零 IO，不消耗比对预算。
            return metadata.Hashes.GetValueOrDefault(sourceId);
        }
        if (!TryAcquireBudget(found.Size))
            return null;

        // legacy 的 DataOffset 来自文件句柄遍历（文件绝对偏移），不加 TocDataOffset+16；
        // slim 的哈希已在 GetBankMetadata 里按相对坐标算完，不会走到这里。
        var slice = _persistentTocStream is not null
            ? ReadStreamRange(_persistentTocStream, metadata.Chunks.DataOffset + found.Offset, (int)found.Size)
            : _readTocRange(record.TocDataOffset + 16 + metadata.Chunks.DataOffset + found.Offset, (int)found.Size);
        var hash = slice is null ? null : SHA256.HashData(slice);
        if (hash is not null)
            metadata.Hashes[sourceId] = hash;
        return hash;
    }

    /// <summary>计算流媒体的原始 SHA-256（消耗比对预算；每次直读，语音流条目小且按偏移有序）。</summary>
    public byte[]? TryGetStreamMediaHash(ulong streamFileId)
    {
        if (_readStreamRange is null)
            return null;
        if (!_tocEntries.TryGetValue(streamFileId, out var record) || record.Kind != EntryKind.Stream)
            return null;
        if (record.StreamSize == 0 || record.StreamSize > MaxMediaBytes)
            return null;
        if (!TryAcquireBudget(record.StreamSize))
            return null;
        var slice = _readStreamRange(record.StreamOffset, (int)record.StreamSize);
        return slice is null ? null : SHA256.HashData(slice);
    }

    private bool TryAcquireBudget(uint size)
    {
        if (Volatile.Read(ref _remainingComparisons) <= 0)
            return false;
        lock (_banks)
        {
            if (_remainingComparisons <= 0 || size > _remainingReadBytes)
                return false;
            _remainingComparisons--;
            _remainingReadBytes -= size;
            return true;
        }
    }

    /// <summary>加载 bank 的 chunk 元数据。legacy 只走 chunk 头；slim 需解码整个 bank item
    /// （≤64MB，瞬时缓冲，用完即弃），并顺手算完全部媒体哈希。</summary>
    private BankMetadata? GetBankMetadata(TocRecord record)
    {
        if (_banks.TryGetValue(record.FileId, out var cached))
            return cached;
        if (record.TocDataSize <= 16 || record.TocDataSize > MaxBankBytes)
            return null;

        BankMetadata? metadata;
        if (_persistentTocStream is { } tocStream)
        {
            // legacy：常开句柄上做 chunk 头遍历，不读 DATA 体。
            var chunks = AudioBankChunkReader.ReadFromStream(tocStream, record.TocDataOffset + 16, record.TocDataSize - 16);
            metadata = chunks is null ? null : new BankMetadata(chunks.Value);
        }
        else
        {
            // slim：bank 数据在 bundle 里，读取即解码；一次性建立尺寸+哈希表后立即释放字节。
            var bankData = _readTocRange(record.TocDataOffset + 16, (int)record.TocDataSize - 16);
            if (bankData is not null && AudioBankChunkReader.Parse(bankData) is { } chunks)
            {
                metadata = new BankMetadata(chunks);
                var dataEnd = (long)chunks.DataOffset + chunks.DataSize;
                foreach (var media in chunks.Didx)
                {
                    if (media.Size == 0 || media.Size > MaxMediaBytes ||
                        chunks.DataOffset + media.Offset + media.Size > dataEnd)
                        continue;
                    metadata.Hashes[media.Id] = SHA256.HashData(bankData.AsSpan((int)(chunks.DataOffset + media.Offset), (int)media.Size));
                }
                metadata.Complete = true;
            }
            else
            {
                metadata = null;
            }
        }

        if (metadata is not null)
            _banks[record.FileId] = metadata;
        return metadata;
    }

    /// <summary>定位并加载一个游戏包的基线。游戏 data 目录不提供它（布局未知、包缺失或解析
    /// 失败）时返回 null。legacy 优先，其次 slim DSAR 布局。</summary>
    public static GameAudioBaseline? TryLoad(string gameDirectory, string packageBaseName,
        Func<string, string, CancellationToken, SlimPackageHandle?>? slimOpener,
        ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var dataDirectory = Path.Combine(Path.GetFullPath(gameDirectory), "data");
            var legacyToc = Path.Combine(dataDirectory, packageBaseName);
            if (File.Exists(legacyToc))
            {
                var tocStream = OpenSharedRead(legacyToc);
                FileStream? streamStream = null;
                var legacyStream = legacyToc + ".stream";
                if (File.Exists(legacyStream))
                    streamStream = OpenSharedRead(legacyStream);

                var legacyBaseline = TryParseToc(
                    (offset, length) => ReadStreamRange(tocStream, offset, length),
                    streamStream is null ? null : (offset, length) => ReadStreamRange(streamStream, offset, length));
                if (legacyBaseline is null)
                {
                    tocStream.Dispose();
                    streamStream?.Dispose();
                    return null;
                }
                legacyBaseline._persistentTocStream = tocStream;
                legacyBaseline._ownedStreams.Add(tocStream);
                if (streamStream is not null)
                    legacyBaseline._ownedStreams.Add(streamStream);
                return legacyBaseline;
            }

            if (slimOpener is null || !File.Exists(Path.Combine(dataDirectory, "bundles.nxa")))
                return null;

            // 构造阶段（读 TOC 头/条目）与后续读取共用同一句柄；句柄随基线释放。
            var tocHandle = slimOpener(gameDirectory, packageBaseName, cancellationToken);
            if (tocHandle is null)
                return null;
            var streamHandle = slimOpener(gameDirectory, packageBaseName + ".stream", cancellationToken);

            var baseline = TryParseToc(
                (offset, length) => tocHandle.ReadRange(offset, length),
                streamHandle is null ? null : (offset, length) => streamHandle.ReadRange(offset, length));
            if (baseline is null)
            {
                tocHandle.Dispose();
                streamHandle?.Dispose();
                return null;
            }
            baseline._ownedHandles = [tocHandle];
            if (streamHandle is not null)
                baseline._ownedHandles.Add(streamHandle);
            return baseline;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Failed to load game audio baseline for {Base}", packageBaseName);
            return null;
        }
    }

    private static GameAudioBaseline? TryParseToc(RangeReader readToc, RangeReader? readStream)
    {
        var headerBytes = readToc(0, HeaderSize);
        if (headerBytes is null || headerBytes.Length < HeaderSize)
            return null;
        if (BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(0, 4)) != PatchHeaderMagic)
            return null;

        var numTypes = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(4, 4));
        var numFiles = BinaryPrimitives.ReadInt32LittleEndian(headerBytes.AsSpan(8, 4));
        if (numTypes < 0 || numFiles < 0 || numTypes > MaxTypes || numFiles > MaxFiles)
            return null;

        var entriesOffset = HeaderSize + (long)numTypes * TypeEntrySize;
        var entriesLength = (long)numFiles * FileEntrySize;
        if (entriesLength > MaxBundleRangeBytes)
            return null;
        var entriesBytes = readToc(entriesOffset, (int)entriesLength);
        if (entriesBytes is null)
            return null;

        var entries = new Dictionary<ulong, TocRecord>(numFiles);
        for (var i = 0; i < numFiles; i++)
        {
            var entry = entriesBytes.AsSpan(i * FileEntrySize, FileEntrySize);
            var fileId = BinaryPrimitives.ReadUInt64LittleEndian(entry);
            var typeId = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
            var tocDataOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]);
            var streamOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[24..]);
            var tocDataSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[56..]);
            var streamSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[60..]);

            EntryKind? kind = typeId switch
            {
                PatchResourceTypeIds.WwiseBank => EntryKind.Bank,
                PatchResourceTypeIds.WwiseStream => EntryKind.Stream,
                _ => null,
            };
            if (kind is not { } parsedKind)
                continue;
            entries[fileId] = new TocRecord(fileId, parsedKind, tocDataOffset, tocDataSize, streamOffset, streamSize);
        }

        if (entries.Count == 0)
            return null;
        return new GameAudioBaseline(readToc, readStream, entries);
    }

    private static FileStream OpenSharedRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        81920,
        FileOptions.RandomAccess);

    private static byte[]? ReadStreamRange(FileStream stream, long offset, int length)
    {
        try
        {
            if (offset < 0 || length < 0 || offset > stream.Length - length)
                return null;
            var buffer = new byte[length];
            stream.Position = offset;
            var read = 0;
            while (read < buffer.Length)
            {
                var count = stream.Read(buffer, read, buffer.Length - read);
                if (count <= 0)
                    return null;
                read += count;
            }
            return buffer;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
