using System.Net;

namespace HD2Mimnt.Infrastructure.Platforms;

/// <summary>Honours server Retry-After without assuming undocumented v1 quota headers.</summary>
public class RateLimiter
{
    private long _retryAtUtcTicks;

    public async Task WaitIfNeededAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var wait = new DateTime(Interlocked.Read(ref _retryAtUtcTicks), DateTimeKind.Utc) - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, cancellationToken);
    }

    public void UpdateFromHeaders(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.TooManyRequests) return;
        var retry = response.Headers.RetryAfter;
        var retryAt = retry?.Date?.UtcDateTime ??
            (retry?.Delta is { } delta ? DateTime.UtcNow.Add(delta) : DateTime.UtcNow.AddSeconds(1));
        Interlocked.Exchange(ref _retryAtUtcTicks, retryAt.Ticks);
    }
}
