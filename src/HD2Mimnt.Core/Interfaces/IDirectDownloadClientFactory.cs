using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Interfaces;

public interface IDirectDownloadClientFactory
{
    IDirectDownloadClient Create(DownloadProxyOptions proxy);
}

/// <summary>One operation owns its HTTP client, redirect cookies and immutable proxy configuration.</summary>
public interface IDirectDownloadClient : IHttpDownloadService, IDisposable
{
    Task<DirectDownloadInfo> GetInfoAsync(Uri uri, CancellationToken cancellationToken = default);
}
