using System.IO.Compression;
using HD2Mimnt.Core.Darktide;
using HD2Mimnt.Core.IO;

namespace HD2Mimnt.Infrastructure.Darktide;

public sealed class DarktideArchiveInspector
{
    private const int MaxEntries = 100_000;
    private const long MaxTotalBytes = 16L * 1024 * 1024 * 1024;

    public DarktideArchiveInspection Inspect(string archivePath)
    {
        try
        {
            if (!File.Exists(archivePath))
                return new DarktideArchiveInspection(archivePath, Array.Empty<DarktideArchiveCandidate>(), "压缩包不存在。");
            using var archive = ZipFile.OpenRead(archivePath);
            var entries = ReadEntries(archive);
            if (entries.Error != null)
                return new DarktideArchiveInspection(archivePath, Array.Empty<DarktideArchiveCandidate>(), entries.Error);

            var (candidates, error, kind, extra) = DarktidePackageClassifier.Classify(
                entries.Files.Select(file => (file.Path, file.Entry.Length)).ToList());
            return new DarktideArchiveInspection(archivePath, candidates, error, kind, extra);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new DarktideArchiveInspection(archivePath, Array.Empty<DarktideArchiveCandidate>(),
                $"无法读取压缩包：{ex.Message}");
        }
    }

    public async Task ExtractCandidateAsync(string archivePath, DarktideArchiveCandidate candidate,
        string destinationRoot, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = ReadEntries(archive);
        if (entries.Error != null) throw new InvalidDataException(entries.Error);
        Directory.CreateDirectory(destinationRoot);
        var selected = entries.Files.Where(file => IsUnderRoot(file.Path, candidate.RelativeRoot)).ToList();
        if (selected.Count == 0) throw new InvalidDataException("压缩包中的候选模组目录为空。");
        var total = Math.Max(1, selected.Sum(file => Math.Max(0, file.Entry.Length)));
        if (total > MaxTotalBytes) throw new InvalidDataException("压缩包解压后体积超过限制。");
        long written = 0;

        foreach (var (entry, path) in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = candidate.RelativeRoot.Length == 0 ? path : path[(candidate.RelativeRoot.Length + 1)..];
            if (relative.Length == 0) continue;
            var target = Path.GetFullPath(Path.Combine(destinationRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            PathGuard.ResolveInsideRoot(destinationRoot, target, "压缩包条目越过了暂存目录。");
            var directory = Path.GetDirectoryName(target);
            if (directory != null) Directory.CreateDirectory(directory);

            await using var source = entry.Open();
            await using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true);
            var buffer = new byte[81920];
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                written += read;
                progress?.Report(Math.Min(100d, written * 100d / total));
            }
        }

        var modFile = Path.Combine(destinationRoot, candidate.InternalName + ".mod");
        if (!File.Exists(modFile) || new FileInfo(modFile).Length == 0)
            throw new InvalidDataException($"暂存目录缺少有效的 {candidate.InternalName}.mod。");
        progress?.Report(100d);
    }

    private static (List<(ZipArchiveEntry Entry, string Path)> Files, string? Error) ReadEntries(ZipArchive archive)
    {
        var files = new List<(ZipArchiveEntry, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (files.Count >= MaxEntries) return (files, "压缩包条目过多，已停止检查。");
            var normalized = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (normalized.Length == 0) continue;
            if (!IsSafeEntryPath(normalized)) return (files, $"压缩包包含不安全路径：{entry.FullName}");
            if (IsSymbolicLink(entry)) return (files, $"压缩包包含符号链接条目：{entry.FullName}");
            if (!normalized.EndsWith('/') && !seen.Add(normalized))
                return (files, $"压缩包包含重复条目：{entry.FullName}");
            total += Math.Max(0, entry.Length);
            if (total > MaxTotalBytes) return (files, "压缩包解压后体积超过限制。");
            if (normalized.EndsWith('/')) continue;
            files.Add((entry, normalized));
        }
        return (files, null);
    }

    private static bool IsSafeEntryPath(string path)
    {
        if (path.Length == 0 || path[0] == '/' || path.Contains('\0')) return false;
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':') return false;
        var segments = path.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0) continue;
            if (segment is "." or "..") return false;
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            if (segment.EndsWith('.') || segment.EndsWith(' ')) return false;
            if (IsReservedDeviceName(Path.GetFileNameWithoutExtension(segment))) return false;
        }
        return true;
    }

    private static bool IsReservedDeviceName(string name)
        => ReservedDeviceNames.Contains(name);

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        var mode = (entry.ExternalAttributes >> 16) & 0xF000;
        return mode == 0xA000;
    }

    private static bool IsUnderRoot(string path, string root)
        => root.Length == 0 || path.Equals(root, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
}
