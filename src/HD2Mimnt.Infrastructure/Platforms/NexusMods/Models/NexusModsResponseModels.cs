namespace HD2Mimnt.Infrastructure.Platforms.NexusMods.Models;

internal sealed class Envelope<T> { public required T Data { get; init; } }
internal sealed class DownloadResponse { public required string DownloadUrl { get; init; } }
internal sealed class ProblemDetails
{
    public string? Title { get; init; }
    public string? Detail { get; init; }
}
