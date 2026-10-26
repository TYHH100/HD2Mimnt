using System.IO.Compression;
using HD2Mimnt.Core.Darktide;

namespace HD2Mimnt.Infrastructure.Darktide;

public sealed class DarktideLoaderPackageInspector
{
    private static readonly string[] RequiredCore =
    {
        "binaries/mod_loader",
        "mods/base/mod_manager.lua"
    };

    private static readonly string[] AllowedRootFiles =
    {
        "README.md",
        "toggle_darktide_mods.bat",
        "mods/mod_load_order.txt"
    };

    private static bool IsAllowedFile(string path)
    {
        if (AllowedRootFiles.Contains(path, StringComparer.OrdinalIgnoreCase)) return true;
        if (path.Equals("binaries/mod_loader", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("tools/dtkit-patch.exe", StringComparison.OrdinalIgnoreCase)
            || path.Equals("tools/README.md", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("mods/base/", StringComparison.OrdinalIgnoreCase)) return true;
        return path.StartsWith("bundle/", StringComparison.OrdinalIgnoreCase)
               && Path.GetFileName(path).Contains(".patch_", StringComparison.OrdinalIgnoreCase);
    }

    public DarktideLoaderPackageInfo Inspect(string packagePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            var files = new List<string>();
            var unexpected = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                var path = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (path.Length == 0) continue;
                if (!IsSafePath(path)) return Failure(packagePath, $"安装包包含不安全路径：{entry.FullName}");
                if (IsSymbolicLink(entry)) return Failure(packagePath, $"安装包包含符号链接：{entry.FullName}");
                if (path.EndsWith('/')) continue;
                if (!seen.Add(path)) return Failure(packagePath, $"安装包包含重复条目：{path}");
                files.Add(path);
                if (!IsAllowedFile(path)) unexpected.Add(path);
            }

            var hasLoadOrder = files.Contains("mods/mod_load_order.txt", StringComparer.OrdinalIgnoreCase);
            var hasCore = RequiredCore.All(required => files.Contains(required, StringComparer.OrdinalIgnoreCase));
            var hasToggle = files.Contains("toggle_darktide_mods.bat", StringComparer.OrdinalIgnoreCase);
            var hasPatcher = files.Contains("tools/dtkit-patch.exe", StringComparer.OrdinalIgnoreCase);
            var nameHintUpdate = Path.GetFileName(packagePath).Contains("updateonly", StringComparison.OrdinalIgnoreCase)
                                 || Path.GetFileName(packagePath).Contains("update-only", StringComparison.OrdinalIgnoreCase);
            var type = hasLoadOrder ? DarktideReleasePackageType.Full : DarktideReleasePackageType.UpdateOnly;
            if (nameHintUpdate && type == DarktideReleasePackageType.Full)
                return Failure(packagePath, "文件名表示仅更新包，但压缩包包含 mods/mod_load_order.txt；已停止安装。");
            if (unexpected.Count > 0)
                return Failure(packagePath,
                    $"安装包包含未识别的文件，已停止安装：{string.Join(", ", unexpected.Take(8))}");

            string? error = null;
            if (!hasCore) error = "安装包缺少 binaries/mod_loader 或 mods/base/mod_manager.lua。";
            else if (!hasToggle) error = "安装包缺少 toggle_darktide_mods.bat。";
            else if (!hasPatcher) error = "安装包缺少 tools/dtkit-patch.exe，无法运行官方切换脚本。";
            return new DarktideLoaderPackageInfo(packagePath, type, hasLoadOrder, hasCore, hasToggle, hasPatcher,
                files, unexpected, error);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Failure(packagePath, $"无法读取加载器安装包：{ex.Message}");
        }
    }

    private static DarktideLoaderPackageInfo Failure(string packagePath, string error)
        => new(packagePath, null, false, false, false, false, Array.Empty<string>(), Array.Empty<string>(), error);

    private static bool IsSafePath(string path)
    {
        if (path[0] == '/' || path.Contains('\0')) return false;
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':') return false;
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0) continue;
            if (segment is "." or "..") return false;
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        }
        return true;
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry) => ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;
}
