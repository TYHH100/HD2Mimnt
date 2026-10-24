using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Downloads;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HD2Mimnt.Infrastructure.Platforms;

/// <summary>
/// GitHub Releases platform adapter for downloading mods from GitHub.
/// </summary>
public class GitHubPlatformAdapter : BasePlatformAdapter
{
    private const string ApiBaseUrl = "https://api.github.com";
    private const string SearchQuery = "helldivers 2 mod";
    private readonly IHttpDownloadService _downloader;

    /// <inheritdoc/>
    public override string Name => "GitHub";

    /// <inheritdoc/>
    public override string Id => "github";

    /// <inheritdoc/>
    public override PlatformCapabilities Capabilities =>
        PlatformCapabilities.Search |
        PlatformCapabilities.Download |
        PlatformCapabilities.Metadata |
        PlatformCapabilities.Versions |
        PlatformCapabilities.Files;

    /// <summary>
    /// Initializes a new instance of the <see cref="GitHubPlatformAdapter"/> class.
    /// </summary>
    public GitHubPlatformAdapter(HttpClient httpClient, IHttpDownloadService? downloader = null) : base(httpClient)
    {
        _downloader = downloader ?? new HttpDownloadService(httpClient);
        // GitHub API requires User-Agent header
        if (!HttpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            HttpClient.DefaultRequestHeaders.Add("User-Agent", "HD2Mimnt-ModManager");
        }
        HttpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <inheritdoc/>
    public override async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await HttpClient.GetAsync($"{ApiBaseUrl}/zen", cancellationToken);
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

        var searchTerm = string.IsNullOrWhiteSpace(query.Query) ? SearchQuery : $"{SearchQuery} {query.Query}";
        var encodedQuery = Uri.EscapeDataString(searchTerm);
        var url = $"{ApiBaseUrl}/search/repositories?q={encodedQuery}&sort=stars&order=desc&per_page={query.PageSize}&page={query.Page}";

        var response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var searchResult = JsonSerializer.Deserialize<GitHubSearchResponse>(content, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

        if (searchResult == null)
        {
            return new PlatformSearchResult
            {
                Mods = new List<PlatformModInfo>(),
                TotalCount = 0,
                Page = query.Page,
                PageSize = query.PageSize
            };
        }

        var mods = new List<PlatformModInfo>();
        foreach (var repo in searchResult.Items)
        {
            // Get the latest release for each repository
            try
            {
                var modInfo = await GetModInfoFromRepo(repo, cancellationToken);
                if (modInfo != null)
                {
                    mods.Add(modInfo);
                }
            }
            catch
            {
                // Skip repositories without valid releases
                continue;
            }
        }

        // Apply additional sorting if needed
        mods = query.SortBy switch
        {
            PlatformSearchSort.Name => mods.OrderBy(m => m.Name).ToList(),
            PlatformSearchSort.UpdatedDate => mods.OrderByDescending(m => m.UpdatedAt).ToList(),
            PlatformSearchSort.DownloadCount => mods.OrderByDescending(m => m.DownloadCount).ToList(),
            _ => mods
        };

        return new PlatformSearchResult
        {
            Mods = mods,
            TotalCount = searchResult.TotalCount,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }

    /// <inheritdoc/>
    public override async Task<PlatformModInfo> GetModInfoAsync(string modId, CancellationToken cancellationToken = default)
    {
        EnsureCapability(PlatformCapabilities.Metadata);

        // ModId format: "owner/repo" or "owner/repo/tag"
        var parts = modId.Split('/');
        if (parts.Length < 2)
        {
            throw new ArgumentException("Invalid GitHub modId format. Expected: owner/repo or owner/repo/tag", nameof(modId));
        }

        var owner = parts[0];
        var repo = parts[1];
        var tag = parts.Length > 2 ? parts[2] : null;

        // Get repository info
        var repoUrl = $"{ApiBaseUrl}/repos/{owner}/{repo}";
        var repoResponse = await HttpClient.GetAsync(repoUrl, cancellationToken);
        repoResponse.EnsureSuccessStatusCode();

        var repoContent = await repoResponse.Content.ReadAsStringAsync(cancellationToken);
        var repoInfo = JsonSerializer.Deserialize<GitHubRepository>(repoContent, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

        if (repoInfo == null)
        {
            throw new InvalidOperationException($"Repository not found: {modId}");
        }

        // Get release info
        var releaseUrl = string.IsNullOrWhiteSpace(tag)
            ? $"{ApiBaseUrl}/repos/{owner}/{repo}/releases/latest"
            : $"{ApiBaseUrl}/repos/{owner}/{repo}/releases/tags/{tag}";

        var releaseResponse = await HttpClient.GetAsync(releaseUrl, cancellationToken);
        releaseResponse.EnsureSuccessStatusCode();

        var releaseContent = await releaseResponse.Content.ReadAsStringAsync(cancellationToken);
        var release = JsonSerializer.Deserialize<GitHubRelease>(releaseContent, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

        if (release == null)
        {
            throw new InvalidOperationException($"No release found for: {modId}");
        }

        return new PlatformModInfo
        {
            Id = $"{owner}/{repo}",
            Name = repoInfo.Name,
            Description = repoInfo.Description ?? string.Empty,
            Author = owner,
            Version = release.TagName,
            UpdatedAt = release.PublishedAt,
            DownloadCount = release.Assets.Sum(a => a.DownloadCount),
            ThumbnailUrl = repoInfo.Owner.AvatarUrl,
            DownloadUrl = release.Assets.FirstOrDefault()?.BrowserDownloadUrl ?? string.Empty,
            Tags = new List<string>(),
            Metadata = new Dictionary<string, object>
            {
                ["github_owner"] = owner,
                ["github_repo"] = repo,
                ["github_stars"] = repoInfo.StargazersCount,
                ["release_tag"] = release.TagName,
                ["release_id"] = release.Id
            }
        };
    }

    /// <inheritdoc/>
    public override async Task<Stream> DownloadModAsync(string modId, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureCapability(PlatformCapabilities.Download);

        var files = await GetFilesAsync(modId, null, cancellationToken);
        if (files.Count == 0)
        {
            throw new InvalidOperationException($"No release assets found for: {modId}");
        }

        // Use the first asset (usually the main file)
        var file = files[0];

        return await _downloader.DownloadAsync(new Uri(file.DownloadUrl), progress: progress,
            cancellationToken: cancellationToken, resumeKey: $"github:{modId}:asset:{file.Id}");
    }

    /// <inheritdoc/>
    public override async Task<List<PlatformModVersion>> GetVersionsAsync(string modId, CancellationToken cancellationToken = default)
    {
        EnsureCapability(PlatformCapabilities.Versions);

        var parts = modId.Split('/');
        if (parts.Length < 2)
        {
            throw new ArgumentException("Invalid GitHub modId format. Expected: owner/repo", nameof(modId));
        }

        var owner = parts[0];
        var repo = parts[1];

        var url = $"{ApiBaseUrl}/repos/{owner}/{repo}/releases?per_page=20";
        var response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(content, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) ?? new();

        return releases
            .Select(r => new PlatformModVersion
            {
                Version = r.TagName,
                ReleasedAt = r.PublishedAt ?? DateTime.UtcNow,
                DownloadUrl = r.Assets.FirstOrDefault()?.BrowserDownloadUrl ?? string.Empty,
                FileSize = r.Assets.Sum(a => a.Size),
                ChangeLog = r.Body
            })
            .ToList();
    }

    /// <inheritdoc/>
    public override async Task<List<PlatformModFile>> GetFilesAsync(string modId, string? version = null, CancellationToken cancellationToken = default)
    {
        EnsureCapability(PlatformCapabilities.Files);

        var parts = modId.Split('/');
        if (parts.Length < 2)
        {
            throw new ArgumentException("Invalid GitHub modId format. Expected: owner/repo", nameof(modId));
        }

        var owner = parts[0];
        var repo = parts[1];

        var releaseUrl = string.IsNullOrWhiteSpace(version)
            ? $"{ApiBaseUrl}/repos/{owner}/{repo}/releases/latest"
            : $"{ApiBaseUrl}/repos/{owner}/{repo}/releases/tags/{version}";

        var response = await HttpClient.GetAsync(releaseUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var release = JsonSerializer.Deserialize<GitHubRelease>(content, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

        if (release == null)
        {
            return new List<PlatformModFile>();
        }

        return release.Assets
            .Select(a => new PlatformModFile
            {
                Id = a.Id.ToString(),
                Name = a.Name,
                Size = a.Size,
                DownloadUrl = a.BrowserDownloadUrl,
                Version = release.TagName,
                UploadedAt = a.UpdatedAt,
                Description = a.Label ?? string.Empty
            })
            .ToList();
    }

    private async Task<PlatformModInfo?> GetModInfoFromRepo(GitHubRepository repo, CancellationToken cancellationToken)
    {
        try
        {
            var releaseUrl = $"{ApiBaseUrl}/repos/{repo.FullName}/releases/latest";
            var response = await HttpClient.GetAsync(releaseUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var release = JsonSerializer.Deserialize<GitHubRelease>(content, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

            if (release == null)
            {
                return null;
            }

            return new PlatformModInfo
            {
                Id = repo.FullName,
                Name = repo.Name,
                Description = repo.Description ?? string.Empty,
                Author = repo.Owner.Login,
                Version = release.TagName,
                UpdatedAt = release.PublishedAt,
                DownloadCount = release.Assets.Sum(a => a.DownloadCount),
                ThumbnailUrl = repo.Owner.AvatarUrl,
                DownloadUrl = release.Assets.FirstOrDefault()?.BrowserDownloadUrl ?? string.Empty,
                Tags = new List<string>(),
                Metadata = new Dictionary<string, object>
                {
                    ["github_stars"] = repo.StargazersCount,
                    ["release_tag"] = release.TagName
                }
            };
        }
        catch
        {
            return null;
        }
    }

    // API response models
    private class GitHubSearchResponse
    {
        public int TotalCount { get; set; }
        public List<GitHubRepository> Items { get; set; } = new();
    }

    private class GitHubRepository
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public GitHubUser Owner { get; set; } = new();
        public int StargazersCount { get; set; }
    }

    private class GitHubUser
    {
        public string Login { get; set; } = string.Empty;
        public string AvatarUrl { get; set; } = string.Empty;
    }

    private class GitHubRelease
    {
        public long Id { get; set; }
        public string TagName { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public DateTime? PublishedAt { get; set; }
        public List<GitHubAsset> Assets { get; set; } = new();
    }

    private class GitHubAsset
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public long Size { get; set; }
        public long DownloadCount { get; set; }
        public string BrowserDownloadUrl { get; set; } = string.Empty;
        public DateTime? UpdatedAt { get; set; }
        public string? Label { get; set; }
    }
}
