using HD2Mimnt.Core.IO;

namespace HD2Mimnt.Core.Darktide;

public static class DarktidePaths
{
    public const string GameExecutableRelativePath = "binaries/Darktide.exe";
    public const string BundleDirectoryName = "bundle";
    public const string ModsDirectoryName = "mods";
    public const string LoadOrderRelativePath = "mods/mod_load_order.txt";
    public const string ToggleScriptRelativePath = "toggle_darktide_mods.bat";
    public const string PatcherRelativePath = "tools/dtkit-patch.exe";
    public const string DmlCoreRelativePath = "binaries/mod_loader";
    public const string DmfModRelativePath = "mods/dmf/dmf.mod";
    public const string DmfFolderName = "dmf";
    public const string DmlBaseFolderName = "base";
    public const string StagingPrefix = ".darktide-staging-";
    public const string BackupPrefix = ".darktide-backup-";

    public static string ModsDirectory(string gameRoot) => Path.Combine(gameRoot, ModsDirectoryName);
    public static string GameExecutable(string gameRoot) => Path.Combine(gameRoot, "binaries", "Darktide.exe");
    public static string LoadOrderFile(string gameRoot) => Path.Combine(gameRoot, "mods", "mod_load_order.txt");
    public static string ToggleScript(string gameRoot) => Path.Combine(gameRoot, ToggleScriptRelativePath);
    public static string Patcher(string gameRoot) => Path.Combine(gameRoot, "tools", "dtkit-patch.exe");
    public static string DmlCore(string gameRoot) => Path.Combine(gameRoot, "binaries", "mod_loader");
    public static string DmfMod(string gameRoot) => Path.Combine(gameRoot, "mods", "dmf", "dmf.mod");

    public static bool IsFrameworkFolder(string name)
        => name.Equals(DmlBaseFolderName, StringComparison.OrdinalIgnoreCase)
           || name.Equals(DmfFolderName, StringComparison.OrdinalIgnoreCase);

    public static bool IsStagingOrBackupFolder(string name)
        => name.StartsWith(StagingPrefix, StringComparison.OrdinalIgnoreCase)
           || name.StartsWith(BackupPrefix, StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>模组目录名必须能直接作为单个目录名，不能携带路径分隔符或越级片段。</summary>
    public static bool IsSafeModFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar)) return false;
        if (name.Contains(':')) return false;
        if (name.EndsWith('.') || name.EndsWith(' ')) return false;
        if (ReservedDeviceNames.Contains(Path.GetFileNameWithoutExtension(name))) return false;
        return !IsFrameworkFolder(name);
    }

    public static string ResolveModFolder(string gameRoot, string name)
    {
        if (!IsSafeModFolderName(name))
            throw new InvalidDataException($"不安全的模组目录名：{name}");
        var modsRoot = ModsDirectory(gameRoot);
        var target = Path.Combine(modsRoot, name);
        if (!PathGuard.IsInsideRoot(modsRoot, target))
            throw new InvalidDataException($"模组目录越过 mods 根目录：{name}");
        return Path.GetFullPath(target);
    }

    /// <summary>删除和替换前拒绝指向其他位置的目录联接、符号链接。</summary>
    public static bool IsReparsePoint(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
