using System.Net;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Infrastructure.Platforms.AyakaMods;

/// <summary>Reauthorize each API request; redirects and Range retries never forward the key to storage hosts.</summary>
internal sealed class AyakaModsDownloadHandler(Func<string?> apiKey, HttpMessageHandler transport, ILogger? logger = null)
    : DelegatingHandler(transport)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri!;
        for (var redirects = 0; redirects <= 20; redirects++)
        {
            if (uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidDataException("A 网下载返回了无效的 HTTPS 地址。");
            using var current = new HttpRequestMessage(request.Method, uri);
            foreach (var header in request.Headers)
                if (!header.Key.Equals("XF-Api-Key", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                    current.Headers.TryAddWithoutValidation(header.Key, header.Value);
            var isApiDownload = uri.GetLeftPart(UriPartial.Authority) == AyakaModsApiClient.BaseUri.GetLeftPart(UriPartial.Authority) &&
                uri.AbsolutePath.StartsWith("/api/mod-versions/", StringComparison.Ordinal) &&
                uri.AbsolutePath.EndsWith("/download", StringComparison.Ordinal);
            var key = apiKey();
            if (isApiDownload)
            {
                if (key == null) throw new InvalidOperationException("A 网密钥已清除，请重新应用后下载。");
                current.Headers.Add("XF-Api-Key", key);
            }
            var response = await base.SendAsync(current, token);
            if (logger?.IsEnabled(LogLevel.Debug) == true)
            {
                var media = response.Content.Headers.ContentType?.MediaType;
                var isText = media?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true ||
                    media == "application/json" || media?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true;
                var isRedirect = (int)response.StatusCode is >= 300 and < 400;
                var isAttachment = response.Content.Headers.ContentDisposition?.DispositionType == "attachment";
                try
                {
                    await PlatformResponseLog.WriteAsync(logger, current, response, "Ayaka", "下载所选文件",
                        [key], token, includeBody: isText && !isRedirect && !isAttachment, preserveApiErrorCodes: true);
                }
                catch { response.Dispose(); throw; }
            }
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location == null) throw new InvalidDataException("A 网下载跳转缺少目标地址。");
            uri = new Uri(uri, location);
        }
        throw new InvalidDataException("A 网下载跳转次数过多。");
    }
}
