using System.Buffers.Binary;
using System.Text;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using K4os.Compression.LZ4;

namespace HD2Mimnt.Infrastructure.Analysis;

/// <summary>DSAR 只读入口。TOC 定位缓存按归档指纹失效，资源按需读取，句柄限于单次调用。</summary>
public sealed partial class GameUnitReferenceReader(string? cacheRoot = null) : IGameUnitReferenceReader, IGameReferenceCache
{
    private const int MaxResourceBytes = 64 * 1024 * 1024;
    private const int MaxIndexBytes = 256 * 1024 * 1024;
    private const ulong UnitTypeId = 0xe0a48d0be9a7453f;
    private readonly record struct Chunk(ulong Offset, ulong StoredOffset, int Size, int StoredSize, byte Compression, byte Flags);
    private sealed record PackageItem(ulong ArchiveOffset, uint BundleOffset, byte BundleIndex);
    private sealed record Candidate(uint Version, string Package, byte[]? Data, uint GpuSize);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ArchiveSnapshot? _snapshot;
    private readonly string? _cachePath = cacheRoot is null ? null : Path.Combine(cacheRoot, "compatibility", "game-reference-index.bin");
    private bool _snapshotReused;

    public Task<GameUnitReferenceReport> ReadAsync(string gameDirectory, IReadOnlyCollection<ulong> resourceIds,
        CancellationToken cancellationToken = default) => ReadUnitsAsync(gameDirectory, resourceIds, false, cancellationToken);

    public Task<GameUnitReferenceReport> ReadDetailedAsync(string gameDirectory, IReadOnlyCollection<ulong> resourceIds,
        CancellationToken cancellationToken = default) => ReadUnitsAsync(gameDirectory, resourceIds, true, cancellationToken);

    private async Task<GameUnitReferenceReport> ReadUnitsAsync(string directory, IReadOnlyCollection<ulong> ids, bool detailed, CancellationToken token)
    {
        var requested = ids.Distinct().ToArray();
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(() => ReadUnitsCore(directory, requested, detailed, token), token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private GameUnitReferenceReport ReadUnitsCore(string directory, ulong[] ids, bool detailed, CancellationToken token)
    {
        var report = new GameUnitReferenceReport { Source = directory };
        if (ids.Length == 0) return report;
        try
        {
            var snapshot = GetSnapshot(directory, token);
            report.IndexCacheHit = _snapshotReused;
            using var session = new ArchiveSession(snapshot, token);
            foreach (var id in ids)
            {
                token.ThrowIfCancellationRequested();
                var matches = new List<Candidate>();
                string? error = null;
                if (snapshot.Units.TryGetValue(id, out var locators))
                    foreach (var locator in locators)
                    {
                        try
                        {
                            Require(locator.Size >= (detailed ? 0x78 : 0x30) && locator.Size <= MaxResourceBytes, "游戏 Unit 尺寸无效。");
                            var package = snapshot.Packages[locator.Package];
                            var data = session.ReadRange(package.Items, locator.Offset, detailed ? (int)locator.Size : 0x30);
                            if (detailed)
                            {
                                // 游戏 TOC 可包含结束记录之后的填充；这里只提取参考，不重建其尺寸。
                                var ending = U32(data, 0x60);
                                Require(ending >= 0x60 && ending + 8UL <= (ulong)data.Length, "游戏 Unit 结束位置越界。");
                                _ = GetLod(data);
                            }
                            matches.Add(new Candidate(U32(data, 0x2c), package.Name, detailed ? data : null, locator.GpuSize));
                        }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException) { error = ex.Message; }
                    }
                var signatures = matches.Select(c => detailed
                    ? $"{c.Version}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(GetLod(c.Data!)))}"
                    : c.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)).Distinct().Count();
                var status = error is not null ? GameUnitReferenceStatus.Error : signatures > 1 ? GameUnitReferenceStatus.Ambiguous
                    : signatures == 1 ? GameUnitReferenceStatus.Found : GameUnitReferenceStatus.Missing;
                var selected = status == GameUnitReferenceStatus.Found ? matches[0] : null;
                report.Units[id] = new GameUnitReference { ResourceId = id, Status = status, Version = selected?.Version,
                    UnitData = selected?.Data, LodGroupData = detailed && selected is not null ? GetLod(selected.Data!) : [],
                    MeshIds = detailed && selected is not null ? ModCompatibilityAnalyzer.ReadMeshIds(selected.Data!) : [],
                    GpuSize = selected?.GpuSize ?? 0, Packages = matches.Select(m => m.Package).Distinct().ToArray(), ErrorMessage = error };
            }
            // 整个读取期间保持共享只读句柄；最后再次校验，避免更新期间混用两个快照。
            Require(snapshot.Fingerprint == GetFingerprint(directory), "游戏归档在读取期间发生变化。");
            report.IndexComplete = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            report.Issues.Add($"无法完整读取游戏参考：{ex.Message}");
            foreach (var id in ids) report.Units[id] = new GameUnitReference { ResourceId = id, Status = GameUnitReferenceStatus.Error, ErrorMessage = ex.Message };
        }
        return report;
    }

    private static byte[] GetLod(byte[] data)
    {
        var start = U32(data, 0x30);
        var end = U32(data, 0x34);
        if (start == 0 && end == 0) return [];
        Require(start >= 0x68 && end > start && end <= data.Length, "游戏 Unit LOD 范围无效。");
        return data.AsSpan((int)start, (int)(end - start)).ToArray();
    }
    private sealed class Bundle : IDisposable
    {
        private readonly FileStream _stream;
        private readonly Chunk[] _chunks;
        public Chunk[] Chunks => _chunks;
        private readonly DsarDecodeCache? _cache;
        private readonly string _path;

        public Bundle(string path, CancellationToken token, Chunk[]? knownChunks = null, DsarDecodeCache? cache = null)
        {
            _cache = cache;
            _path = path;
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.RandomAccess);
            try
            {
                if (knownChunks is not null) { _chunks = knownChunks; return; }
                var buffer = new byte[32];
                _stream.ReadExactly(buffer);
                Require(U32(buffer, 0) == 0x52415344, "DSAR magic 无效。");
                var count = U32(buffer, 8);
                Require(count is > 0 and <= 10_000_000 && 32UL + count * 32UL <= (ulong)_stream.Length,
                    "DSAR 块表无效。");
                _chunks = new Chunk[count];
                for (var i = 0; i < _chunks.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    _stream.ReadExactly(buffer);
                    var size = U32(buffer, 16);
                    var stored = U32(buffer, 20);
                    var chunk = new Chunk(U64(buffer, 0), U64(buffer, 8), checked((int)size), checked((int)stored), buffer[24], buffer[25]);
                    // 原版归档的未压缩块可大于 64 MiB；块表的物理范围与单次资源读取上限分开校验。
                    if (!(size > 0 && stored > 0
                        && chunk.StoredOffset >= 32UL + count * 32UL
                        && chunk.StoredOffset <= (ulong)_stream.Length
                        && stored <= (ulong)_stream.Length - chunk.StoredOffset
                        && chunk.Offset <= ulong.MaxValue - size
                        && (i == 0 || _chunks[i - 1].Offset + (ulong)_chunks[i - 1].Size <= chunk.Offset)))
                        throw new InvalidDataException($"DSAR 块范围或顺序无效：{Path.GetFileName(path)}，块 {i}，逻辑偏移 {chunk.Offset}，存储偏移 {chunk.StoredOffset}，大小 {size}/{stored}。");
                    _chunks[i] = chunk;
                }
            }
            catch { _stream.Dispose(); throw; }
        }

        public byte[] ReadIndex(CancellationToken token)
        {
            var last = _chunks[^1];
            var size = last.Offset + (ulong)last.Size;
            Require(size <= MaxIndexBytes, "游戏归档索引过大。");
            var data = new byte[(int)size];
            var position = 0;
            foreach (var chunk in _chunks)
            {
                Require(chunk.Offset == (ulong)position, "游戏归档索引块不连续。");
                ReadChunkRange(chunk, 0, data.AsSpan(position, chunk.Size), token);
                position += chunk.Size;
            }
            return data;
        }

        public byte[] ReadRange(ulong offset, int length, CancellationToken token)
        {
            Require(length is > 0 and <= MaxResourceBytes, "游戏资源读取长度无效。");
            // Unit/TOC 可能位于块中间；以包含该偏移的块开始读取。
            var low = 0;
            var high = _chunks.Length - 1;
            while (low <= high)
            {
                var mid = low + (high - low) / 2;
                if (_chunks[mid].Offset <= offset) low = mid + 1;
                else high = mid - 1;
            }
            Require(high >= 0, "游戏资源偏移不在块表中。");
            var output = new byte[length];
            var written = 0;
            for (var i = high; written < length && i < _chunks.Length; i++)
            {
                var chunk = _chunks[i];
                Require(offset >= chunk.Offset && offset - chunk.Offset < (ulong)chunk.Size
                    && (written == 0 || (chunk.Flags & 2) == 0), "游戏资源范围越过块或资源边界。");
                var skip = (int)(offset - chunk.Offset);
                var count = Math.Min(length - written, chunk.Size - skip);
                ReadChunkRange(chunk, skip, output.AsSpan(written, count), token);
                written += count;
                offset = checked(offset + (ulong)count);
            }
            Require(written == length, "游戏资源范围超出归档。");
            return output;
        }

        private void ReadChunkRange(Chunk chunk, int skip, Span<byte> destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (chunk.Compression == 0)
            {
                Require(chunk.Size == chunk.StoredSize, "未压缩 DSAR 块的大小不一致。");
                // 大块中的 TOC/Unit 通常只需几十字节，直接读取对应区间，不分配整块缓冲区。
                _stream.Position = checked((long)(chunk.StoredOffset + (ulong)skip));
                _stream.ReadExactly(destination);
            }
            else Decode(chunk, token).AsSpan(skip, destination.Length).CopyTo(destination);
            token.ThrowIfCancellationRequested();
        }

        private byte[] Decode(Chunk chunk, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_cache?.Get(_path, chunk.StoredOffset) is { } cached) return cached;
            Require(chunk.Compression == 3, $"不支持 DSAR 压缩类型 {chunk.Compression}。");
            Require(chunk.Size <= MaxResourceBytes && chunk.StoredSize <= MaxResourceBytes + 1024 * 1024,
                "DSAR 压缩块超过当前解码上限。");
            _stream.Position = checked((long)chunk.StoredOffset);
            var encoded = new byte[chunk.StoredSize];
            _stream.ReadExactly(encoded);
            var decoded = new byte[chunk.Size];
            Require(LZ4Codec.Decode(encoded, decoded) == decoded.Length, "DSAR LZ4 解压长度不一致。");
            token.ThrowIfCancellationRequested();
            _cache?.Add(_path, chunk.StoredOffset, decoded);
            return decoded;
        }

        public void Dispose() => _stream.Dispose();
    }

    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    private static ulong U64(byte[] data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8));
    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidDataException(message);
    }

    private static string ReadName(byte[] data, uint offset)
    {
        Require(offset < data.Length, "游戏包名称偏移越界。");
        var end = Array.IndexOf(data, (byte)0, (int)offset);
        Require(end > offset, "游戏包名称缺失终止符。");
        return Encoding.UTF8.GetString(data, (int)offset, end - (int)offset);
    }
}
