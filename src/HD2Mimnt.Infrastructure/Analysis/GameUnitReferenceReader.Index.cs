using System.Text;

namespace HD2Mimnt.Infrastructure.Analysis;

public sealed partial class GameUnitReferenceReader
{
    private sealed record BundleDescriptor(string Path, Chunk[] Chunks);
    private sealed record Package(string Name, PackageItem[] Items, ulong[] Filter);
    private readonly record struct UnitLocator(int Package, ulong Offset, uint Size, uint GpuSize);
    private sealed record ArchiveSnapshot(string Fingerprint, BundleDescriptor[] Bundles, Package[] Packages,
        Dictionary<ulong, List<UnitLocator>> Units, DateTime BuiltAtUtc);

    private static string GetFingerprint(string directory)
    {
        var root = Path.GetFullPath(directory);
        var builder = new StringBuilder(root);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "data"), "bundles*.nxa").Order(StringComparer.OrdinalIgnoreCase))
        {
            var file = new FileInfo(path);
            builder.Append('|').Append(file.Name).Append(':').Append(file.Length).Append(':')
                .Append(file.LastWriteTimeUtc.Ticks).Append(':').Append(file.CreationTimeUtc.Ticks);
        }
        return builder.ToString();
    }

    private ArchiveSnapshot GetSnapshot(string directory, CancellationToken token, bool force = false, IProgress<HD2Mimnt.Core.Entities.GameReferenceBuildProgress>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        var fingerprint = GetFingerprint(directory);
        _snapshotReused = false;
        if (!force && _snapshot?.Fingerprint == fingerprint)
        {
            if (_cachePath is not null && !File.Exists(_cachePath)) SaveSnapshot(_snapshot, token);
            _snapshotReused = true; return _snapshot;
        }
        if (!force && TryLoadSnapshot(directory, fingerprint, token) is { } saved)
        {
            _snapshotReused = true;
            return _snapshot = saved;
        }
        var root = Path.Combine(Path.GetFullPath(directory), "data");
        using var index = new Bundle(Path.Combine(root, "bundles.nxa"), token);
        var data = index.ReadIndex(token);
        Require(data.Length >= 24, "游戏归档索引头部不完整。");
        var count = U32(data, 12);
        var packageCount = U32(data, 16);
        Require(count is > 0 and <= 256 && packageCount <= 1_000_000
            && 24UL + packageCount * 24UL <= (ulong)data.Length, "游戏包表无效。");
        var descriptors = new BundleDescriptor[count];
        for (var i = 0; i < descriptors.Length; i++)
        {
            var path = Path.Combine(root, $"bundles.{i:00}.nxa");
            using var bundle = new Bundle(path, token);
            descriptors[i] = new BundleDescriptor(path, bundle.Chunks);
        }
        var packages = new Package[packageCount];
        for (var p = 0; p < packages.Length; p++)
        {
            token.ThrowIfCancellationRequested();
            var offset = 24 + p * 24;
            var name = ReadName(data, U32(data, offset + 8));
            var itemCount = U32(data, offset + 12);
            var start = U32(data, offset + 16);
            Require(itemCount is > 0 and <= 100_000 && start + itemCount * 16UL <= (ulong)data.Length, "游戏包映射表越界。");
            var items = new PackageItem[itemCount];
            for (var i = 0; i < items.Length; i++)
            {
                var entry = checked((int)start + i * 16);
                items[i] = new PackageItem(U64(data, entry), U32(data, entry + 8), data[entry + 15]);
                Require(items[i].BundleIndex < count && (i == 0 || items[i - 1].ArchiveOffset < items[i].ArchiveOffset), "游戏包映射顺序无效。");
            }
            packages[p] = new Package(name, items, new ulong[16]);
        }
        var snapshot = new ArchiveSnapshot(fingerprint, descriptors, packages, [], DateTime.UtcNow);
        using var session = new ArchiveSession(snapshot, token);
        for (var p = 0; p < packages.Length; p++)
        {
            token.ThrowIfCancellationRequested();
            if (p % 32 == 0) progress?.Report(new(p, packages.Length));
            var package = packages[p];
            if (!IsMainPackage(package.Name)) continue;
            var (toc, start, resources) = session.ReadToc(package);
            for (var i = 0; i < resources; i++)
            {
                var entry = start + i * 80;
                var id = U64(toc, entry);
                FilterAdd(package.Filter, id);
                if (U64(toc, entry + 8) != UnitTypeId) continue;
                if (!snapshot.Units.TryGetValue(id, out var locators)) snapshot.Units[id] = locators = [];
                locators.Add(new UnitLocator(p, U64(toc, entry + 16), U32(toc, entry + 56), U32(toc, entry + 64)));
            }
        }
        Require(fingerprint == GetFingerprint(directory), "游戏归档在索引构建期间发生变化。");
        SaveSnapshot(snapshot, token);
        progress?.Report(new(packages.Length, packages.Length));
        return _snapshot = snapshot;
    }

    private static bool IsMainPackage(string name) => !name.Contains(".patch", StringComparison.OrdinalIgnoreCase)
        && !name.EndsWith(".stream", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase);

    // 每包 128 字节过滤器；伴随恢复跳过不含目标 ID 的包，不为全游戏每个资源分配字典条目。
    private static void FilterAdd(ulong[] filter, ulong id)
    {
        var (first, second) = FilterBits(id);
        filter[first >> 6] |= 1UL << (first & 63);
        filter[second >> 6] |= 1UL << (second & 63);
    }
    private static bool FilterContains(ulong[] filter, ulong id)
    {
        var (first, second) = FilterBits(id);
        return (filter[first >> 6] & (1UL << (first & 63))) != 0 && (filter[second >> 6] & (1UL << (second & 63))) != 0;
    }
    private static (int First, int Second) FilterBits(ulong id)
    {
        var mixed = unchecked((id ^ (id >> 33)) * 0xff51afd7ed558ccdUL);
        return ((int)(mixed & 1023), (int)((mixed >> 32) & 1023));
    }

    private sealed class ArchiveSession(ArchiveSnapshot snapshot, CancellationToken token) : IDisposable
    {
        private readonly Dictionary<int, Bundle> _opened = [];
        private readonly DsarDecodeCache _cache = new();
        public byte[] ReadRange(PackageItem[] items, ulong offset, int size)
        {
            Require(size is > 0 and <= MaxResourceBytes, "游戏资源读取尺寸无效。");
            var output = new byte[size];
            var written = 0;
            while (written < size)
            {
                token.ThrowIfCancellationRequested();
                var index = Array.FindLastIndex(items, item => item.ArchiveOffset <= offset);
                Require(index >= 0, "游戏资源缺少映射。");
                var item = items[index];
                if (!_opened.TryGetValue(item.BundleIndex, out var bundle))
                {
                    var descriptor = snapshot.Bundles[item.BundleIndex];
                    _opened[item.BundleIndex] = bundle = new Bundle(descriptor.Path, token, descriptor.Chunks, _cache);
                }
                var count = size - written;
                if (index + 1 < items.Length) count = (int)Math.Min((ulong)count, items[index + 1].ArchiveOffset - offset);
                var part = bundle.ReadRange(checked(item.BundleOffset + offset - item.ArchiveOffset), count, token);
                part.CopyTo(output, written);
                written += count;
                offset = checked(offset + (uint)count);
            }
            return output;
        }
        public (byte[] Data, int Start, int Count) ReadToc(Package package)
        {
            var first = package.Items[0];
            var header = ReadRange(package.Items, first.ArchiveOffset, 72);
            Require(U32(header, 0) == 0xf0000011 && U32(header, 4) <= 1000 && U32(header, 8) <= 100000, "游戏 TOC 无效。");
            var start = checked(72 + (int)U32(header, 4) * 32);
            var count = (int)U32(header, 8);
            return (ReadRange(package.Items, first.ArchiveOffset, checked(start + count * 80)), start, count);
        }
        public void Dispose() { foreach (var bundle in _opened.Values) bundle.Dispose(); }
    }

    private sealed class DsarDecodeCache
    {
        private const int Capacity = 16 * 1024 * 1024;
        private readonly LinkedList<((string Path, ulong Offset) Key, byte[] Data)> _lru = [];
        private readonly Dictionary<(string, ulong), LinkedListNode<((string Path, ulong Offset) Key, byte[] Data)>> _entries = [];
        private int _bytes;
        public byte[]? Get(string path, ulong offset)
        {
            if (!_entries.TryGetValue((path, offset), out var node)) return null;
            _lru.Remove(node);
            _lru.AddLast(node);
            return node.Value.Data;
        }
        public void Add(string path, ulong offset, byte[] data)
        {
            if (data.Length > Capacity) return;
            while (_bytes + data.Length > Capacity && _lru.First is { } first)
            {
                _entries.Remove(first.Value.Key);
                _bytes -= first.Value.Data.Length;
                _lru.RemoveFirst();
            }
            var node = _lru.AddLast(((path, offset), data));
            _entries[(path, offset)] = node;
            _bytes += data.Length;
        }
    }
}
