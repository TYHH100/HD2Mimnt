using System.Globalization;
using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Infrastructure.Analysis;

/// <summary>按整份 hashlist 更新；检查批次共用内存快照，不逐资源联网。</summary>
public sealed class CompatibilityResourceNameProvider(HttpClient client, string cacheRoot) : ICompatibilityResourceNames
{
    public const string Source = "https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition/tree/main/hashlists";
    private const string RawRoot = "https://raw.githubusercontent.com/Boxofbiscuits97/HD2SDK-CommunityEdition/main/hashlists/";
    private const int MaxDocumentBytes = 8 * 1024 * 1024;
    private readonly string _cachePath = Path.Combine(cacheRoot, "compatibility", "resource-names.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CompatibilityResourceNames? _snapshot;
    private DateTime _retryAfterUtc;
    private bool _loaded;

    private sealed record CacheDocument(DateTime UpdatedAtUtc, string FriendlyNames, string ArchiveHashes);

    public async Task<CompatibilityResourceNames> GetAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded)
            {
                if (File.Exists(_cachePath))
                    try
                    {
                        if (new FileInfo(_cachePath).Length > 2 * MaxDocumentBytes) throw new InvalidDataException("名称缓存过大。");
                        var cached = JsonSerializer.Deserialize<CacheDocument>(await File.ReadAllTextAsync(_cachePath, cancellationToken).ConfigureAwait(false));
                        if (cached is not null) _snapshot = Parse(cached);
                    }
                    catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { }
                _loaded = true;
            }
            if (!refresh && (DateTime.UtcNow < _retryAfterUtc || _snapshot?.UpdatedAtUtc > DateTime.UtcNow.AddDays(-1)))
                return _snapshot ?? new() { Source = Source };

            string? temporary = null;
            try
            {
                var friendly = await DownloadAsync("friendlynames.txt", cancellationToken).ConfigureAwait(false);
                var archives = await DownloadAsync("archivehashes.json", cancellationToken).ConfigureAwait(false);
                var document = new CacheDocument(DateTime.UtcNow, friendly, archives);
                var snapshot = Parse(document);
                Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                temporary = _cachePath + $".{Guid.NewGuid():N}.tmp";
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, _cachePath, true);
                _snapshot = snapshot;
                _retryAfterUtc = default;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException or UnauthorizedAccessException
                || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _retryAfterUtc = DateTime.UtcNow.AddMinutes(5);
                _snapshot = new() { Resources = _snapshot?.Resources ?? new Dictionary<ulong, string>(),
                    Archives = _snapshot?.Archives ?? new Dictionary<ulong, string>(), UpdatedAtUtc = _snapshot?.UpdatedAtUtc,
                    Source = Source, UpdateError = ex.Message };
            }
            finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            return _snapshot!;
        }
        finally { _gate.Release(); }
    }

    private async Task<string> DownloadAsync(string file, CancellationToken token)
    {
        using var response = await client.GetAsync(RawRoot + file, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxDocumentBytes) throw new InvalidDataException("在线名称表过大。");
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int count;
        while ((count = await source.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaxDocumentBytes) throw new InvalidDataException("在线名称表过大。");
            buffer.Write(chunk, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static CompatibilityResourceNames Parse(CacheDocument document)
    {
        var resources = new Dictionary<ulong, string>();
        using var reader = new StringReader(document.FriendlyNames);
        while (reader.ReadLine() is { } line)
        {
            var span = line.AsSpan().Trim().TrimStart('\ufeff');
            if (span.IsEmpty || span[0] == '#') continue;
            var separator = span.IndexOfAny(' ', '\t');
            if (separator < 1 || !ulong.TryParse(span[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || span[(separator + 1)..].Trim().IsEmpty) throw new InvalidDataException("资源名称表格式无效。");
            resources[id] = span[(separator + 1)..].Trim().ToString();
        }
        var archives = new Dictionary<ulong, string>();
        using var json = JsonDocument.Parse(document.ArchiveHashes);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("归档名称表格式无效。");
        foreach (var category in json.RootElement.EnumerateObject())
        {
            if (category.Value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("归档名称分类格式无效。");
            foreach (var entry in category.Value.EnumerateObject())
            {
                // 上游包含省略前导零的 15 位哈希，按数值读取，再由展示层统一格式化。
                if (entry.Name.Length is < 1 or > 16 || !ulong.TryParse(entry.Name, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)
                    || entry.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.Value.GetString()))
                    throw new InvalidDataException("归档名称表格式无效。");
                archives[id] = entry.Value.GetString()!;
            }
        }
        if (resources.Count == 0 || archives.Count == 0) throw new InvalidDataException("在线名称表为空。");
        return new() { Resources = resources, Archives = archives, UpdatedAtUtc = document.UpdatedAtUtc, Source = Source };
    }
}
