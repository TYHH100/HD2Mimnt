using HD2Mimnt.Core.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Migration;
using HD2Mimnt.Core.Services;

namespace HD2Mimnt.Infrastructure.Migration;

internal static class ExternalExportSupport
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal static string DataRoot(ExternalMigrationSource target) => MigrationJson.DataDirectory(target.DataPath);
    internal static string ModId(ExternalMigrationMod mod) => MigrationIdentity.Create("hd2mimnt-export-v1", mod.SourceId, mod.ContentHash).ToString();
    internal static string ProfileId(ExternalMigrationProfile profile, ExternalMigrationPlan plan)
        => MigrationIdentity.Create("hd2mimnt-export-profile-v1", profile.SourceId, plan.TargetDeploymentOrder.ToString()!,
            JsonSerializer.Serialize(profile), string.Join("|", plan.Mods.Select(mod => ModId(mod)))).ToString();

    internal static async Task<JsonObject> ReadObjectAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return new();
        return JsonNode.Parse(await File.ReadAllTextAsync(path, token), documentOptions: new JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
            ?? throw new InvalidDataException($"目标数据不是 JSON 对象：{path}");
    }

    internal static async Task<string> FileHashAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return string.Empty;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    internal static async Task InspectFilesAsync(ExternalMigrationPlan plan, IEnumerable<string> paths, CancellationToken token)
    {
        EnsureStopped(plan.Source);
        foreach (var path in paths)
            plan.TargetFileHashes[path] = await FileHashAsync(path, token);
        var targetRoot = DataRoot(plan.Source);
        foreach (var mod in plan.Mods)
        {
            var source = MigrationJson.Root(mod.SourcePath);
            if (targetRoot.Equals(source, StringComparison.OrdinalIgnoreCase) || PathGuard.IsInsideRoot(source, targetRoot)
                || plan.TargetModsPath.Equals(source, StringComparison.OrdinalIgnoreCase) || PathGuard.IsInsideRoot(source, plan.TargetModsPath))
                plan.Issues.Add(new(mod.Manifest.Name, "目标目录位于源模组内部，请选择其它目录。", true));
        }
    }

    internal static void EnsureStopped(ExternalMigrationSource target)
    {
        var name = target.Manager switch { ExternalManagerKind.Helldivers2ModManager => "Helldivers2ModManager", ExternalManagerKind.Echelon => "Echelon", _ => "HD2Arsenal" };
        var processes = Process.GetProcessesByName(name);
        try
        {
            foreach (var process in processes)
            {
                if (!string.IsNullOrWhiteSpace(target.InstallationPath))
                {
                    try
                    {
                        var executable = process.MainModule?.FileName;
                        if (executable != null && !MigrationJson.Root(Path.GetDirectoryName(executable)!).Equals(MigrationJson.Root(target.InstallationPath), StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) when (process.HasExited) { continue; }
                }
                throw new IOException($"请先关闭 {name}，避免它覆盖迁回的数据。");
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    internal static ExternalMigrationProfile MapProfile(ExternalMigrationProfile source, ExternalMigrationPlan plan)
    {
        var mods = plan.Mods.ToDictionary(mod => mod.SourceId, StringComparer.OrdinalIgnoreCase);
        bool Normal(ExternalMigrationProfileMod member) => mods[member.SourceModId].Role == BslModRole.None;
        var normal = source.Mods.Where(Normal).ToList();
        var reverse = source.DeploymentOrder != plan.TargetDeploymentOrder;
        var mapped = new ExternalMigrationProfile { SourceId = source.SourceId, Name = source.Name, WasActive = source.WasActive,
            IsLibraryLayout = source.IsLibraryLayout, DeploymentOrder = plan.TargetDeploymentOrder,
            IsLuaGroupCollapsed = source.IsLuaGroupCollapsed, LuaSeparatorColor = source.LuaSeparatorColor };
        if (reverse) normal.Reverse();
        mapped.Mods.AddRange(normal);
        mapped.Mods.AddRange(source.Mods.Where(member => !Normal(member)).OrderBy(member => Analysis.LoaderRegistry.Current.PriorityRank(mods[member.SourceModId].Role)));
        var separators = source.Separators.OrderBy(separator => separator.Order).ToList();
        for (var i = 0; i < separators.Count; i++)
        {
            var separator = separators[i];
            var start = Math.Clamp(separator.Order, 0, normal.Count);
            var end = Math.Clamp(separator.MemberCount.HasValue ? start + separator.MemberCount.Value
                : i + 1 < separators.Count ? separators[i + 1].Order : normal.Count, start, normal.Count);
            mapped.Separators.Add(separator with { Order = reverse ? normal.Count - end : start, MemberCount = end - start });
        }
        mapped.Separators.Sort((a, b) => a.Order.CompareTo(b.Order));
        if (plan.TargetDeploymentOrder == DeploymentOrder.BottomToTop)
        {
            var fixedMembers = mapped.Mods.Skip(normal.Count).Reverse().ToList();
            mapped.Mods.Clear(); mapped.Mods.AddRange(fixedMembers); mapped.Mods.AddRange(normal);
            for (var index = 0; index < mapped.Separators.Count; index++) mapped.Separators[index] = mapped.Separators[index] with { Order = mapped.Separators[index].Order + fixedMembers.Count };
        }
        return mapped;
    }

    // 外部列表只有起点分隔符；添加结束分隔符才能保留目标中显式的短范围。
    internal static IEnumerable<(ExternalMigrationProfileMod? Mod, ExternalMigrationSeparator? Separator)> Entries(ExternalMigrationProfile profile)
    {
        var separators = profile.Separators.OrderBy(separator => separator.Order).ToList();
        for (var position = 0; position <= profile.Mods.Count; position++)
        {
            foreach (var separator in separators.Where(separator => separator.MemberCount > 0 && separator.Order + separator.MemberCount == position))
                if (!separators.Any(next => next.Order == position)) yield return (null, new("未分组", position, MemberCount: 0));
            foreach (var separator in separators.Where(separator => separator.Order == position)) yield return (null, separator);
            if (position < profile.Mods.Count && separators.Any(separator => separator.Order == position && separator.MemberCount == 0)
                && !separators.Any(separator => separator.Order == position && separator.MemberCount != 0))
                yield return (null, new("未分组", position, MemberCount: 0));
            if (position < profile.Mods.Count) yield return (profile.Mods[position], null);
        }
    }

    internal static string UniqueName(string name, HashSet<string> names)
    {
        var candidate = name;
        for (var number = 2; names.Contains(candidate); number++) candidate = $"{name} ({number})";
        names.Add(candidate);
        return candidate;
    }

    internal static async Task CopyDirectoryAsync(string source, string target, CancellationToken token)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("模组目录包含链接，无法复制为独立迁移副本。");
            Directory.CreateDirectory(MigrationJson.Inside(target, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("模组文件包含链接，无法复制为独立迁移副本。");
            await CopyFileAsync(file, MigrationJson.Inside(target, Path.GetRelativePath(source, file)), token);
        }
    }

    internal static async Task CopyFileAsync(string source, string target, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, token);
    }

    internal static async Task SaveManifestAsync(ExternalMigrationMod mod, string target, CancellationToken token, Guid? nativeGuid = null)
    {
        var path = Path.Combine(target, "manifest.json");
        var original = File.Exists(path) ? await ManifestPackage.ReadAsync(path, token) : new JsonObject();
        var manifest = JsonSerializer.Deserialize<ModManifest>(JsonSerializer.Serialize(mod.Manifest))!;
        if (nativeGuid.HasValue)
        {
            original["HD2MimntOriginalGuid"] = manifest.Guid.ToString();
            manifest.Guid = nativeGuid.Value;
        }
        if (mod.ImagePath != null)
        {
            var image = "hd2mimnt-preview" + Path.GetExtension(mod.ImagePath);
            await CopyFileAsync(mod.ImagePath, Path.Combine(target, image), token);
            manifest.IconPath = image;
        }
        new ManifestParser().SaveToFile(manifest, path, original);
    }
}

/// <summary>所有文件先在临时目录准备；提交保留备份并在失败时回滚本次新增文件及数据。</summary>
internal sealed class ExternalExportSession : IAsyncDisposable
{
    private readonly ExternalMigrationPlan _plan;
    private readonly List<(string Stage, string Target, bool Replace)> _directories = [];
    private readonly List<(string Stage, string Target)> _files = [];
    private readonly List<string> _publishedDirectories = [];
    private readonly List<(string Target, string Backup)> _replacedDirectories = [];
    private readonly List<(string Target, string? Backup)> _publishedFiles = [];
    private bool _committed;
    internal string StageRoot { get; }
    internal ExternalMigrationResult Result { get; } = new();

    internal ExternalExportSession(ExternalMigrationPlan plan)
    {
        _plan = plan;
        StageRoot = Path.Combine(ExternalExportSupport.DataRoot(plan.Source), ".hd2mimnt-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(StageRoot);
    }

    internal async Task<string?> PrepareModAsync(ExternalMigrationMod mod, string target, CancellationToken token, bool allowReplace = false)
    {
        if (Directory.Exists(target))
        {
            var marker = Path.Combine(target, ".hd2mimnt-export.json");
            var metadata = await ExternalExportSupport.ReadObjectAsync(marker, token);
            if (metadata["source"]?.GetValue<string>() == mod.SourceId && metadata["hash"]?.GetValue<string>() == mod.ContentHash)
            { Result.ReusedMods++; return null; }
            if (!allowReplace) throw new IOException($"目标模组目录已存在且不是本次迁移副本：{target}");
        }
        var stage = Path.Combine(StageRoot, "mods", ExternalExportSupport.ModId(mod));
        await ExternalExportSupport.CopyDirectoryAsync(mod.SourcePath, stage, token);
        if (await ModDirectoryHash.CalculateAsync(stage, token) != mod.ContentHash)
            throw new IOException($"模组文件在预览后发生变化，请重新预览：{mod.Manifest.Name}");
        _directories.Add((stage, target, allowReplace));
        Result.ImportedMods++;
        return stage;
    }

    internal async Task MarkModAsync(ExternalMigrationMod mod, string stage, CancellationToken token)
        => await File.WriteAllTextAsync(Path.Combine(stage, ".hd2mimnt-export.json"),
            new JsonObject { ["source"] = mod.SourceId, ["hash"] = mod.ContentHash }.ToJsonString(), token);

    internal string AddFile(string target)
    {
        var stage = Path.Combine(StageRoot, "files", _files.Count.ToString());
        Directory.CreateDirectory(Path.GetDirectoryName(stage)!);
        _files.Add((stage, target));
        return stage;
    }

    internal async Task CommitAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ExternalExportSupport.EnsureStopped(_plan.Source);
        foreach (var (path, expected) in _plan.TargetFileHashes)
            if (await ExternalExportSupport.FileHashAsync(path, token) != expected)
                throw new IOException("目标管理器的数据在预览后发生变化，请重新预览后迁移。");
        var backups = new Dictionary<string, string?>();
        foreach (var file in _files)
        {
            string? backup = null;
            if (File.Exists(file.Target))
            {
                backup = file.Target + ".hd2mimnt-backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N");
                File.Copy(file.Target, backup);
                Result.BackupPaths.Add(backup);
            }
            backups.Add(file.Target, backup);
        }
        foreach (var directory in _directories)
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(directory.Target)!);
            if (Directory.Exists(directory.Target))
            {
                if (!directory.Replace) throw new IOException("目标模组目录在迁移期间发生变化。");
                var backup = Path.Combine(Directory.GetParent(_plan.TargetModsPath)!.FullName, "migration-backups", Path.GetFileName(StageRoot), Path.GetFileName(directory.Target));
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                Directory.Move(directory.Target, backup);
                _replacedDirectories.Add((directory.Target, backup)); Result.BackupPaths.Add(backup);
            }
            try { Directory.Move(directory.Stage, directory.Target); }
            catch (IOException) when (Path.GetPathRoot(directory.Stage) != Path.GetPathRoot(directory.Target))
            {
                _publishedDirectories.Add(directory.Target);
                await ExternalExportSupport.CopyDirectoryAsync(directory.Stage, directory.Target, token);
                continue;
            }
            _publishedDirectories.Add(directory.Target);
        }
        // 完成可取消准备后，一次发布元数据；失败按备份回滚。
        token.ThrowIfCancellationRequested();
        foreach (var file in _files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file.Target)!);
            var sibling = file.Target + ".hd2mimnt-pending-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(file.Stage, sibling); File.Move(sibling, file.Target, true);
                _publishedFiles.Add((file.Target, backups[file.Target]));
            }
            finally { if (File.Exists(sibling)) File.Delete(sibling); }
        }
        _committed = true;
    }

    public ValueTask DisposeAsync()
    {
        if (!_committed)
        {
            foreach (var file in _publishedFiles.AsEnumerable().Reverse())
            {
                if (file.Backup != null) File.Copy(file.Backup, file.Target, true);
                else if (File.Exists(file.Target)) File.Delete(file.Target);
            }
            foreach (var path in _publishedDirectories.AsEnumerable().Reverse()) if (Directory.Exists(path)) Directory.Delete(path, true);
            foreach (var directory in _replacedDirectories.AsEnumerable().Reverse()) Directory.Move(directory.Backup, directory.Target);
        }
        if (Directory.Exists(StageRoot)) Directory.Delete(StageRoot, true);
        return ValueTask.CompletedTask;
    }
}
