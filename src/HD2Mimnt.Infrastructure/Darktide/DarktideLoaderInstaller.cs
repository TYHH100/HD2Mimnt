using System.IO.Compression;
using HD2Mimnt.Core.Darktide;
using HD2Mimnt.Core.IO;

namespace HD2Mimnt.Infrastructure.Darktide;

public sealed class DarktideLoaderInstaller
{
    private const string LoadOrderPath = "mods/mod_load_order.txt";

    public async Task<DarktideLoaderInstallResult> InstallAsync(string gameRoot, string packagePath,
        DarktideLoaderPackageInfo package, string stagingParent, string backupParent,
        CancellationToken cancellationToken = default)
    {
        if (package.Error != null || !package.IsInstallable || package.Type is null)
            return Failed(package.Type ?? DarktideReleasePackageType.Full, package.Error ?? "安装包未通过检查。");

        var type = package.Type.Value;
        var loadOrderTarget = DarktidePaths.LoadOrderFile(gameRoot);
        var loadOrderExists = File.Exists(loadOrderTarget);
        if (type == DarktideReleasePackageType.UpdateOnly)
        {
            if (!File.Exists(DarktidePaths.DmlCore(gameRoot))
                || !File.Exists(Path.Combine(gameRoot, "mods", "base", "mod_manager.lua")))
                return Failed(type, "检测不到已安装的 Darktide Mod Loader，不能使用仅更新包。");
            if (!loadOrderExists)
                return Failed(type, "仅更新包不包含 mod_load_order.txt；当前安装缺少该文件，请改用完整包。");
        }

        var staging = Path.Combine(stagingParent, "loader-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(backupParent, $"loader-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        var installed = new List<string>();
        var backupByTarget = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        byte[]? originalLoadOrder = null;
        try
        {
            Directory.CreateDirectory(staging);
            await ExtractAsync(packagePath, staging, cancellationToken);
            Directory.CreateDirectory(backup);
            if (loadOrderExists) originalLoadOrder = await File.ReadAllBytesAsync(loadOrderTarget, cancellationToken);

            var installFiles = package.Files
                .Where(path => !string.Equals(path, LoadOrderPath, StringComparison.OrdinalIgnoreCase) || !loadOrderExists)
                .ToList();
            var targets = new List<(string Relative, string Target)>();
            foreach (var relative in installFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.GetFullPath(Path.Combine(gameRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                PathGuard.ResolveInsideRoot(gameRoot, target, "加载器安装包条目越过了游戏目录。");
                if (File.Exists(target))
                {
                    if (File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly))
                        return Failed(type, $"目标文件为只读，已停止安装：{relative}");
                    if (DarktidePaths.IsReparsePoint(target))
                        return Failed(type, $"目标文件是符号链接或目录联接，已停止安装：{relative}");
                    var backupTarget = Path.Combine(backup, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(backupTarget)!);
                    File.Copy(target, backupTarget, overwrite: false);
                    backupByTarget[target] = backupTarget;
                }
                else
                {
                    backupByTarget[target] = null;
                }
                targets.Add((relative, target));
            }

            foreach (var (relative, target) in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var staged = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                var temporary = target + ".darktide-tmp-" + Guid.NewGuid().ToString("N");
                await using (var source = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read,
                                 bufferSize: 81920, useAsync: true))
                await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                                 FileShare.None, bufferSize: 81920, useAsync: true))
                    await source.CopyToAsync(destination, cancellationToken);
                File.Move(temporary, target, overwrite: true);
                installed.Add(target);
            }

            if (loadOrderExists && originalLoadOrder != null)
            {
                var current = await File.ReadAllBytesAsync(loadOrderTarget, cancellationToken);
                if (!current.AsSpan().SequenceEqual(originalLoadOrder))
                {
                    Rollback(installed, backupByTarget);
                    return Failed(type, "安装后 mod_load_order.txt 发生变化，已回滚加载器文件。", rolledBack: true, backup);
                }
            }

            var preserved = loadOrderExists;
            PruneBackups(backupParent);
            return new DarktideLoaderInstallResult(true, type, backup, preserved,
                installed.Select(path => Path.GetRelativePath(gameRoot, path).Replace('\\', '/')).ToList(), false, null);
        }
        catch (OperationCanceledException)
        {
            Rollback(installed, backupByTarget);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            var rollbackComplete = Rollback(installed, backupByTarget);
            return Failed(type, $"安装加载器失败：{ex.Message}" + (rollbackComplete ? "，已回滚。" : "，回滚未完整完成，请检查备份目录。"),
                rolledBack: rollbackComplete, backup);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private static async Task ExtractAsync(string packagePath, string stagingRoot, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (relative.Length == 0 || relative.EndsWith('/')) continue;
            if (relative.Split('/').Any(segment => segment is "." or "..")
                || relative.Contains('\0') || (relative.Length >= 2 && char.IsLetter(relative[0]) && relative[1] == ':'))
                throw new InvalidDataException($"安装包包含不安全路径：{entry.FullName}");
            var target = Path.GetFullPath(Path.Combine(stagingRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            PathGuard.ResolveInsideRoot(stagingRoot, target, "安装包条目越过了暂存目录。");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open();
            await using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true);
            await source.CopyToAsync(destination, cancellationToken);
        }
    }

    private static bool Rollback(List<string> installed, Dictionary<string, string?> backupByTarget)
    {
        var complete = true;
        foreach (var target in installed.AsEnumerable().Reverse())
        {
            try
            {
                if (backupByTarget.TryGetValue(target, out var backup) && backup != null)
                {
                    var temporary = target + ".darktide-restore-" + Guid.NewGuid().ToString("N");
                    File.Copy(backup, temporary, overwrite: true);
                    File.Move(temporary, target, overwrite: true);
                }
                else if (File.Exists(target))
                {
                    File.Delete(target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                complete = false;
            }
        }
        return complete;
    }

    private static DarktideLoaderInstallResult Failed(DarktideReleasePackageType type, string error,
        bool rolledBack = false, string? backup = null)
        => new(false, type, backup, false, Array.Empty<string>(), rolledBack, error);

    private static void PruneBackups(string backupParent)
    {
        try
        {
            if (!Directory.Exists(backupParent)) return;
            foreach (var directory in Directory.EnumerateDirectories(backupParent, "loader-*")
                         .OrderByDescending(Directory.GetLastWriteTimeUtc).Skip(5))
                TryDeleteDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
