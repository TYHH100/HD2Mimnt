using System.Security.Cryptography;
using System.Text;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Infrastructure.Analysis;

public sealed partial class GameUnitReferenceReader
{
    private const int CacheMagic = 0x48443249;
    private const int CacheVersion = 1;

    public async Task<GameReferenceCacheStatus> GetCacheStatusAsync(string gameDirectory, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => GetCacheStatusCore(gameDirectory, cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private GameReferenceCacheStatus GetCacheStatusCore(string directory, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var fingerprint = GetFingerprint(directory);
            Require(File.Exists(Path.Combine(directory, "data", "bundles.nxa")), "请先设置有效的游戏目录。");
            if (_cachePath is null || !File.Exists(_cachePath)) return new() { CachePath = _cachePath ?? string.Empty };
            using var file = File.OpenRead(_cachePath);
            using var reader = new BinaryReader(file, Encoding.UTF8);
            file.Position = 32;
            Require(file.Length <= MaxIndexBytes && reader.ReadInt32() == CacheMagic && reader.ReadInt32() == CacheVersion, "游戏索引缓存格式已过期。");
            var savedFingerprint = reader.ReadString();
            var built = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
            var packages = reader.ReadInt32();
            var units = reader.ReadInt32();
            Require(packages is >= 0 and <= 1_000_000 && units is >= 0 and <= 10_000_000, "游戏索引缓存计数无效。");
            return new() { State = fingerprint == savedFingerprint ? GameReferenceCacheState.Ready : GameReferenceCacheState.Stale,
                CachePath = _cachePath, BuiltAtUtc = built, PackageCount = packages, UnitCount = units };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            return new() { State = File.Exists(Path.Combine(directory, "data", "bundles.nxa"))
                    ? GameReferenceCacheState.Stale : GameReferenceCacheState.Unavailable, CachePath = _cachePath ?? string.Empty, Error = ex.Message };
        }
    }

    public async Task<GameReferenceCacheStatus> BuildCacheAsync(string gameDirectory, bool force = false,
        IProgress<GameReferenceBuildProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                Require(_cachePath is not null, "游戏索引缓存目录未配置。");
                var snapshot = GetSnapshot(gameDirectory, cancellationToken, force, progress);
                Require(snapshot.Fingerprint == GetFingerprint(gameDirectory), "游戏归档在索引构建期间发生变化。");
                return new GameReferenceCacheStatus { State = GameReferenceCacheState.Ready, CachePath = _cachePath!,
                    BuiltAtUtc = snapshot.BuiltAtUtc, PackageCount = snapshot.Packages.Length, UnitCount = snapshot.Units.Count };
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private ArchiveSnapshot? TryLoadSnapshot(string directory, string fingerprint, CancellationToken token)
    {
        if (_cachePath is null || !File.Exists(_cachePath)) return null;
        try
        {
            using var file = File.OpenRead(_cachePath);
            Require(file.Length is >= 64 and <= MaxIndexBytes, "游戏索引缓存尺寸无效。");
            using var reader = new BinaryReader(file, Encoding.UTF8);
            var digest = reader.ReadBytes(32);
            using var hash = SHA256.Create();
            using (var hashing = new CryptoStream(file, hash, CryptoStreamMode.Read, leaveOpen: true))
            {
                var buffer = new byte[65536];
                while (hashing.Read(buffer) > 0) token.ThrowIfCancellationRequested();
            }
            Require(hash.Hash!.AsSpan().SequenceEqual(digest), "游戏索引缓存校验失败。");
            file.Position = 32;
            Require(reader.ReadInt32() == CacheMagic && reader.ReadInt32() == CacheVersion, "游戏索引缓存版本不匹配。");
            if (reader.ReadString() != fingerprint) return null;
            var built = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
            var packageCount = ReadCount(reader, 1_000_000);
            var unitCount = ReadCount(reader, 10_000_000);
            var bundleCount = ReadCount(reader, 256);
            // 归档路径从当前安装目录派生，不信任缓存中的路径。
            var bundles = new BundleDescriptor[bundleCount];
            for (var i = 0; i < bundleCount; i++)
            {
                token.ThrowIfCancellationRequested();
                var chunks = new Chunk[ReadCount(reader, 10_000_000)];
                for (var c = 0; c < chunks.Length; c++)
                {
                    chunks[c] = new(reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadByte(), reader.ReadByte());
                    Require(chunks[c].Size > 0 && chunks[c].StoredSize > 0, "缓存块尺寸无效。");
                }
                bundles[i] = new(Path.Combine(Path.GetFullPath(directory), "data", $"bundles.{i:00}.nxa"), chunks);
            }
            var packages = new Package[packageCount];
            for (var p = 0; p < packages.Length; p++)
            {
                token.ThrowIfCancellationRequested();
                var name = reader.ReadString();
                var items = new PackageItem[ReadCount(reader, 100_000)];
                for (var i = 0; i < items.Length; i++)
                {
                    items[i] = new(reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadByte());
                    Require(items[i].BundleIndex < bundleCount, "缓存 bundle 编号无效。");
                }
                var filter = new ulong[16];
                for (var f = 0; f < filter.Length; f++) filter[f] = reader.ReadUInt64();
                packages[p] = new(name, items, filter);
            }
            var units = new Dictionary<ulong, List<UnitLocator>>(unitCount);
            for (var u = 0; u < unitCount; u++)
            {
                token.ThrowIfCancellationRequested();
                var id = reader.ReadUInt64();
                var locators = new List<UnitLocator>();
                var count = ReadCount(reader, 1_000_000);
                for (var i = 0; i < count; i++)
                {
                    var locator = new UnitLocator(reader.ReadInt32(), reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadUInt32());
                    Require(locator.Package >= 0 && locator.Package < packages.Length, "缓存包编号无效。");
                    locators.Add(locator);
                }
                units.Add(id, locators);
            }
            Require(file.Position == file.Length, "游戏索引缓存长度不一致。");
            return new(fingerprint, bundles, packages, units, built);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException or CryptographicException)
        { return null; } // 缓存失效时从当前归档重建，绝不复用陈旧定位。
    }

    private static int ReadCount(BinaryReader reader, int max)
    {
        var count = reader.ReadInt32();
        Require(count >= 0 && count <= max && count <= reader.BaseStream.Length - reader.BaseStream.Position, "缓存计数无效。");
        return count;
    }

    private void SaveSnapshot(ArchiveSnapshot snapshot, CancellationToken token)
    {
        if (_cachePath is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        var temporary = _cachePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(new byte[32]);
                using var hash = SHA256.Create();
                using (var hashing = new CryptoStream(file, hash, CryptoStreamMode.Write, leaveOpen: true))
                using (var writer = new BinaryWriter(hashing, Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(CacheMagic); writer.Write(CacheVersion); writer.Write(snapshot.Fingerprint);
                    writer.Write(snapshot.BuiltAtUtc.Ticks); writer.Write(snapshot.Packages.Length); writer.Write(snapshot.Units.Count);
                    writer.Write(snapshot.Bundles.Length);
                    foreach (var bundle in snapshot.Bundles)
                    {
                        token.ThrowIfCancellationRequested(); writer.Write(bundle.Chunks.Length);
                        foreach (var chunk in bundle.Chunks)
                        { writer.Write(chunk.Offset); writer.Write(chunk.StoredOffset); writer.Write(chunk.Size); writer.Write(chunk.StoredSize); writer.Write(chunk.Compression); writer.Write(chunk.Flags); }
                    }
                    foreach (var package in snapshot.Packages)
                    {
                        token.ThrowIfCancellationRequested(); writer.Write(package.Name); writer.Write(package.Items.Length);
                        foreach (var item in package.Items) { writer.Write(item.ArchiveOffset); writer.Write(item.BundleOffset); writer.Write(item.BundleIndex); }
                        foreach (var filter in package.Filter) writer.Write(filter);
                    }
                    foreach (var (id, locators) in snapshot.Units)
                    {
                        token.ThrowIfCancellationRequested(); writer.Write(id); writer.Write(locators.Count);
                        foreach (var locator in locators) { writer.Write(locator.Package); writer.Write(locator.Offset); writer.Write(locator.Size); writer.Write(locator.GpuSize); }
                    }
                }
                Require(file.Length <= MaxIndexBytes, "游戏索引缓存过大。");
                file.Position = 0; file.Write(hash.Hash!); file.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            Require(snapshot.Fingerprint == GetFingerprint(Path.GetDirectoryName(Path.GetDirectoryName(snapshot.Bundles[0].Path)!)!),
                "游戏归档在索引保存期间发生变化。");
            File.Move(temporary, _cachePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
