using HD2Mimnt.Core.Darktide;
using HD2Mimnt.Core.IO;

namespace HD2Mimnt.Infrastructure.Darktide;

public interface IDarktideFileOperations
{
    void MoveDirectory(string source, string destination);
    void DeleteDirectory(string path);
}

public sealed class DarktideFileOperations : IDarktideFileOperations
{
    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);
    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);
}

public sealed class DarktideModInstaller
{
    private readonly IDarktideFileOperations _files;

    public DarktideModInstaller(IDarktideFileOperations? files = null)
        => _files = files ?? new DarktideFileOperations();

    public async Task<DarktideInstallResult> InstallFromStagingAsync(string gameRoot, string stagedModRoot,
        string internalName, bool allowReplace, bool register,
        Func<CancellationToken, Task>? afterCommit = null, CancellationToken cancellationToken = default,
        bool allowDmfFramework = false)
    {
        var isDmf = internalName.Equals(DarktidePaths.DmfFolderName, StringComparison.OrdinalIgnoreCase);
        if (isDmf && !allowDmfFramework)
            throw new InvalidOperationException("DMF 是框架，只能通过加载器页面的 DMF 导入入口安装。");
        if (!isDmf && !DarktidePaths.IsSafeModFolderName(internalName))
            throw new InvalidOperationException($"不能安装名为“{internalName}”的保留或非法模组目录。");
        var modsRoot = DarktidePaths.ModsDirectory(gameRoot);
        if (!Directory.Exists(modsRoot)) throw new DirectoryNotFoundException("mods 目录不存在，请先安装 Darktide Mod Loader。");
        if (!Directory.Exists(stagedModRoot)) throw new DirectoryNotFoundException("暂存模组目录不存在。");
        var stagedModFile = Path.Combine(stagedModRoot, internalName + ".mod");
        if (!File.Exists(stagedModFile) || new FileInfo(stagedModFile).Length == 0)
            throw new InvalidDataException($"暂存模组缺少 {internalName}.mod。");

        cancellationToken.ThrowIfCancellationRequested();
        var target = isDmf
            ? Path.GetFullPath(Path.Combine(modsRoot, DarktidePaths.DmfFolderName))
            : DarktidePaths.ResolveModFolder(gameRoot, internalName);
        PathGuard.ResolveInsideRoot(modsRoot, target, "模组安装目标越过了 mods 根目录。");
        var warnings = new List<string>();
        var replaced = Directory.Exists(target);
        if (replaced && !allowReplace)
            throw new DarktideInstallConflictException(internalName);
        if (replaced && DarktidePaths.IsReparsePoint(target))
            throw new InvalidOperationException("目标模组目录是符号链接或目录联接，已停止替换。");

        string? backupRoot = null;
        string? backup = null;
        if (replaced)
        {
            backupRoot = Path.Combine(modsRoot, DarktidePaths.BackupPrefix + Guid.NewGuid().ToString("N"));
            backup = Path.Combine(backupRoot, internalName);
            Directory.CreateDirectory(backupRoot);
            _files.MoveDirectory(target, backup);
        }

        try
        {
            _files.MoveDirectory(stagedModRoot, target);
            if (afterCommit != null) await afterCommit(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            RestoreBackup(target, backup);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or InvalidDataException or ArgumentException)
        {
            var rollbackError = RestoreBackup(target, backup);
            throw new IOException(rollbackError == null
                ? $"安装模组失败，原有文件已恢复：{ex.Message}"
                : $"安装模组失败，且回滚未完成：{ex.Message}；{rollbackError}", ex);
        }

        if (backupRoot != null)
        {
            try { _files.DeleteDirectory(backupRoot); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"旧版本备份未能删除，已保留在 {backupRoot}：{ex.Message}");
            }
        }

        return new DarktideInstallResult(internalName, target, replaced, register, warnings);
    }

    public void Uninstall(string gameRoot, string internalName)
    {
        if (!DarktidePaths.IsSafeModFolderName(internalName))
            throw new InvalidOperationException($"拒绝删除保留或非法模组目录：{internalName}");
        var target = DarktidePaths.ResolveModFolder(gameRoot, internalName);
        if (!Directory.Exists(target)) return;
        if (DarktidePaths.IsReparsePoint(target))
            throw new InvalidOperationException("模组目录是符号链接或目录联接，已停止删除。");
        _files.DeleteDirectory(target);
    }

    public void CleanupDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try { _files.DeleteDirectory(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string? RestoreBackup(string target, string? backup)
    {
        try
        {
            if (Directory.Exists(target))
            {
                if (DarktidePaths.IsReparsePoint(target))
                    return "目标目录变成符号链接或目录联接，未自动删除。";
                _files.DeleteDirectory(target);
            }
            if (backup != null && Directory.Exists(backup))
            {
                var parent = Path.GetDirectoryName(backup);
                _files.MoveDirectory(backup, target);
                if (parent != null && Directory.Exists(parent)) _files.DeleteDirectory(parent);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"恢复旧版本失败：{ex.Message}；备份位置：{backup}";
        }
    }
}
