namespace HD2Mimnt.Core.Entities;

/// <summary>最近一次账户 API 响应的额度；null 表示服务器未提供，不能推断为零。</summary>
public sealed record NexusModsRateLimit
{
    public int? HourlyLimit { get; init; }
    public int? HourlyRemaining { get; init; }
    public DateTimeOffset? HourlyReset { get; init; }
    public int? DailyLimit { get; init; }
    public int? DailyRemaining { get; init; }
    public DateTimeOffset? DailyReset { get; init; }
    public DateTimeOffset? RetryAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}
