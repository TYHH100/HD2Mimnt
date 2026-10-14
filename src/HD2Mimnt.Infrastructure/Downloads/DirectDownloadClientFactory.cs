using System.Net;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Infrastructure.Downloads;

public sealed class DirectDownloadClientFactory(IDownloadOptionsProvider optionsProvider,
    string resumeDirectory, string tempDirectory) : IDirectDownloadClientFactory
{
    public IDirectDownloadClient Create(DownloadProxyOptions proxy)
    {
        var address = proxy.Validate();
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true, MaxAutomaticRedirections = 20,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            UseProxy = proxy.Mode != DownloadProxyMode.Direct
        };
        if (address != null)
            handler.Proxy = new WebProxy(address)
            {
                Credentials = string.IsNullOrEmpty(proxy.UserName) ? null : new NetworkCredential(proxy.UserName, proxy.Password)
            };
        var client = new HttpClient(new DirectDownloadResponseGuard(handler)) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HD2Mimnt-ModManager");
        return new Client(client, new HttpDownloadService(client, optionsProvider, resumeDirectory, tempDirectory));
    }

    private sealed class Client(HttpClient http, HttpDownloadService downloader) : IDirectDownloadClient
    {
        public async Task<DirectDownloadInfo> GetInfoAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(1));
            // GET headers works for endpoints that reject HEAD. Dispose without buffering the body.
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            var disposition = response.Content.Headers.ContentDisposition;
            var name = disposition?.FileNameStar ?? disposition?.FileName;
            if (string.IsNullOrWhiteSpace(name))
                name = Uri.UnescapeDataString((response.RequestMessage?.RequestUri ?? uri).AbsolutePath.Split('/').Last());
            return new(name?.Trim('"') ?? "", response.Content.Headers.ContentLength);
        }

        public Task<Stream> DownloadAsync(Uri uri, DownloadOptions? options = null, IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default, string? resumeKey = null)
            => downloader.DownloadAsync(uri, options, progress, cancellationToken, resumeKey);

        public Task DownloadToFileAsync(Uri uri, string targetPath, DownloadOptions? options = null,
            IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default, string? resumeKey = null)
            => downloader.DownloadToFileAsync(uri, targetPath, options, progress, cancellationToken, resumeKey);

        public void Dispose() => http.Dispose();
    }
}
