namespace HD2Mimnt.Infrastructure.Platforms.NexusMods.Models;

internal sealed class ModFilesResponse { public required List<ModFile> ModFiles { get; init; } }
internal sealed class ModFile { public required string Id { get; init; } }
internal sealed class FileVersionsResponse { public required List<FileVersion> Versions { get; init; } }
internal sealed class FileVersion
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Category { get; init; }
    public required DateTime UploadedAt { get; init; }
    public bool IsPrimary { get; init; }
}
