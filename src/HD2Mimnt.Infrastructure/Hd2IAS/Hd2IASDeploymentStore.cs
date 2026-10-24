using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Infrastructure.Deployment;

namespace HD2Mimnt.Infrastructure.Hd2IAS;

/// <summary>运行时文件的独立所有权、真实旧文件及恢复日志；data 仅生成库导入目录。</summary>
public sealed partial class Hd2IASDeploymentStore(string storagePath, string? updateCachePath = null,
    IHd2IASPackageReader? packages = null)
{
    public const string PatchPrefix = "9ba626afa44a3aa3";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CopyDeploymentMethod _copy = new();
    public async Task<IDisposable> AcquireAsync(CancellationToken token = default)
    { await _gate.WaitAsync(token); return new Lease(_gate); }
    private sealed class Lease(SemaphoreSlim gate) : IDisposable { public void Dispose() => gate.Release(); }

    public sealed record OwnedFile(string Target, string Sha256);
    public sealed record Installation(string Revision, string Version, Hd2IASRoute Route, string Module,
        string Identity, string SourceSha256, string ResourceId, IReadOnlyList<OwnedFile> Files,
        IReadOnlyList<InventoryFile> Inventory)
    { public bool IsUninstalled { get; init; } }
    public sealed record InventoryFile(string Target, string? BeforeSha256, string? AfterSha256, string Disposition);
    public sealed record Change(string Target, string? Before, string? After);
    public sealed record Journal(string Revision, IReadOnlyList<Change> Changes, int StartedCount = 0);

    private string Root(string game) => Path.Combine(storagePath, Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(game)).ToUpperInvariant()))));
    public async Task<Installation?> ReadAsync(string game, CancellationToken token = default)
    {
        var state = await ReadStateAsync(game, token);
        return state is { IsUninstalled: false } ? state : null;
    }
    private async Task<Installation?> ReadStateAsync(string game, CancellationToken token)
    {
        var path = Path.Combine(Root(game), "active.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<Installation>(await File.ReadAllTextAsync(path, token))
            ?? throw new InvalidDataException("HD2IAS 安装记录损坏。") : null;
    }

    public async Task<IReadOnlyList<OwnedFile>> ReadRecognizedFilesAsync(string game, CancellationToken token = default)
    {
        var installation = await ReadAsync(game, token);
        var known = installation?.Files.ToList() ?? [];
        // 旧版管理器没有部署/拥有 build.json。只用已校验的当前版本缓存包补足该元数据的认可哈希，
        // 不把旧清册的 PreserveUnclassified 或 JSON 中自报的版本视为所有权证据。
        if (installation == null || known.Any(file => file.Target.Equals(PackageManifestReader.BuildTarget, StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(updateCachePath) || !Version.TryParse(installation.Version, out _) ||
            !installation.Version.All(character => char.IsAsciiDigit(character) || character == '.')) return known;
        var cached = Path.Combine(updateCachePath, $"HD2IAS-{installation.Version}-update.zip");
        if (!File.Exists(cached)) return known;
        var package = await (packages ?? new PackageManifestReader()).ReadAsync(cached, token);
        if (!package.IsValid || package.Version != installation.Version) return known;
        var build = package.ForRoute(installation.Route).Files.SingleOrDefault(file => file.Path.Equals(
            PackageManifestReader.BuildFile, StringComparison.OrdinalIgnoreCase));
        if (build != null)
            known.AddRange(build.PreviousSha256.Prepend(build.Sha256).Select(hash => new OwnedFile(PackageManifestReader.BuildTarget, hash)));
        return known;
    }

    public async Task<Hd2IASGeneratedPackage> InstallAsync(string zipPath, string game, Hd2IASInspection inspection,
        byte[] source, IProgress<string>? progress, CancellationToken token)
    {
        var manifest = inspection.Package.ForRoute(inspection.SelectedRoute);
        var other = inspection.Package.ForRoute(manifest.Route == Hd2IASRoute.Lua ? Hd2IASRoute.Native : Hd2IASRoute.Lua);
        var previous = await ReadAsync(game, token);
        var recognized = await ReadRecognizedFilesAsync(game, token);
        var root = Root(game);
        var revision = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(root, "versions", revision);
        var payload = Path.Combine(directory, "payload");
        var changes = new List<Change>();
        var owned = new List<OwnedFile>();
        var inventory = new List<InventoryFile>();
        var committed = false;
        try
        {
            Directory.CreateDirectory(payload);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var prefix = manifest.Route == Hd2IASRoute.Lua ? "RuntimePackage/" : "RuntimePackage/Native/";
                foreach (var (file, entry) in manifest.Files.Where(file => file.Target != null).Select(file => (file, prefix + file.Path))
                    .Concat(inspection.Package.Foundation.Files.Select(file => (file, "Foundation/" + file.Path))))
                {
                    token.ThrowIfCancellationRequested();
                    var relative = file.Target!;
                    if (relative.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("运行时安装不能直接写游戏 data 目录，请通过模组管理。");
                    var current = await HashAsync(Target(game, relative), token);
                    if (current != null && InstalledBuildReader.PreserveExisting(relative))
                    { inventory.Add(new(relative, current, current, "PreserveUserData")); continue; }
                    // 路线切换只额外接受同包另一条路线的明确载荷哈希。
                    var alternate = other.Files.FirstOrDefault(item => item.Target?.Equals(relative, StringComparison.OrdinalIgnoreCase) == true);
                    if (current != null && current != file.Sha256 && !file.PreviousSha256.Contains(current, StringComparer.OrdinalIgnoreCase)
                        && current != alternate?.Sha256 && !recognized.Any(item => item.Target.Equals(relative,
                            StringComparison.OrdinalIgnoreCase) && item.Sha256.Equals(current, StringComparison.OrdinalIgnoreCase)))
                        throw new IOException($"来源未知，不能覆盖：{relative}");
                    if (owned.Any(item => item.Target.Equals(relative, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException($"系统目标重复：{relative}");
                    var staged = Target(payload, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                    await using (var input = (zip.GetEntry(entry) ?? throw new InvalidDataException($"Missing {entry}")).Open())
                    await using (var output = File.Create(staged)) await input.CopyToAsync(output, token);
                    if (await HashAsync(staged, token) != file.Sha256) throw new InvalidDataException($"载荷已改变：{entry}");
                    owned.Add(new(relative, file.Sha256));
                    inventory.Add(new(relative, current, file.Sha256, current == null ? "InstallMissing" : current == file.Sha256 ? "HashMatches" : "ReplaceRecognized"));
                    if (current != file.Sha256) changes.Add(new(relative, current, file.Sha256));
                }
            }
            // 退役文件和上一条路线的专用文件均需有明确旧哈希，不能遗留第二套运行时。
            var retired = manifest.RetiredFiles.Select(file => (Target: file.Target!, Hashes: file.PreviousSha256))
                .Concat(other.Files.Where(file => file.Target != null).Select(file => (Target: file.Target!, Hashes: (IReadOnlyList<string>)[file.Sha256])))
                .Concat(previous?.Files.Select(file => (Target: file.Target, Hashes: (IReadOnlyList<string>)[file.Sha256])) ?? []);
            foreach (var group in retired.GroupBy(item => item.Target, StringComparer.OrdinalIgnoreCase))
            {
                if (owned.Any(file => file.Target.Equals(group.Key, StringComparison.OrdinalIgnoreCase)) ||
                    group.Key.StartsWith("data/", StringComparison.OrdinalIgnoreCase) || PreserveOnUninstall(group.Key)) continue;
                var current = await HashAsync(Target(game, group.Key), token);
                if (current == null) continue;
                if (!group.SelectMany(item => item.Hashes).Contains(current, StringComparer.OrdinalIgnoreCase))
                    throw new IOException($"退役文件来源未知：{group.Key}");
                changes.Add(new(group.Key, current, null));
                inventory.Add(new(group.Key, current, null, "RemoveRecognized"));
            }

            var dataDirectory = Path.Combine(directory, "data-mod");
            await Hd2IASDataDirectoryWriter.WriteAsync(dataDirectory, manifest, source, token);
            var settingsDirectory = Path.Combine(game, "bin", "HD2IndependentRig");
            var extraFiles = Directory.Exists(settingsDirectory) ? Directory.EnumerateFiles(settingsDirectory, "*", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }) : [];
            foreach (var path in extraFiles.Concat(new[] { Path.Combine(game, "bin", "HD2IndependentRig.build.json") }.Where(File.Exists)))
            {
                var relative = Path.GetRelativePath(game, path).Replace('\\', '/');
                if (inventory.Any(file => file.Target.Equals(relative, StringComparison.OrdinalIgnoreCase))) continue;
                var hash = await HashAsync(Target(game, relative), token);
                inventory.Add(new(relative, hash, hash, InstalledBuildReader.PreserveExisting(relative) ? "PreserveUserData" : "PreserveUnclassified"));
            }
            foreach (var file in inventory) progress?.Report($"交叉对比：{Describe(file.Disposition)} · {file.Target}");
            if (changes.Count == 0 && previous?.Version == manifest.Version && previous.Route == manifest.Route && previous.Identity == manifest.IndependentLua.Identity
                && Directory.Exists(Path.Combine(root, "versions", previous.Revision, "data-mod")))
                return new(Path.Combine(root, "versions", previous.Revision, "data-mod"), previous.Version, previous.Route,
                    previous.Module, previous.Identity, previous.SourceSha256);
            var installation = new Installation(revision, manifest.Version, manifest.Route, manifest.IndependentLua.Module,
                manifest.IndependentLua.Identity, manifest.IndependentLua.SourceSha256, manifest.IndependentLua.ResourceId, owned, inventory);
            await CommitAsync(game, installation, changes, progress, "安装", token);
            committed = true;
            return new(dataDirectory, manifest.Version, manifest.Route, manifest.IndependentLua.Module,
                manifest.IndependentLua.Identity, manifest.IndependentLua.SourceSha256);
        }
        finally
        {
            // 失败且恢复完成才清理暂存；恢复失败保留日志及真实备份供重试。
            if (!committed && !File.Exists(Path.Combine(root, "pending.json")) && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    public async Task RecoverAsync(string game, CancellationToken token = default)
    {
        var root = Root(game); var pending = Path.Combine(root, "pending.json");
        if (!File.Exists(pending)) return;
        var journal = JsonSerializer.Deserialize<Journal>(await File.ReadAllTextAsync(pending, token))
            ?? throw new InvalidDataException("HD2IAS 恢复日志损坏。");
        if (!Guid.TryParseExact(journal.Revision, "N", out _)) throw new InvalidDataException("HD2IAS 恢复版本无效。");
        var directory = Path.Combine(root, "versions", journal.Revision);
        var committed = (await ReadStateAsync(game, token))?.Revision == journal.Revision;
        if (!committed)
            foreach (var change in journal.Changes.Take(journal.StartedCount).Reverse())
            {
                var target = Target(game, change.Target);
                if (await HashAsync(target, token) == change.Before) continue;
                if (change.Before == null) File.Delete(target);
                else
                {
                    var backup = Target(Path.Combine(directory, "before"), change.Target);
                    if (await HashAsync(backup, token) != change.Before) throw new IOException($"HD2IAS 恢复备份损坏：{change.Target}");
                    await CopyAsync(backup, target, token);
                }
            }
        File.Delete(pending);
        if (!committed && Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private async Task CommitAsync(string game, Installation installation, IReadOnlyList<Change> changes,
        IProgress<string>? progress, string operation, CancellationToken token, IReadOnlyDictionary<string, byte[]>? preparedFiles = null)
    {
        var root = Root(game); var directory = Path.Combine(root, "versions", installation.Revision);
        var committed = false; var journalWritten = false;
        try
        {
            Directory.CreateDirectory(directory);
            if (preparedFiles != null)
                foreach (var (relative, contents) in preparedFiles)
                {
                    var staged = Target(Path.Combine(directory, "payload"), relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                    await File.WriteAllBytesAsync(staged, contents, token);
                }
            await WriteJsonAsync(Path.Combine(directory, "installation.json"), installation, token);
            foreach (var change in changes.Where(change => change.Before != null))
            {
                var backup = Target(Path.Combine(directory, "before"), change.Target);
                await CopyAsync(Target(game, change.Target), backup, token);
                if (await HashAsync(backup, token) != change.Before) throw new IOException($"文件在预检后改变：{change.Target}");
            }
            var journal = new Journal(installation.Revision, changes);
            await WriteJsonAsync(Path.Combine(root, "pending.json"), journal, token); journalWritten = true;
            foreach (var change in changes)
                if (await HashAsync(Target(game, change.Target), token) != change.Before)
                    throw new IOException($"文件在操作前改变：{change.Target}");
            foreach (var change in changes)
            {
                token.ThrowIfCancellationRequested();
                journal = journal with { StartedCount = journal.StartedCount + 1 };
                await WriteJsonAsync(Path.Combine(root, "pending.json"), journal, token);
                progress?.Report($"{operation}：{change.Target}");
                token.ThrowIfCancellationRequested();
                var target = Target(game, change.Target);
                if (change.After == null) File.Delete(target);
                else await CopyAsync(Target(Path.Combine(directory, "payload"), change.Target), target, token);
                if (await HashAsync(target, token) != change.After) throw new IOException($"操作后哈希不符：{change.Target}");
            }
            token.ThrowIfCancellationRequested();
            await WriteJsonAsync(Path.Combine(root, "active.json"), installation, token);
            committed = true;
            File.Delete(Path.Combine(root, "pending.json"));
        }
        catch
        {
            if (!committed && journalWritten) await RecoverAsync(game, CancellationToken.None);
            throw;
        }
        finally
        {
            if (!committed && !File.Exists(Path.Combine(root, "pending.json")) && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private async Task CopyAsync(string from, string to, CancellationToken token)
    {
        var copied = await _copy.DeployFileAsync(from, to, cancellationToken: token);
        token.ThrowIfCancellationRequested();
        if (!copied) throw new IOException($"复制失败：{to}");
    }
    private static string Describe(string disposition) => disposition switch
    {
        "PreserveUserData" => "保留用户数据", "PreserveUnclassified" => "保留未分类文件",
        "InstallMissing" => "补齐缺失文件", "HashMatches" => "哈希相同，跳过",
        "ReplaceRecognized" => "替换已确认的系统文件", "RemoveRecognized" => "清理已确认的退役文件",
        _ => disposition
    };
    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken token)
    {
        var temporary = path + ".tmp";
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value), token); token.ThrowIfCancellationRequested(); File.Move(temporary, path, true); }
        finally { File.Delete(temporary); }
    }
    private static string Target(string root, string relative)
    {
        var path = Path.GetFullPath(PackageManifestReader.SafePath(relative), root);
        var current = path;
        while (!current.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), StringComparison.OrdinalIgnoreCase))
        {
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"HD2IAS 目标包含链接，不能自动替换：{relative}");
            current = Path.GetDirectoryName(current)!;
        }
        return path;
    }
    private static async Task<string?> HashAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }
}
