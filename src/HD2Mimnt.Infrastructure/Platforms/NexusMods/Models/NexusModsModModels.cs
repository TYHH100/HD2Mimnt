namespace HD2Mimnt.Infrastructure.Platforms.NexusMods.Models;

internal sealed class TrendingModsResponse { public required List<TrendingMod> Mods { get; init; } }
internal sealed class TrendingMod
{
    public required string Name { get; init; }
    public required string ModPageUrl { get; init; }
    public string? Author { get; init; }
    public string? Summary { get; init; }
    public string? PictureUrl { get; init; }
}
internal sealed class NexusMod
{
    public required string Id { get; init; }
    public required string GameScopedId { get; init; }
    public required string GameId { get; init; }
    public string? Name { get; init; }
}
