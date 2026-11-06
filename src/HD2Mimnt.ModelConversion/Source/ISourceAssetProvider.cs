using HD2Mimnt.Core.IO;
using HD2Mimnt.ModelConversion.Source.Gma;

namespace HD2Mimnt.ModelConversion.Source;

/// <summary>Read-only view of an addon's files, addressed by addon-relative forward-slash paths.</summary>
public interface ISourceAssetProvider : IDisposable
{
    IEnumerable<string> EnumeratePaths(CancellationToken cancellationToken = default);

    bool TryRead(string relativePath, out byte[] data, CancellationToken cancellationToken = default);
}

public sealed class GmaAssetProvider : ISourceAssetProvider
{
    private readonly GmaArchive _archive;

    public GmaAssetProvider(GmaArchive archive)
    {
        _archive = archive;
    }

    public GmaAssetProvider(string path, CancellationToken cancellationToken = default)
        : this(new GmaArchive(path, cancellationToken))
    {
    }

    public GmaArchive Archive => _archive;

    public IEnumerable<string> EnumeratePaths(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var entry in _archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry.Path;
        }
    }

    public bool TryRead(string relativePath, out byte[] data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_archive.TryGetEntry(relativePath, out var entry))
        {
            data = _archive.Read(entry, cancellationToken);
            return true;
        }

        data = Array.Empty<byte>();
        return false;
    }

    public void Dispose() => _archive.Dispose();
}

public sealed class DirectoryAssetProvider : ISourceAssetProvider
{
    private readonly string _root;

    public DirectoryAssetProvider(string root, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException(_root);
        }

        GetSafeAttributes(_root, cancellationToken);
    }

    public IEnumerable<string> EnumeratePaths(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directories = new Stack<string>();
        directories.Push(_root);
        while (directories.TryPop(out var directory))
        {
            GetSafeAttributes(directory, cancellationToken);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var full = PathGuard.ResolveInsideRoot(_root, entry);
                var attributes = GetSafeAttributes(full, cancellationToken);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(full);
                }
                else
                {
                    yield return GmaArchive.NormalizePath(Path.GetRelativePath(_root, full));
                }
            }
        }
    }

    public bool TryRead(string relativePath, out byte[] data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        data = Array.Empty<byte>();
        var normalized = GmaArchive.NormalizePath(relativePath);
        var full = PathGuard.ResolveInsideRoot(_root, Path.Combine(_root, normalized));
        try
        {
            if ((GetSafeAttributes(full, cancellationToken) & FileAttributes.Directory) != 0)
            {
                return false;
            }

            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.Length > GmaArchive.MaxMemberSize)
            {
                throw new SourceFormatException($"Addon file {normalized} exceeds the member size limit");
            }

            var buffer = new byte[(int)stream.Length];
            var offset = 0;
            while (offset < buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(buffer.Length - offset, 64 * 1024);
                stream.ReadExactly(buffer.AsSpan(offset, count));
                offset += count;
            }

            cancellationToken.ThrowIfCancellationRequested();
            data = buffer;
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private FileAttributes GetSafeAttributes(string fullPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = _root;
        var attributes = File.GetAttributes(current);
        RejectReparsePoint(current, attributes);
        if (string.Equals(fullPath, _root, StringComparison.OrdinalIgnoreCase))
        {
            return attributes;
        }

        var relativePath = GmaArchive.NormalizePath(Path.GetRelativePath(_root, fullPath));
        foreach (var segment in relativePath.Split('/'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = Path.Combine(current, segment);
            attributes = File.GetAttributes(current);
            RejectReparsePoint(current, attributes);
        }

        return attributes;
    }

    private static void RejectReparsePoint(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new SourceFormatException($"Addon paths cannot traverse a reparse point: {path}");
        }
    }

    public void Dispose()
    {
    }
}
