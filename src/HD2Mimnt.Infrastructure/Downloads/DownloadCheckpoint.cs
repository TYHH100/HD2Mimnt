using System.Text.Json;
using System.Text;

namespace HD2Mimnt.Infrastructure.Downloads;

/// <summary>Offsets describe only bytes already written. Metadata is published after flushing data.</summary>
internal sealed class DownloadCheckpoint
{
    public int Version { get; set; } = 1;
    public string Identity { get; set; } = string.Empty;
    public long Length { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public List<DownloadSegment> Segments { get; set; } = [];

    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly object _gate = new();
    private long _unsavedBytes;

    public bool Matches(string identity, long length, string? etag, DateTimeOffset? modified)
    {
        if (Version != 1 || Identity != identity || Length != length || length <= 0 ||
            (etag is not null ? ETag != etag : ETag is not null || LastModified != modified) ||
            (etag is null && modified is null) || Segments is null || Segments.Count is < 1 or > 16)
            return false;
        long next = 0;
        foreach (var segment in Segments)
        {
            if (segment is null || segment.Start != next || segment.End < segment.Start ||
                segment.End >= length || segment.Downloaded < 0 || segment.Downloaded > segment.End - segment.Start + 1)
                return false;
            next = segment.End + 1;
        }
        return next == length;
    }

    public bool Advance(DownloadSegment segment, long offset)
    {
        lock (_gate)
        {
            var downloaded = offset - segment.Start;
            _unsavedBytes += downloaded - segment.Downloaded;
            segment.Downloaded = downloaded;
            if (_unsavedBytes < 1024 * 1024) return false;
            _unsavedBytes = 0;
            return true;
        }
    }

    public async Task SaveAsync(FileStream file, string metadataPath)
    {
        await _saveGate.WaitAsync();
        var temporary = metadataPath + ".tmp";
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(this);
            // Snapshot first, then flush: every offset in the snapshot refers to durable bytes.
            RandomAccess.FlushToDisk(file.SafeFileHandle);
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await output.WriteAsync(Encoding.UTF8.GetBytes(json));
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, metadataPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
            _saveGate.Release();
        }
    }
}

internal sealed class DownloadSegment
{
    public long Start { get; set; }
    public long End { get; set; }
    public long Downloaded { get; set; }
}
