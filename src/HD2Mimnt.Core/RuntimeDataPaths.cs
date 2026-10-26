namespace HD2Mimnt.Core;

/// <summary>Returns the per-user data directory for the current build configuration.</summary>
public static class RuntimeDataPaths
{
    public static string GetDefaultApplicationDataPath()
    {
#if DEBUG
        const string directoryName = "DarktideMimnt.dev";
#else
        const string directoryName = "DarktideMimnt";
#endif
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), directoryName);
    }
}
