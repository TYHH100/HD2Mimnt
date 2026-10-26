using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Core.Darktide;

public static class DarktideSettingsKeys
{
    public const string GamePath = "Darktide.GamePath";
    public const string LoaderLastInstalledTag = "Darktide.Loader.LastInstalledTag";
    public const string LoaderLastInstalledAt = "Darktide.Loader.LastInstalledAt";
}

public sealed record DarktideStoragePaths(
    string Root,
    string Archives,
    string Temp,
    string LoaderBackups,
    string LoadOrderBackups,
    string Downloads)
{
    public static DarktideStoragePaths Create(StorageLocations locations)
    {
        var root = Path.Combine(locations.AppDataPath, "Darktide");
        return new DarktideStoragePaths(
            root,
            Path.Combine(root, "Archives"),
            Path.Combine(locations.TempPath, "Darktide"),
            Path.Combine(root, "LoaderBackups"),
            Path.Combine(root, "LoadOrderBackups"),
            Path.Combine(locations.TempPath, "Darktide", "Downloads"));
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Archives);
        Directory.CreateDirectory(Temp);
        Directory.CreateDirectory(LoaderBackups);
        Directory.CreateDirectory(LoadOrderBackups);
        Directory.CreateDirectory(Downloads);
    }
}
