using HD2Mimnt.Core;
using HD2Mimnt.Core.Darktide;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Storage;

namespace HD2Mimnt.Tests.Darktide;

public sealed class DarktideIsolationTests
{
    [Fact]
    public void DefaultPaths_DoNotReuseHd2Identity()
    {
        var dataPath = RuntimeDataPaths.GetDefaultApplicationDataPath();
        Assert.Contains("DarktideMimnt", Path.GetFileName(dataPath));
        Assert.DoesNotContain("HD2Mimnt", Path.GetFileName(dataPath));

        var locations = new StorageLocations(Path.Combine(Path.GetTempPath(), "dt-app"),
            Path.Combine(Path.GetTempPath(), "dt-library"), Path.Combine(Path.GetTempPath(), "dt-cache"));
        Assert.EndsWith("darktidemimnt.db", locations.DatabasePath);

        var storage = DarktideStoragePaths.Create(locations);
        Assert.StartsWith(Path.Combine(locations.AppDataPath, "Darktide"), storage.Archives);
        Assert.DoesNotContain("hd2mimnt", storage.Archives, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StorageLocationStore_PrefersDarktideOverride()
    {
        var old = Environment.GetEnvironmentVariable("DARKTIDEMIMNT_DATA_PATH");
        var oldHd2 = Environment.GetEnvironmentVariable("HD2MIMNT_DATA_PATH");
        var path = Path.Combine(Path.GetTempPath(), "DarktideMimntTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            Environment.SetEnvironmentVariable("DARKTIDEMIMNT_DATA_PATH", path);
            Environment.SetEnvironmentVariable("HD2MIMNT_DATA_PATH", Path.Combine(path, "ignored"));
            var store = new StorageLocationStore();
            Assert.Equal(Path.TrimEndingDirectorySeparator(path),
                Path.TrimEndingDirectorySeparator(store.BootstrapPath));
            Assert.EndsWith("darktidemimnt.db", store.Defaults.DatabasePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DARKTIDEMIMNT_DATA_PATH", old);
            Environment.SetEnvironmentVariable("HD2MIMNT_DATA_PATH", oldHd2);
            try { Directory.Delete(path, recursive: true); } catch { }
        }
    }
}
