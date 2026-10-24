using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Infrastructure.Platforms;

/// <summary>
/// Mock platform adapter for testing purposes.
/// </summary>
public class MockPlatformAdapter : BasePlatformAdapter
{
    private readonly List<PlatformModInfo> _mockMods = new();

    /// <inheritdoc/>
    public override string Name => "Mock Platform";

    /// <inheritdoc/>
    public override string Id => "mock";

    /// <inheritdoc/>
    public override PlatformCapabilities Capabilities =>
        PlatformCapabilities.Search |
        PlatformCapabilities.Download |
        PlatformCapabilities.Metadata |
        PlatformCapabilities.Versions |
        PlatformCapabilities.Files;

    /// <summary>
    /// Initializes a new instance of the <see cref="MockPlatformAdapter"/> class.
    /// </summary>
    public MockPlatformAdapter(HttpClient httpClient) : base(httpClient)
    {
        InitializeMockData();
    }

    /// <summary>
    /// Adds a mock Mod to the platform.
    /// </summary>
    public void AddMockMod(PlatformModInfo modInfo)
    {
        _mockMods.Add(modInfo);
    }

    /// <inheritdoc/>
    public override Task<PlatformSearchResult> SearchAsync(PlatformSearchQuery query, CancellationToken cancellationToken = default)
    {
        EnsureCapability(PlatformCapabilities.Search);

        var filtered = _mockMods
            .Where(m => string.IsNullOrWhiteSpace(query.Query) ||
                       m.Name.Contains(query.Query, StringComparison.OrdinalIgnoreCase) ||
                       m.Description.Contains(query.Query, StringComparison.OrdinalIgnoreCase))
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

        return Task.FromResult(new PlatformSearchResult
        {
            Mods = paged,
            TotalCount = filtered.Count,
            Page = query.Page,
            PageSize = query.PageSize
        });
    }

    /// <inheritdoc/>
    public override Task<PlatformModInfo> GetModInfoAsync(string modId, CancellationToken cancellationToken = default)
    {
        EnsureCapability(PlatformCapabilities.Metadata);

        var mod = _mockMods.FirstOrDefault(m => m.Id == modId);
        if (mod == null)
        {
            throw new InvalidOperationException($"Mod not found: {modId}");
        }

        return Task.FromResult(mod);
    }

    /// <inheritdoc/>
    public override Task<Stream> DownloadModAsync(string modId, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureCapability(PlatformCapabilities.Download);

        var mod = _mockMods.FirstOrDefault(m => m.Id == modId);
        if (mod == null)
        {
            throw new InvalidOperationException($"Mod not found: {modId}");
        }

        // Create a mock stream with some data
        var mockData = System.Text.Encoding.UTF8.GetBytes($"Mock data for mod {modId}");
        var stream = new MemoryStream(mockData);

        // Report progress
        progress?.Report(new DownloadProgress
        {
            TotalBytes = mockData.Length,
            DownloadedBytes = mockData.Length,
            StatusMessage = "Download complete"
        });

        return Task.FromResult<Stream>(stream);
    }

    /// <inheritdoc/>
    public override Task<List<PlatformModVersion>> GetVersionsAsync(string modId, CancellationToken cancellationToken = default)
    {
        EnsureCapability(PlatformCapabilities.Versions);

        var mod = _mockMods.FirstOrDefault(m => m.Id == modId);
        if (mod == null)
        {
            throw new InvalidOperationException($"Mod not found: {modId}");
        }

        var versions = new List<PlatformModVersion>
        {
            new PlatformModVersion
            {
                Version = mod.Version,
                ReleasedAt = mod.UpdatedAt ?? DateTime.UtcNow,
                DownloadUrl = mod.DownloadUrl,
                FileSize = 1024 * 1024,
                ChangeLog = "Latest version"
            }
        };

        return Task.FromResult(versions);
    }

    /// <inheritdoc/>
    public override Task<List<PlatformModFile>> GetFilesAsync(string modId, string? version = null, CancellationToken cancellationToken = default)
    {
        EnsureCapability(PlatformCapabilities.Files);

        var mod = _mockMods.FirstOrDefault(m => m.Id == modId);
        if (mod == null)
        {
            throw new InvalidOperationException($"Mod not found: {modId}");
        }

        var files = new List<PlatformModFile>
        {
            new PlatformModFile
            {
                Id = $"{modId}_file1",
                Name = $"{mod.Name}.zip",
                Size = 1024 * 1024,
                DownloadUrl = mod.DownloadUrl,
                Version = mod.Version,
                UploadedAt = mod.UpdatedAt,
                Description = "Main mod file"
            }
        };

        return Task.FromResult(files);
    }

    private void InitializeMockData()
    {
        _mockMods.Add(new PlatformModInfo
        {
            Id = "mod1",
            Name = "Test Armor Mod",
            Description = "A test armor modification",
            Author = "TestAuthor",
            Version = "1.0.0",
            UpdatedAt = DateTime.UtcNow.AddDays(-1),
            DownloadCount = 1000,
            ThumbnailUrl = "https://example.com/thumb1.jpg",
            DownloadUrl = "https://example.com/download/mod1.zip",
            Tags = new List<string> { "armor", "cosmetic" }
        });

        _mockMods.Add(new PlatformModInfo
        {
            Id = "mod2",
            Name = "Test Weapon Mod",
            Description = "A test weapon modification",
            Author = "TestAuthor",
            Version = "2.0.0",
            UpdatedAt = DateTime.UtcNow.AddDays(-5),
            DownloadCount = 500,
            ThumbnailUrl = "https://example.com/thumb2.jpg",
            DownloadUrl = "https://example.com/download/mod2.zip",
            Tags = new List<string> { "weapon", "balance" }
        });
    }
}
