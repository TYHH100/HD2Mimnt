using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Infrastructure.Platforms.NexusMods;

internal static class NexusModsResponseLog
{
    public static Task WriteAsync(ILogger logger, HttpRequestMessage request, HttpResponseMessage response,
        string operation, IEnumerable<string?> requestSecrets, CancellationToken cancellationToken) =>
        PlatformResponseLog.WriteAsync(logger, request, response, "Nexus", operation, requestSecrets, cancellationToken);
}
