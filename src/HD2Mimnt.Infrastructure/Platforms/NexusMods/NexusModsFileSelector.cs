using HD2Mimnt.Infrastructure.Platforms.NexusMods.Models;

namespace HD2Mimnt.Infrastructure.Platforms.NexusMods;

/// <summary>Shares the default-file policy between downloading and update version ordering.</summary>
internal static class NexusModsFileSelector
{
    public static bool IsDownloadable(FileVersion file) => file.Category is "main" or "update" or "optional" or "miscellaneous";

    public static FileVersion? SelectDownloadVersion(IEnumerable<FileVersion> files) => files.Where(IsDownloadable)
        .OrderByDescending(f => f.IsPrimary)
        .ThenByDescending(f => f.Category == "main")
        .ThenByDescending(f => f.UploadedAt)
        .ThenBy(f => f.Id, StringComparer.Ordinal).FirstOrDefault();

}
