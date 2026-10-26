using System.Text.RegularExpressions;
using HD2Mimnt.Core.Darktide;
using HD2Mimnt.Core.IO;
using Microsoft.Win32;

namespace HD2Mimnt.Infrastructure.Darktide;

public sealed partial class DarktideGameLocator
{
    private static readonly string[] SteamGameFolderNames =
    {
        "Warhammer 40,000 DARKTIDE",
        "Warhammer 40,000 - Darktide"
    };

    public DarktideGameState Locate(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return DarktideGameState.NotConfigured;

        string selected;
        try
        {
            selected = NormalizeDirectory(configuredPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new DarktideGameState(configuredPath, DarktideGameStatus.InvalidPath,
                false, false, false, false, false, false, false, false, false, false, null,
                new[] { "路径不是完整的绝对目录。" });
        }

        if (!Directory.Exists(selected))
            return new DarktideGameState(selected, DarktideGameStatus.InvalidPath,
                false, false, false, false, false, false, false, false, false, false, null,
                new[] { "目录不存在。" });

        // Xbox/Game Pass 包把游戏文件放在 content 子目录；这是实际根目录，不是错误选择的上级目录。
        var root = HasGameFiles(selected) ? selected : Path.Combine(selected, "content");
        if (!HasGameFiles(root))
            return new DarktideGameState(selected, DarktideGameStatus.InvalidPath,
                false, false, false, false, false, false, false, false, false, false, null,
                new[] { "未找到 binaries/Darktide.exe 与 bundle 目录，请选择游戏根目录。" });

        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var notes = new List<string>();
        if (!PathGuardEquals(root, selected)) notes.Add("已识别 Game Pass 的 content 子目录为实际游戏根目录。");

        var mods = DarktidePaths.ModsDirectory(root);
        var hasMods = Directory.Exists(mods);
        var hasLoadOrder = File.Exists(DarktidePaths.LoadOrderFile(root));
        var dmlCore = File.Exists(DarktidePaths.DmlCore(root));
        var toggle = File.Exists(DarktidePaths.ToggleScript(root));
        var patcher = File.Exists(DarktidePaths.Patcher(root));
        var baseManager = File.Exists(Path.Combine(mods, "base", "mod_manager.lua"));
        var dmf = File.Exists(DarktidePaths.DmfMod(root));
        var alternative = FindAlternativeLoader(root);

        var loaderInstalled = dmlCore && toggle && patcher && baseManager && hasLoadOrder;
        var loaderIncomplete = (dmlCore || toggle || baseManager) && !loaderInstalled;
        var status = loaderInstalled
            ? DarktideGameStatus.LoaderInstalled
            : loaderIncomplete ? DarktideGameStatus.LoaderIncomplete : DarktideGameStatus.GameWithoutLoader;

        if (!hasMods) notes.Add("缺少 mods 目录，加载器尚未安装。");
        if (loaderIncomplete) notes.Add("检测到部分加载器文件，但缺少 binaries/mod_loader、toggle_darktide_mods.bat、dtkit-patch 或 mods/mod_load_order.txt。");
        if (loaderInstalled && !dmf) notes.Add("未检测到 Darktide Mod Framework（mods/dmf/dmf.mod）。");
        if (alternative != null) notes.Add($"检测到替代加载器“{alternative}”，标准 mod_load_order.txt 可能不是实际加载顺序。");

        return new DarktideGameState(root, status, true, true, hasMods, hasLoadOrder,
            dmlCore, dmf, toggle, patcher, false, false, alternative, notes);
    }

    /// <summary>只用于定位候选目录，不会创建任何文件；失败返回空列表。</summary>
    public IReadOnlyList<string> FindInstalledGames()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in EnumerateSteamLibraries())
            foreach (var name in SteamGameFolderNames)
                AddCandidate(candidates, Path.Combine(library, "steamapps", "common", name));

        foreach (var basePath in new[]
                 {
                     @"C:\Program Files (x86)\Steam\steamapps\common",
                     @"C:\Program Files\Steam\steamapps\common"
                 })
            foreach (var name in SteamGameFolderNames)
                AddCandidate(candidates, Path.Combine(basePath, name));

        foreach (var name in SteamGameFolderNames)
            AddCandidate(candidates, Path.Combine(@"C:\XboxGames", name, "content"));

        return candidates.ToList();
    }

    private void AddCandidate(HashSet<string> candidates, string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            var state = Locate(path);
            if (state.RootPath != null && state.Status != DarktideGameStatus.InvalidPath) candidates.Add(state.RootPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static IEnumerable<string> EnumerateSteamLibraries()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var steamPath in ReadSteamInstallPaths())
        {
            roots.Add(steamPath);
            var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            try
            {
                if (!File.Exists(vdf)) continue;
                foreach (Match match in LibraryPathRegex().Matches(File.ReadAllText(vdf)))
                {
                    var path = match.Groups[1].Value.Replace(@"\\", @"\");
                    if (Path.IsPathFullyQualified(path)) roots.Add(Path.TrimEndingDirectorySeparator(path));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return roots;
    }

    private static IEnumerable<string> ReadSteamInstallPaths()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        foreach (var (hive, key) in new[]
                 {
                     (Registry.CurrentUser, @"Software\Valve\Steam"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
                     (Registry.LocalMachine, @"SOFTWARE\Valve\Steam")
                 })
        {
            string? value = null;
            try { using var subKey = hive.OpenSubKey(key); value = subKey?.GetValue("SteamPath") as string ?? subKey?.GetValue("InstallPath") as string; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            if (!string.IsNullOrWhiteSpace(value)) yield return Path.TrimEndingDirectorySeparator(value);
        }
    }

    private static string? FindAlternativeLoader(string root)
    {
        var mods = DarktidePaths.ModsDirectory(root);
        try
        {
            if (!Directory.Exists(mods)) return null;
            foreach (var directory in Directory.EnumerateDirectories(mods))
            {
                var name = Path.GetFileName(directory);
                if (DarktidePaths.IsFrameworkFolder(name) || DarktidePaths.IsStagingOrBackupFolder(name)) continue;
                if (AmlNameRegex().IsMatch(name) || name.Contains("automodloading", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("auto-mod-loading", StringComparison.OrdinalIgnoreCase))
                    return name;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static bool HasGameFiles(string root)
        => File.Exists(DarktidePaths.GameExecutable(root))
           && Directory.Exists(Path.Combine(root, "bundle"));

    private static string NormalizeDirectory(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (!Path.IsPathFullyQualified(expanded)) throw new ArgumentException("请选择完整的绝对目录路径。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
    }

    private static bool PathGuardEquals(string left, string right)
        => string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right),
            StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathRegex();

    [GeneratedRegex(@"(?i)(^|[-_. ])aml([-_. ]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex AmlNameRegex();
}
