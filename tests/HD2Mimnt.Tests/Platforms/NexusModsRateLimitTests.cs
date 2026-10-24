using System.Net;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Platforms;

namespace HD2Mimnt.Tests.Platforms;

public sealed class NexusModsRateLimitTests
{
    [Fact]
    public async Task AccountResponse_ShouldExposeDocumentedQuotaHeadersAndClearWhenAccountChanges()
    {
        using var handler = new QuotaHandler();
        using var http = new HttpClient(handler);
        var platform = new NexusModsPlatformAdapter(http);
        Assert.Null(platform.RateLimit.DailyRemaining);
        platform.SetApiKey("synthetic-key");
        var changes = 0;
        platform.RateLimitChanged += (_, _) => changes++;
        await platform.RefreshRateLimitAsync();
        var quota = platform.RateLimit;
        Assert.Equal(100, quota.HourlyLimit);
        Assert.Equal(96, quota.HourlyRemaining);
        Assert.Equal(2500, quota.DailyLimit);
        Assert.Equal(2488, quota.DailyRemaining);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero), quota.HourlyReset);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero), quota.DailyReset);
        Assert.NotNull(quota.UpdatedAt);
        Assert.Null(quota.RetryAt);
        Assert.Equal(1, changes);
        platform.SetApiKey("another-key");
        Assert.Null(platform.RateLimit.UpdatedAt);
        Assert.Null(platform.RateLimit.DailyRemaining);
        await platform.RefreshRateLimitAsync();
        platform.ClearAuthentication();
        Assert.Null(platform.RateLimit.DailyRemaining);
    }

    [Fact]
    public async Task ThrottledResponse_ShouldExposeZeroAndRetryAfterBeforeThrowing()
    {
        using var handler = new QuotaHandler { Status = HttpStatusCode.TooManyRequests };
        using var http = new HttpClient(handler);
        var platform = new NexusModsPlatformAdapter(http);
        platform.SetApiKey("synthetic-key");
        await Assert.ThrowsAsync<PlatformRequestException>(() => platform.RefreshRateLimitAsync());
        Assert.Equal(0, platform.RateLimit.DailyRemaining);
        Assert.NotNull(platform.RateLimit.RetryAt);
        Assert.True(platform.IsAuthenticated);
    }

    [Fact]
    public async Task MissingOrInvalidHeaders_ShouldRemainUnknownAndNotInventQuota()
    {
        using var handler = new QuotaHandler { Invalid = true };
        using var http = new HttpClient(handler);
        var platform = new NexusModsPlatformAdapter(http);
        platform.SetApiKey("synthetic-key");
        await platform.RefreshRateLimitAsync();
        Assert.Equal(new NexusModsRateLimit(), platform.RateLimit);
    }

    [Fact]
    public async Task ResponseAfterLogout_ShouldNotRestorePreviousAccountsQuota()
    {
        using var handler = new QuotaHandler { Pending = true };
        using var http = new HttpClient(handler);
        var platform = new NexusModsPlatformAdapter(http);
        platform.SetApiKey("synthetic-key");
        var task = platform.RefreshRateLimitAsync();
        platform.ClearAuthentication();
        handler.Release.SetResult();
        await task;
        Assert.Null(platform.RateLimit.DailyRemaining);
    }

    private sealed class QuotaHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public bool Invalid { get; init; }
        public bool Pending { get; init; }
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("https://api.nexusmods.com/v1/users/validate.json", request.RequestUri!.AbsoluteUri);
            if (Pending) await Release.Task.WaitAsync(token);
            var response = new HttpResponseMessage(Status) { Content = new StringContent("{\"user_id\":123}") };
            if (Invalid)
            {
                response.Headers.TryAddWithoutValidation("X-RL-Hourly-Limit", "-1");
                response.Headers.TryAddWithoutValidation("X-RL-Daily-Remaining", "garbage");
                response.Headers.TryAddWithoutValidation("X-RL-Daily-Reset", "garbage");
                return response;
            }
            response.Headers.Add("X-RL-Hourly-Limit", "100");
            response.Headers.Add("X-RL-Hourly-Remaining", "96");
            response.Headers.Add("X-RL-Hourly-Reset", "2026-10-14T12:00:00+00:00");
            response.Headers.Add("X-RL-Daily-Limit", "2500");
            response.Headers.Add("X-RL-Daily-Remaining", Status == HttpStatusCode.TooManyRequests ? "0" : "2488");
            response.Headers.Add("X-RL-Daily-Reset", "2026-10-15 00:00:00 +0000");
            if (Status == HttpStatusCode.TooManyRequests)
                response.Headers.RetryAfter = new(TimeSpan.FromSeconds(30));
            return response;
        }
    }
}
