using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Platforms.NexusMods.Models;
using System.Text.Json;
using static HD2Mimnt.Infrastructure.Platforms.NexusMods.NexusModsFileSelector;

namespace HD2Mimnt.Infrastructure.Platforms.NexusMods;

/// <summary>Resolves mod/file/version IDs and performs the selected archive download.</summary>
internal sealed class NexusModsFileService
{
    private readonly NexusModsApiClient _api;
    private readonly IHttpDownloadService _downloader;

    public NexusModsFileService(NexusModsApiClient api, IHttpDownloadService downloader)
    {
        _api = api;
        _downloader = downloader;
    }

    public async Task<List<FileVersion>> GetFileVersionsAsync(string modId, CancellationToken cancellationToken)
    {
        var mod = await _api.GetModAsync(modId, cancellationToken);
        var files = await _api.GetModFilesAsync(mod.Id, cancellationToken);
        var result = new List<FileVersion>();
        foreach (var file in files.ModFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var versions = await _api.GetFileVersionsAsync(file.Id, cancellationToken);
            result.AddRange(versions.Versions);
        }
        return result;
    }

    public async Task<Stream> DownloadModAsync(string modId, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        _api.EnsureAuthenticated();
        var versions = await GetFileVersionsAsync(modId, cancellationToken);
        var file = SelectDownloadVersion(versions)
            ?? throw new InvalidOperationException($"模组 {modId} 没有可下载的活动文件版本。");

        var link = await _api.GetDownloadLinkAsync(file.Id, cancellationToken);
        if (!Uri.TryCreate(link.DownloadUrl, UriKind.Absolute, out var downloadUri) || downloadUri.Scheme != Uri.UriSchemeHttps)
            throw new JsonException("Nexus Mods 返回的下载链接不是有效的 HTTPS 地址。");

        // Signed archive URLs require no Nexus credentials; authentication belongs only on API requests.
        return await _downloader.DownloadAsync(downloadUri, progress: progress, cancellationToken: cancellationToken,
            resumeKey: $"nexusmods:version:{file.Id}");
    }
}
