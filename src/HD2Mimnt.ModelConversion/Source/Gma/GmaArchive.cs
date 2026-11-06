using System.Buffers.Binary;
using System.Text;

namespace HD2Mimnt.ModelConversion.Source.Gma;

public sealed record GmaEntry(int Index, string Path, long Size, uint Crc32, long DataOffset);

/// <summary>Read-only GMAD archive (tested with v3).</summary>
// Format: https://github.com/Facepunch/gmad; license: https://github.com/Facepunch/gmad/blob/master/LICENSE
public sealed class GmaArchive : IDisposable
{
    private const string Magic = "GMAD";
    private const int MaxStringLength = 1 << 20;
    private const int ReadChunkSize = 64 * 1024;
    public const int MaxMemberSize = 512 * 1024 * 1024;

    private readonly FileStream _stream;
    private readonly List<GmaEntry> _entries = new();
    private readonly Dictionary<string, GmaEntry> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public GmaArchive(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FilePath = path;
        Entries = _entries.AsReadOnly();
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            Parse(cancellationToken);
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    public string FilePath { get; }
    public byte FormatVersion { get; private set; }
    public ulong SteamId { get; private set; }
    public ulong Timestamp { get; private set; }
    public IReadOnlyList<string> RequiredContent { get; private set; } = Array.Empty<string>();
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string Author { get; private set; } = string.Empty;
    public int AddonVersion { get; private set; }
    public IReadOnlyList<GmaEntry> Entries { get; }

    public bool TryGetEntry(string path, out GmaEntry entry) => _byPath.TryGetValue(NormalizePath(path), out entry!);

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path[0] is '/' or '\\' ||
            Path.IsPathRooted(path) || path.Contains(':'))
        {
            throw new SourceFormatException($"Addon path must be relative: {path}");
        }

        var segments = path.Split(['/', '\\']);
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
                segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(character => character < ' ' || "<>:\"|?*".Contains(character)))
            {
                throw new SourceFormatException($"Addon path is unsafe: {path}");
            }
        }

        return string.Join('/', segments);
    }

    public byte[] Read(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetEntry(path, out var entry))
        {
            throw new SourceFormatException($"GMA member not found: {path}");
        }

        return Read(entry, cancellationToken);
    }

    public byte[] Read(GmaEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Path is null || !_byPath.TryGetValue(entry.Path, out var ownedEntry) ||
            !ReferenceEquals(entry, ownedEntry))
        {
            throw new SourceFormatException("GMA entry does not belong to this archive");
        }

        var length = _stream.Length;
        if (entry.DataOffset < 0 || entry.DataOffset > length || entry.Size < 0 ||
            entry.Size > length - entry.DataOffset || entry.Size > MaxMemberSize)
        {
            throw new SourceFormatException($"GMA member {entry.Path} has an invalid or oversized range");
        }

        var buffer = new byte[(int)entry.Size];
        _stream.Seek(entry.DataOffset, SeekOrigin.Begin);
        ReadExact(buffer, $"member {entry.Path}", cancellationToken);
        if (entry.Crc32 != 0)
        {
            var actual = Crc32.Compute(buffer, cancellationToken);
            if (actual != entry.Crc32)
            {
                throw new SourceFormatException(
                    $"GMA member {entry.Path} CRC mismatch: table {entry.Crc32:x8}, payload {actual:x8}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return buffer;
    }

    private void Parse(CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[21];
        ReadExact(header, "GMA header", cancellationToken);
        if (Encoding.ASCII.GetString(header[..4]) != Magic)
        {
            throw new SourceFormatException("GMA signature is not GMAD");
        }

        FormatVersion = header[4];
        if (FormatVersion is < 1 or > 3)
        {
            throw new SourceFormatException($"Unsupported GMA format version {FormatVersion}");
        }

        SteamId = BinaryPrimitives.ReadUInt64LittleEndian(header[5..13]);
        Timestamp = BinaryPrimitives.ReadUInt64LittleEndian(header[13..21]);
        if (FormatVersion > 1)
        {
            var content = new List<string>();
            while (true)
            {
                var item = ReadCString("required content", cancellationToken);
                if (item.Length == 0)
                {
                    break;
                }

                content.Add(item);
            }

            RequiredContent = content;
        }

        Name = ReadCString("addon name", cancellationToken);
        Description = ReadCString("addon description", cancellationToken);
        Author = ReadCString("addon author", cancellationToken);
        AddonVersion = ReadInt32("addon version", cancellationToken);

        var pending = new List<(int Index, string Path, long Size, uint Crc)>();
        while (true)
        {
            var index = ReadInt32("file index", cancellationToken);
            if (index == 0)
            {
                break;
            }

            var path = NormalizePath(ReadCString("file path", cancellationToken));
            var size = ReadInt64("file size", cancellationToken);
            var crc = (uint)ReadInt32("file crc", cancellationToken);
            if (size < 0)
            {
                throw new SourceFormatException($"GMA member {path} has negative size {size}");
            }

            pending.Add((index, path, size, crc));
        }

        var offset = _stream.Position;
        var length = _stream.Length;
        foreach (var (index, path, size, crc) in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset > length || size > length - offset)
            {
                throw new SourceFormatException($"GMA member {path} extends beyond the archive");
            }

            var entry = new GmaEntry(index, path, size, crc, offset);
            if (!_byPath.TryAdd(path, entry))
            {
                throw new SourceFormatException($"GMA contains duplicate member path {path}");
            }

            _entries.Add(entry);
            offset += size;
        }
    }

    private void ReadExact(Span<byte> buffer, string what, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            while (!buffer.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(buffer.Length, ReadChunkSize);
                _stream.ReadExactly(buffer[..count]);
                buffer = buffer[count..];
            }
        }
        catch (EndOfStreamException exception)
        {
            throw new SourceFormatException($"GMA truncated while reading {what}", exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private int ReadInt32(string what, CancellationToken cancellationToken)
    {
        Span<byte> buffer = stackalloc byte[4];
        ReadExact(buffer, what, cancellationToken);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    private long ReadInt64(string what, CancellationToken cancellationToken)
    {
        Span<byte> buffer = stackalloc byte[8];
        ReadExact(buffer, what, cancellationToken);
        return BinaryPrimitives.ReadInt64LittleEndian(buffer);
    }

    private string ReadCString(string what, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = _stream.ReadByte();
            if (value < 0)
            {
                throw new SourceFormatException($"GMA truncated while reading {what}");
            }

            if (value == 0)
            {
                break;
            }

            bytes.Add((byte)value);
            if (bytes.Count > MaxStringLength)
            {
                throw new SourceFormatException($"GMA {what} string is unterminated");
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    public void Dispose() => _stream.Dispose();
}
