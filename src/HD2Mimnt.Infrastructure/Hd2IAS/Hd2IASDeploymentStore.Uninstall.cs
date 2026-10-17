using HD2Mimnt.Core.Hd2IAS;
using System.Security.Cryptography;

namespace HD2Mimnt.Infrastructure.Hd2IAS;

public sealed partial class Hd2IASDeploymentStore
{
    private static bool PreserveOnUninstall(string target) => target.Equals("bin/dxgi.dll", StringComparison.OrdinalIgnoreCase)
        || target.Equals("bin/ReShade.ini", StringComparison.OrdinalIgnoreCase)
        || InstalledBuildReader.PreserveExisting(target);

    public async Task<IReadOnlyList<string>> GetMissingFilesAsync(string game, CancellationToken token = default)
    {
        var installation = await ReadAsync(game, token);
        var targets = installation?.Files.Where(file => !PreserveOnUninstall(file.Target)).Select(file => file.Target).ToArray()
            ?? ["bin/HD2IndependentRig.addon64", PackageManifestReader.BuildTarget];
        var missing = new List<string>();
        foreach (var relative in targets)
        {
            token.ThrowIfCancellationRequested();
            var path = Target(game, relative);
            if (!File.Exists(path) || new FileInfo(path).Length == 0) missing.Add(relative);
        }
        return missing;
    }

    public async Task<Hd2IASUninstallResult> UninstallAsync(string game, IProgress<string>? progress,
        CancellationToken token)
    {
        var previous = await ReadAsync(game, token)
            ?? throw new InvalidOperationException("未找到管理器的 HD2IAS 安装记录，不能自动卸载未确认归属的文件。");
        var recognized = await ReadRecognizedFilesAsync(game, token);
        var changes = new List<Change>(); var inventory = new List<InventoryFile>();
        var preserved = new List<string>(); var modified = new List<string>();
        foreach (var group in recognized.GroupBy(file => file.Target, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var relative = group.Key;
            if (PreserveOnUninstall(relative) || !relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase))
            {
                preserved.Add(relative);
                progress?.Report($"卸载保留：{relative}");
                continue;
            }
            var current = await HashAsync(Target(game, relative), token);
            if (current == null) continue;
            if (!group.Any(file => file.Sha256.Equals(current, StringComparison.OrdinalIgnoreCase)))
            {
                preserved.Add(relative); modified.Add(relative);
                inventory.Add(new(relative, current, current, "PreserveModified"));
                progress?.Report($"卸载保留已修改文件：{relative}");
                continue;
            }
            changes.Add(new(relative, current, null));
            inventory.Add(new(relative, current, null, "RemoveRecognized"));
        }
        var removed = changes.Select(change => change.Target).ToArray();
        Dictionary<string, byte[]>? preparedFiles = null;
        const string reShadeConfig = "bin/ReShade.ini";
        var configPath = Target(game, reShadeConfig);
        if (File.Exists(configPath))
        {
            var contents = await File.ReadAllBytesAsync(configPath, token);
            if (ReShadeFontCleaner.Clean(contents, game) is { } cleaned)
            {
                var before = Convert.ToHexStringLower(SHA256.HashData(contents));
                var after = Convert.ToHexStringLower(SHA256.HashData(cleaned));
                preparedFiles = new() { [reShadeConfig] = cleaned };
                changes.Insert(0, new(reShadeConfig, before, after));
                inventory.Add(new(reShadeConfig, before, after, "RestoreReShadeFont"));
                progress?.Report("清理 ReShade 的 HD2IAS 字体引用");
            }
        }
        var installation = previous with { Revision = Guid.NewGuid().ToString("N"), Files = [], Inventory = inventory, IsUninstalled = true };
        await CommitAsync(game, installation, changes, progress, "卸载", token, preparedFiles);
        return new(removed, preserved, modified);
    }
}
