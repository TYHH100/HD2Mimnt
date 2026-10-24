using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 解析两种游戏来源的实际目录。HellForge 安装根目录包含安装标记与 game 游戏副本；
/// 设置页允许用户选择安装根目录，也允许直接选择其中的 game 目录。
/// </summary>
internal static class GameInstallationPaths
{
    private static readonly string[] HellForgeRootMarkers =
        ["hellforge-install.json", "bts-install.json", "HellForge.exe"];

    public static string? ResolveHellForgeGameDirectory(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) return null;
        var fullPath = GetFullPath(configuredPath);
        if (fullPath is null) return null;
        if (IsHellForgeRoot(fullPath)) return Path.Combine(fullPath, "game");
        if (IsHellForgeGameSubdirectory(fullPath)) return fullPath;
        return Path.Combine(fullPath, "game");
    }

    public static bool TryResolveHellForgeRoot(string? configuredPath, out string root)
    {
        root = string.Empty;
        if (string.IsNullOrWhiteSpace(configuredPath)) return false;
        var fullPath = GetFullPath(configuredPath);
        if (fullPath is null) return false;
        if (IsHellForgeRoot(fullPath))
        {
            root = fullPath;
            return true;
        }
        if (IsHellForgeGameSubdirectory(fullPath))
        {
            root = Path.GetDirectoryName(fullPath)!;
            return true;
        }
        return false;
    }

    public static bool ValidateGameFiles(string path)
    {
        if (!Directory.Exists(path)) return false;
        return Directory.Exists(Path.Combine(path, "data"))
            && Directory.Exists(Path.Combine(path, "tools"))
            && Directory.Exists(Path.Combine(path, "bin"))
            && File.Exists(Path.Combine(path, "bin", "helldivers2.exe"));
    }

    private static bool IsHellForgeRoot(string path)
        => Directory.Exists(path)
           && HellForgeRootMarkers.Any(marker => File.Exists(Path.Combine(path, marker)));

    private static bool IsHellForgeGameSubdirectory(string path)
        => string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), "game",
               StringComparison.OrdinalIgnoreCase)
           && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) is { } parent
           && IsHellForgeRoot(parent);

    private static string? GetFullPath(string path)
    {
        try { return Path.GetFullPath(path.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }
    }
}
