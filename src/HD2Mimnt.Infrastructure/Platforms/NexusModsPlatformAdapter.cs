using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HD2Mimnt.Infrastructure.Platforms;

/// <summary>
/// NexusMods platform adapter for Helldivers 2.
/// </summary>
public class NexusModsPlatformAdapter : BasePlatformAdapter
{
    private const string ApiBaseUrl = "https://api.nexusmods.com/v1";
    private const string GameDomain = "helldivers2";

    private string? _apiKey;
    private readonly RateLimiter _rateLimiter = new();

    /// <inheritdoc/>
    public override string Name => "NexusMods";

    /// <inheritdoc/>
    public override string Id => "nexusmods";

    /// <inheritdoc/>
    public override PlatformCapabilities Capabilities =>
        PlatformCapabilities.Search |
        PlatformCapabilities.Download |
        PlatformCapabilities.Metadata |
        PlatformCapabilities.Versions |
        PlatformCapabilities.Files |
        PlatformCapabilities.Authentication |
        PlatformCapabilities.RateLimit;

    /// <summary>
    /// Initializes a new instance of the <see cref="NexusModsPlatformAdapter"/> class.
    /// </summary>
    public NexusModsPlatformAdapter(HttpClient httpClient) : base(httpClient)
    {
    }

    /// <summary>
    /// Sets the API key for authentication.
    /// </summary>
    public void SetApiKey(string apiKey)
    {
        _apiKey = apiKey;
        HttpClient.DefaultRequestHeaders.Clear();
        HttpClient.DefaultRequestHeaders.Add("apikey", apiKey);
        HttpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Clears the API key.
    /// </summary>
    public void ClearApiKey()
    {
        _apiKey = null;
        HttpClient.DefaultRequestHeaders.Clear();
    }

    /// <summary>
    /// Gets whether the adapter is authenticated.
    /// </summary>
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_apiKey);

    /// <inheritdoc/>
    public override async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAuthenticated)
        {
            return false;
        }

        try
        {
            await _rateLimiter.WaitIfNeededAsync(cancellationToken);
            var response = await HttpClient.GetAsync($"{ApiBaseUrl}/users/validate.json", cancellationToken);
            _rateLimiter.UpdateFromHeaders(response);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public override async Task<PlatformSearchResult> SearchAsync(PlatformSearchQuery query, CancellationToken cancellationToken = default)
    {
        EnsureCapability(PlatformCapabilities.Search);
        EnsureAuthenticated();

        await _rateLimiter.WaitIfNeededAsync(cancellationToken);

        var url = $"{ApiBaseUrl}/games/{GameDomain}/mods/updated.json?period=1m";
        var response = await HttpClient.GetAsync(url, cancellationToken);
        _rateLimiter.UpdateFromHeaders(response);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        var mods = JsonSerializer.Deserialize<List<NexusModsApiMod>>(content, jsonOptions) ?? new();

        var filtered = mods
            .Where(m => string.IsNullOrWhiteSpace(query.Query) ||
                       m.Name.Contains(query.Query, StringComparison.OrdinalIgnoreCase) ||
                       m.Summary.Contains(query.Query, StringComparison.OrdinalIgnoreCase))
            .Select(m => ToPlatformModInfo(m))
            .ToList();

        // Apply sorting
        filtered = query.SortBy switch
        {
            PlatformSearchSort.Name => filtered.OrderBy(m => m.Name).ToList(),
            PlatformSearchSort.Author => filtered.OrderBy(m => m.Author).ToList(),
            PlatformSearchSort.DownloadCount => filtered.OrderByDescending(m => m.DownloadCount).ToList(),
            PlatformSearchSort.UpdatedDate => filtered.OrderByDescending(m => m.UpdatedAt).ToList(),
            _ => filtered
        };

        var skip = (query.Page - 1) * query.PageSize;
        var paged = filtered.Skip(skip).Take(query.PageSize).ToList();

        return new PlatformSearchResult
        {
            Mods = paged,
            TotalCount = filtered.Count,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }

    /// <inheritdoc/>
    public override async Task<PlatformModInfo> GetModInfoAsync(string modId, CancellationToken cancellationToken = default)
    {
        EnsureCapability(PlatformCapabilities.Metadata);
        EnsureAuthenticated();

        await _rateLimiter.WaitIfNeededAsync(cancellationToken);

        var url = $"{ApiBaseUrl}/games/{GameDomain}/mods/{modId}.json";
        var response = await HttpClient.GetAsync(url, cancellationToken);
        _rateLimiter.UpdateFromHeaders(response);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        var mod = JsonSerializer.Deserialize<NexusModsApiMod>(content, jsonOptions);

        if (mod == null)
        {
            throw new InvalidOperationException($"Mod not found: {modId}");
        }

        return ToPlatformModInfo(mod);
    }

    /// <inheritdoc/>
    public override async Task<Stream> DownloadModAsync(string modId, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureCapability(PlatformCapabilities.Download);
        EnsureAuthenticated();

        // Get files for the mod
        var files = await GetFilesAsync(modId, null, cancellationToken);
        if (files.Count == 0)
        {
            throw new InvalidOperationException($"No files found for mod: {modId}");
        }

        // Use the first file (usually the main file)
        var file = files[0];

        // Get download link
        await _rateLimiter.WaitIfNeededAsync(cancellationToken);
        var url = $"{ApiBaseUrl}/games/{GameDomain}/mods/{modId}/files/{file.Id}/download_link.json";
        var response = await HttpClient.GetAsync(url, cancellationToken);
        _rateLimiter.UpdateFromHeaders(response);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        var downloadLinks = JsonSerializer.Deserialize<List<NexusModsDownloadLink>>(content, jsonOptions) ?? new();

        if (downloadLinks.Count == 0)
        {
            throw new InvalidOperationException($"No download link available for mod: {modId}");
        }

        // Download the file
        var downloadUrl = downloadLinks[0].Uri;
        await _rateLimiter.WaitIfNeededAsync(cancellationToken);
        var downloadResponse = await HttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        _rateLimiter.UpdateFromHeaders(downloadResponse);
        downloadResponse.EnsureSuccessStatusCode();

        var totalBytes = downloadResponse.Content.Headers.ContentLength ?? 0;
        var memoryStream = new MemoryStream();

        await using var stream = await downloadResponse.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[8192];
        long downloadedBytes = 0;
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await memoryStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            downloadedBytes += bytesRead;

            progress?.Report(new DownloadProgress
            {
                TotalBytes = totalBytes,
                DownloadedBytes = downloadedBytes,
                StatusMessage = $"Downloading {file.Name}"
            });
        }

        memoryStream.Position = 0;
        return memoryStream;
    }

    /// <inheritdoc/>
    public override async Task<List<PlatformModVersion>> GetVersionsAsync(string modId, CancellationToken cancellationToken = default)
    {
        EnsureCapability(PlatformCapabilities.Versions);
        EnsureAuthenticated();

        var files = await GetFilesAsync(modId, null, cancellationToken);

        return files
            .GroupBy(f => f.Version)
            .Select(g => new PlatformModVersion
            {
                Version = g.Key,
                ReleasedAt = g.Max(f => f.UploadedAt) ?? DateTime.UtcNow,
                DownloadUrl = g.First().DownloadUrl,
                FileSize = g.Sum(f => f.Size),
                ChangeLog = string.Empty
            })
            .OrderByDescending(v => v.ReleasedAt)
            .ToList();
    }

    /// <inheritdoc/>
    public override async Task<List<PlatformModFile>> GetFilesAsync(string modId, string? version = null, CancellationToken cancellationToken = default)
    {
        EnsureCapability(PlatformCapabilities.Files);
        EnsureAuthenticated();

        await _rateLimiter.WaitIfNeededAsync(cancellationToken);

        var url = $"{ApiBaseUrl}/games/{GameDomain}/mods/{modId}/files.json";
        var response = await HttpClient.GetAsync(url, cancellationToken);
        _rateLimiter.UpdateFromHeaders(response);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        var apiFiles = JsonSerializer.Deserialize<NexusModsFilesResponse>(content, jsonOptions);

        if (apiFiles?.Files == null)
        {
            return new List<PlatformModFile>();
        }

        var files = apiFiles.Files
            .Select(f => new PlatformModFile
            {
                Id = f.FileId.ToString(),
                Name = f.FileName,
                Size = f.SizeInBytes ?? 0,
                DownloadUrl = string.Empty,
                Version = f.Version,
                UploadedAt = f.UploadedTime,
                Description = f.Description
            })
            .ToList();

        if (!string.IsNullOrWhiteSpace(version))
        {
            files = files.Where(f => f.Version == version).ToList();
        }

        return files;
    }

    private void EnsureAuthenticated()
    {
        if (!IsAuthenticated)
        {
            throw new InvalidOperationException("NexusMods platform requires API key authentication. Call SetApiKey() first.");
        }
    }

    private PlatformModInfo ToPlatformModInfo(NexusModsApiMod mod)
    {
        return new PlatformModInfo
        {
            Id = mod.ModId.ToString(),
            Name = mod.Name,
            Description = mod.Summary,
            Author = mod.Author,
            Version = mod.Version,
            UpdatedAt = mod.UpdatedTime,
            DownloadCount = mod.UniqueDownloads,
            ThumbnailUrl = mod.PictureUrl,
            DownloadUrl = string.Empty,
            Tags = new List<string>(),
            Metadata = new Dictionary<string, object>
            {
                ["nexus_mod_id"] = mod.ModId,
                ["category_id"] = mod.CategoryId,
                ["endorsement_count"] = mod.EndorsementCount
            }
        };
    }

    // API response models
    private class NexusModsApiMod
    {
        public int ModId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public DateTime? UpdatedTime { get; set; }
        public long UniqueDownloads { get; set; }
        public string PictureUrl { get; set; } = string.Empty;
        public int CategoryId { get; set; }
        public int EndorsementCount { get; set; }
    }

    private class NexusModsFilesResponse
    {
        public List<NexusModsFile> Files { get; set; } = new();
    }

    private class NexusModsFile
    {
        public int FileId { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public long? SizeInBytes { get; set; }
        public DateTime? UploadedTime { get; set; }
        public string Description { get; set; } = string.Empty;
    }

    private class NexusModsDownloadLink
    {
        public string Uri { get; set; } = string.Empty;
    }
}

/// <summary>
/// Rate limiter for API requests.
/// </summary>
public class RateLimiter
{
    private int _remainingRequests = int.MaxValue;
    private DateTime _resetTime = DateTime.UtcNow;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    /// Waits if rate limit is reached.
    /// </summary>
    public async Task WaitIfNeededAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (_remainingRequests <= 0 && DateTime.UtcNow < _resetTime)
            {
                var waitTime = _resetTime - DateTime.UtcNow;
                if (waitTime.TotalSeconds > 0)
                {
                    await Task.Delay(waitTime, cancellationToken);
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Updates rate limit information from HTTP response headers.
    /// </summary>
    public void UpdateFromHeaders(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RL-Daily-Remaining", out var remainingValues))
        {
            if (int.TryParse(remainingValues.FirstOrDefault(), out var remaining))
            {
                _remainingRequests = remaining;
            }
        }

        if (response.Headers.TryGetValues("X-RL-Daily-Reset", out var resetValues))
        {
            if (long.TryParse(resetValues.FirstOrDefault(), out var resetTimestamp))
            {
                _resetTime = DateTimeOffset.FromUnixTimeSeconds(resetTimestamp).UtcDateTime;
            }
        }
    }
}
