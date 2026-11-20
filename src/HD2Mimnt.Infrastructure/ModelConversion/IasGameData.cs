using System.Buffers.Binary;
using K4os.Compression.LZ4;

namespace HD2Mimnt.Infrastructure.ModelConversion;

/// <summary>
/// 只按需读取游戏 <c>data/bundles*.nxa</c> 中的资源，不扫描整份游戏。
/// 对应上游 <c>ias_binary.GameData</c>/<c>DSAR</c>；用于骨架、材质和装配资料的只读参考。
/// </summary>
public sealed class IasGameData : IDisposable
{
    private const int MaxIndexBytes = 512 * 1024 * 1024;
    private const int MaxResourceBytes = 256 * 1024 * 1024;
    private readonly Dictionary<string, IasDsar> _dsars = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyDictionary<(ulong Type, ulong Id), IasArchiveRow>> _tables = new(StringComparer.OrdinalIgnoreCase);
    private byte[]? _packageIndex;
    private readonly Dictionary<string, IasPackageEntry> _packagesByName = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public IasGameData(string root) => Root = Path.GetFullPath(root);

    public string Root { get; }

    public (ulong Original, uint Start, byte Bundle)[] ReadPackageItems(string name)
    {
        Index();
        if (!_packagesByName.TryGetValue(name, out var package)) throw new FileNotFoundException("native archive not indexed: " + name);
        return package.Items;
    }

    public IasResource Resource(string archive, ulong type, ulong id, bool tocOnly = false)
    {
        var rows = Table(archive);
        if (!rows.TryGetValue((type, id), out var row)) throw new KeyNotFoundException($"{type:x16}/{id:x16}");
        var toc = ReadSegment(archive, row.TocOffset, row.TocSize);
        var gpu = tocOnly ? IasPayload.Empty : IasPayload.From(ReadSegment(archive + ".gpu_resources", row.GpuOffset, row.GpuSize));
        var stream = tocOnly ? IasPayload.Empty : IasPayload.From(ReadSegment(archive + ".stream", row.StreamOffset, row.StreamSize));
        return new IasResource(toc, gpu, stream,
            [(int)row.Meta0, (int)row.Meta1, (int)row.Meta2, (int)row.Meta3],
            $"native_archive={archive};type={type:x16};id={id:x16}");
    }

    public IReadOnlyDictionary<(ulong Type, ulong Id), IasArchiveRow> Table(string archive)
    {
        if (_tables.TryGetValue(archive, out var cached)) return cached;
        var prefix = Read(archive, 0, 12);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        var typeCount = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(4));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(8));
        if (magic != IasArchive.Magic || typeCount > 1024 || count > 100_000)
            throw new InvalidDataException("模型数据检查：native archive magic");
        var size = checked((int)(IasArchive.HeaderSize + (long)IasArchive.TypeRecordSize * typeCount + (long)IasArchive.RowSize * count));
        var table = IasArchive.ReadRows(Read(archive, 0, size));
        _tables[archive] = table;
        return table;
    }

    private byte[] ReadSegment(string archive, ulong offset, uint size)
        => size == 0 ? [] : Read(archive, offset, checked((int)size));

    public byte[] Read(string name, ulong offset, int size)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (size < 0 || size > MaxResourceBytes) throw new InvalidDataException("游戏资源读取尺寸无效。");
        var path = Path.Combine(Root, name);
        if (File.Exists(path))
        {
            if (!IsDsar(path)) return ReadRaw(path, offset, size);
            return Dsar(path).Read(offset, size);
        }
        Index();
        if (!_packagesByName.TryGetValue(name, out var package))
            throw new FileNotFoundException("native archive not indexed: " + name);
        if (offset + (ulong)size > package.Total) throw new InvalidDataException("native package range");
        var result = new byte[size];
        var written = 0;
        for (var index = 0; index < package.Items.Length; index++)
        {
            var item = package.Items[index];
            var end = index + 1 < package.Items.Length ? package.Items[index + 1].Original : package.Total;
            if (item.Original >= offset + (ulong)size) break;
            var from = Math.Max(offset, item.Original);
            var to = Math.Min(offset + (ulong)size, end);
            if (from >= to) continue;
            var chunk = Dsar(Path.Combine(Root, $"bundles.{item.Bundle:00}.nxa"))
                .Read(item.Start + (from - item.Original), checked((int)(to - from)));
            Buffer.BlockCopy(chunk, 0, result, written, chunk.Length);
            written += chunk.Length;
        }
        if (written != size) throw new InvalidDataException("native package mapping incomplete");
        return result;
    }

    private void Index()
    {
        if (_packageIndex is not null) return;
        var data = Dsar(Path.Combine(Root, "bundles.nxa")).ReadAll(MaxIndexBytes);
        _packageIndex = data;
        if (data.Length < 24) throw new InvalidDataException("游戏归档索引头部不完整。");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        if (count > 1_000_000) throw new InvalidDataException("游戏包表无效。");
        for (var index = 0; index < count; index++)
        {
            var offset = 24 + index * 24;
            if (offset + 24 > data.Length) throw new InvalidDataException("游戏包表越界。");
            var total = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
            var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8));
            var itemCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 12));
            var itemsOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 16));
            var name = ReadName(data, nameOffset);
            if (itemCount > 100_000 || (ulong)itemsOffset + 16UL * itemCount > (ulong)data.Length)
                throw new InvalidDataException("游戏包映射表越界。");
            var items = new (ulong Original, uint Start, byte Bundle)[itemCount];
            for (var item = 0; item < items.Length; item++)
            {
                var entry = (int)itemsOffset + item * 16;
                items[item] = (BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(entry)),
                    BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entry + 8)), data[entry + 15]);
            }
            _packagesByName[name] = new IasPackageEntry(total, items);
        }
    }

    private static string ReadName(byte[] data, uint offset)
    {
        if (offset >= data.Length) throw new InvalidDataException("游戏包名称偏移越界。");
        var end = Array.IndexOf(data, (byte)0, (int)offset);
        if (end <= offset) throw new InvalidDataException("游戏包名称缺失终止符。");
        return System.Text.Encoding.ASCII.GetString(data, (int)offset, end - (int)offset);
    }

    private static bool IsDsar(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> magic = stackalloc byte[4];
        return stream.Read(magic) == 4 && magic.SequenceEqual("DSAR"u8);
    }

    private static byte[] ReadRaw(string path, ulong offset, int size)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.RandomAccess);
        if (offset + (ulong)size > (ulong)stream.Length) throw new InvalidDataException("short native archive range");
        stream.Position = checked((long)offset);
        var result = new byte[size];
        stream.ReadExactly(result);
        return result;
    }

    private IasDsar Dsar(string path)
    {
        if (_dsars.TryGetValue(path, out var cached)) return cached;
        var dsar = new IasDsar(path);
        _dsars[path] = dsar;
        return dsar;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var dsar in _dsars.Values) dsar.Dispose();
        _dsars.Clear();
    }

    private sealed record IasPackageEntry(ulong Total, (ulong Original, uint Start, byte Bundle)[] Items);
}

/// <summary>DSAR 归档的随机访问：块表 + 按需 LZ4 解码。</summary>
internal sealed class IasDsar : IDisposable
{
    private readonly FileStream _stream;
    private readonly IasChunk[] _chunks;
    private readonly Dictionary<int, byte[]> _cache = new();

    private readonly record struct IasChunk(ulong Offset, ulong StoredOffset, int Size, int StoredSize, byte Codec);

    public IasDsar(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.RandomAccess);
        try
        {
            Span<byte> header = stackalloc byte[32];
            _stream.ReadExactly(header);
            if (!header[..4].SequenceEqual("DSAR"u8)) throw new InvalidDataException("DSAR magic");
            var count = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            if (count > 10_000_000) throw new InvalidDataException("DSAR chunk count");
            _chunks = new IasChunk[count];
            var buffer = new byte[32];
            for (var index = 0; index < _chunks.Length; index++)
            {
                _stream.ReadExactly(buffer);
                var chunk = new IasChunk(
                    BinaryPrimitives.ReadUInt64LittleEndian(buffer),
                    BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(8)),
                    checked((int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(16))),
                    checked((int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(20))),
                    buffer[24]);
                if (!(chunk.Size > 0 && chunk.StoredSize > 0
                      && chunk.StoredOffset >= 32UL + (ulong)count * 32UL
                      && chunk.StoredOffset <= (ulong)_stream.Length
                      && (ulong)chunk.StoredSize <= (ulong)_stream.Length - chunk.StoredOffset
                      && (index == 0 || _chunks[index - 1].Offset + (ulong)_chunks[index - 1].Size <= chunk.Offset)))
                    throw new InvalidDataException("DSAR 块表无效。");
                _chunks[index] = chunk;
            }
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    public byte[] Read(ulong offset, int size)
    {
        if (_chunks.Length == 0) return size == 0 ? [] : throw new InvalidDataException("DSAR 块表为空。");
        var result = new byte[size];
        var written = 0;
        var index = FindChunk(offset);
        var wanted = offset;
        while (written < size && index < _chunks.Length)
        {
            var chunk = _chunks[index];
            if (!(chunk.Offset <= wanted && wanted < chunk.Offset + (ulong)chunk.Size)) throw new InvalidDataException("DSAR range gap");
            var skip = (int)(wanted - chunk.Offset);
            var count = Math.Min(size - written, chunk.Size - skip);
            var raw = Decode(index, chunk);
            Buffer.BlockCopy(raw, skip, result, written, count);
            written += count;
            wanted += (ulong)count;
            index++;
        }
        if (written != size) throw new InvalidDataException("DSAR range incomplete");
        return result;
    }

    public byte[] ReadAll(int limit)
    {
        if (_chunks.Length == 0) return [];
        var last = _chunks[^1];
        var total = last.Offset + (ulong)last.Size;
        if (total > (ulong)limit) throw new InvalidDataException("游戏归档索引过大。");
        return Read(0, (int)total);
    }

    private int FindChunk(ulong offset)
    {
        var low = 0;
        var high = _chunks.Length - 1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (_chunks[mid].Offset <= offset) low = mid + 1;
            else high = mid - 1;
        }
        return high;
    }

    private byte[] Decode(int index, IasChunk chunk)
    {
        if (_cache.TryGetValue(index, out var cached)) return cached;
        _stream.Position = checked((long)chunk.StoredOffset);
        var encoded = new byte[chunk.StoredSize];
        _stream.ReadExactly(encoded);
        byte[] decoded;
        if (chunk.Codec == 0)
        {
            if (chunk.Size != chunk.StoredSize) throw new InvalidDataException("DSAR raw chunk size");
            decoded = encoded;
        }
        else if (chunk.Codec == 3)
        {
            decoded = new byte[chunk.Size];
            if (LZ4Codec.Decode(encoded, decoded) != decoded.Length) throw new InvalidDataException("DSAR LZ4 解压长度不一致。");
        }
        else throw new InvalidDataException("unsupported DSAR codec");
        if (_cache.Count > 8) _cache.Remove(_cache.Keys.First());
        _cache[index] = decoded;
        return decoded;
    }

    public void Dispose() => _stream.Dispose();
}
